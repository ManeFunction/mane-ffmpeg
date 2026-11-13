//! The script commands; see `debug_automation`.

use crate::debug_automation::Automation;
use crate::project::ProjectWindow;
use mane_core::media::source_catalog::SourceStatus;
use mane_core::model::time;
use std::time::{Duration, Instant};

pub fn step(a: &mut Automation, w: &mut ProjectWindow, ctx: &egui::Context) {
    let c = a.cursor();
    if let Some(until) = *c.wait_until {
        if Instant::now() < until {
            ctx.request_repaint_after(until - Instant::now());
            return;
        }
        *c.wait_until = None;
    }
    if let Some(what) = c.waiting_for.clone() {
        let done = match what.as_str() {
            "ready" => !w.sources.is_empty() && w.sources.iter().all(|s| s.status != SourceStatus::Analyzing),
            "idle" => !w.export.is_exporting,
            _ => true,
        };
        let timed_out = c.wait_started.is_some_and(|s| s.elapsed() > Duration::from_secs(60));
        if !done && !timed_out {
            ctx.request_repaint_after(Duration::from_millis(100));
            return;
        }
        *c.waiting_for = None;
    }
    while *c.next < c.lines.len() {
        let line = c.lines[*c.next].trim().to_string();
        *c.next += 1;
        if line.is_empty() || line.starts_with('#') {
            continue;
        }
        println!("[script] {line}");
        let (cmd, arg) = line.split_once(' ').map(|(a, b)| (a.to_string(), b.trim().to_string())).unwrap_or((line.clone(), String::new()));
        match cmd.as_str() {
            "wait" => {
                *c.wait_until = Some(Instant::now() + Duration::from_secs_f64(arg.parse().unwrap_or(1.0)));
                ctx.request_repaint_after(Duration::from_millis(10));
                return;
            }
            "waitfor" => {
                *c.waiting_for = Some(arg);
                *c.wait_started = Some(Instant::now());
                ctx.request_repaint_after(Duration::from_millis(10));
                return;
            }
            "add" => w.add_media(vec![arg]),
            "addaudio" => w.add_audio(vec![arg]),
            "open" => w.app.send(crate::shared::AppCommand::OpenProject { path: arg, requester: Some(w.id) }),
            "seek" => {
                let t = time::try_parse(&arg).unwrap_or_default();
                w.seek_to(t, false);
            }
            "play" => w.play_pause(),
            "split" => w.split(),
            "delete" => w.delete_selection(),
            "selectall" => w.select_all(),
            "select" => {
                if let Some(c) = arg.parse::<usize>().ok().and_then(|i| w.state().timeline.clips().get(i).cloned()) {
                    w.timeline.select_only(&c.id);
                }
            }
            "offset" => w.set_audio_offset_text(&arg),
            "nudge" => w.nudge_audio(mane_core::model::Time::from_ms(arg.parse().unwrap_or(10))),
            "undo" => w.undo(),
            "redo" => w.redo(),
            "compact" => w.set_compact(arg != "off"),
            "crop" => {
                let v: Vec<f64> = arg.split_whitespace().filter_map(|x| x.parse().ok()).collect();
                if v.len() == 4 {
                    w.commit_crop(Some(mane_core::model::CropRect::new(v[0], v[1], v[2], v[3])));
                }
            }
            "cropmode" => w.toggle_crop(),
            "preset" => {
                let id = w.export.presets.iter().find(|p| p.name == arg || p.id == arg).map(|p| p.id.clone());
                if let Some(id) = id {
                    w.select_preset(&id);
                }
            }
            "output" => w.set_output_path(Some(arg)),
            "export" => w.export(),
            "save" => {
                w.file_path = Some(arg);
                w.save(false, |_, _| {});
            }
            "presets" => w.show_presets(),
            "newwindow" => w.app.send(crate::shared::AppCommand::NewWindow { from: Some(w.id) }),
            "close" => ctx.send_viewport_cmd(egui::ViewportCommand::Close),
            "settings" => w.show_settings(),
            "snapshot" => {
                let path = arg.clone();
                ctx.send_viewport_cmd(egui::ViewportCommand::Screenshot(egui::ScreenshotCallback::new(move |image| {
                    save_png(&path, &image)
                })));
                // The picture is taken at the end of this frame: nothing else changes before it.
                *c.wait_until = Some(Instant::now() + Duration::from_millis(300));
                ctx.request_repaint_after(Duration::from_millis(10));
                return;
            }
            "inline" => {
                // "inline presets": that dialog is drawn in the window; "inline" alone goes back.
                w.inline_dialog = (!arg.is_empty()).then_some(arg);
            }
            "snapshotof" => {
                // A dialog window: "snapshotof presets /tmp/p.png".
                let (which, path) = arg.split_once(' ').map(|(a, b)| (a.to_string(), b.to_string())).unwrap_or_default();
                // Taken from inside that window's own frame.
                w.dialog_shot = Some((which, egui::ScreenshotCallback::new(move |image| save_png(&path, &image))));
                *c.wait_until = Some(Instant::now() + Duration::from_millis(500));
                ctx.request_repaint_after(Duration::from_millis(10));
                return;
            }
            "state" => print_state(w),
            "pixel" => {
                println!("[pixel] {}", w.preview.renderer.as_ref().map(|r| r.center_pixel()).unwrap_or_else(|| "no renderer".into()))
            }
            "glyphs" => {
                for (name, font) in [("regular", crate::theme::regular(12.0)), ("semibold", crate::theme::semibold(12.0))] {
                    let missing: String = ctx.fonts_mut(|f| arg.chars().filter(|c| !f.has_glyph(&font, *c)).collect());
                    println!("[glyphs] {name} missing: {missing:?}");
                }
            }
            "quit" => {
                w.close_confirmed = true;
                w.app.send(crate::shared::AppCommand::ForceQuit);
                return;
            }
            other => println!("[script] unknown command {other}"),
        }
    }
}

fn print_state(w: &ProjectWindow) {
    let s = w.state();
    println!(
        "[state] sources={} audio={} clips={} duration={} playhead={} selection={} crop={:?} compact={} exporting={} status={:?} export={:?} preview={:?} rendered={}",
        s.sources.len(),
        s.audio_files.len(),
        s.timeline.clips().len(),
        time::display(s.timeline.duration(), false),
        time::display(w.timeline.playhead, false),
        w.timeline.selection.len(),
        s.crop(),
        w.preview.is_compact,
        w.export.is_exporting,
        w.status_message,
        w.export.status,
        w.preview.message,
        w.preview.renderer.as_ref().map(|r| r.rendered_frames()).unwrap_or(0),
    );
    for (i, c) in s.timeline.clips().iter().enumerate() {
        println!(
            "[clip {i}] {} {}–{} offset={} replaced={}",
            s.source(&c.source_id).map(|x| x.display_name()).unwrap_or_default(),
            time::display(c.r#in, false),
            time::display(c.out, false),
            time::offset(c.active_audio_offset()),
            c.replaced_audio.is_some()
        );
    }
}

fn save_png(path: &str, image: &egui::ColorImage) {
    let Ok(file) = std::fs::File::create(path) else { return };
    let mut encoder = png::Encoder::new(std::io::BufWriter::new(file), image.size[0] as u32, image.size[1] as u32);
    encoder.set_color(png::ColorType::Rgba);
    encoder.set_depth(png::BitDepth::Eight);
    let Ok(mut writer) = encoder.write_header() else { return };
    let bytes: Vec<u8> = image.pixels.iter().flat_map(|c| c.to_array()).collect();
    let _ = writer.write_image_data(&bytes);
    println!("[script] saved {path}");
}
