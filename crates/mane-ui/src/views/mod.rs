//! A project window: the same arrangement as the Avalonia MainWindow — menu, export bar along the bottom, the timeline
//! above it, the Files panel and Selection inspector on the left and the preview on the right.

pub mod export_bar;
pub mod files;
pub mod inspector;
pub mod preview;
pub mod timeline;

use crate::logic::audio_drag::AudioDragEffect;
use crate::logic::timeline_gestures::TimelineZone;
use crate::logic::timeline_layout as tl;
use crate::project::{ProjectWindow, file_name, is_project, looks_like_audio};
use crate::shared::AppCommand;
use crate::theme;
use egui::{Key, KeyboardShortcut, Modifiers, RichText, ViewportCommand};
use mane_core::model::{FileKind, Time};
use std::path::Path;

const COMPACT_TOP_HEIGHT: f32 = 330.0;
const NORMAL_MIN_HEIGHT: f32 = 640.0;
const COMPACT_MIN_HEIGHT: f32 = 470.0;
const MIN_WIDTH: f32 = 960.0;

fn cmd() -> Modifiers {
    Modifiers::COMMAND
}

fn shortcut(m: Modifiers, k: Key) -> KeyboardShortcut {
    KeyboardShortcut::new(m, k)
}

/// Draws the whole window and handles its input. `frame` gives the GPU for the video.
pub fn window_ui(w: &mut ProjectWindow, ui: &mut egui::Ui) {
    // Every window's widgets live under its own id, so windows sharing egui's memory don't share panel sizes.
    let id = w.id;
    ui.push_id(("window", id), |ui| window_body(w, ui));
}

fn window_body(w: &mut ProjectWindow, ui: &mut egui::Ui) {
    let ctx = ui.ctx().clone();
    if let Some(which) = w.inline_dialog.clone() {
        w.logic();
        crate::dialogs::show_inline(w, ui, &which);
        if let Some(mut automation) = w.debug.take() {
            automation.step(w, &ctx);
            w.debug = Some(automation);
        }
        return;
    }
    w.logic();
    track_window(w, &ctx);
    ctx.send_viewport_cmd(ViewportCommand::Title(w.title()));
    handle_close_request(w, &ctx);
    apply_layout(w, &ctx);
    apply_compact(w, &ctx);
    handle_shortcuts(w, &ctx);

    egui::Panel::top("menu")
        .frame(egui::Frame::new().fill(theme::palette(&ctx).window).inner_margin(egui::Margin::symmetric(8, 2)))
        .show(ui, |ui| menu_bar(w, ui));
    let p = theme::palette(&ctx);
    egui::Panel::bottom("export")
        .frame(egui::Frame::new().fill(p.window).inner_margin(egui::Margin { left: 8, right: 8, top: 0, bottom: 8 }))
        .show(ui, |ui| {
            theme::panel_frame(ui.ctx()).inner_margin(egui::Margin::symmetric(10, 8)).show(ui, |ui| export_bar::export_bar(w, ui));
        });
    egui::Panel::bottom("timeline")
        .frame(egui::Frame::new().fill(p.window).inner_margin(egui::Margin { left: 8, right: 8, top: 6, bottom: 6 }))
        .show(ui, |ui| {
            theme::panel_frame(ui.ctx()).inner_margin(0).show(ui, |ui| {
                ui.set_width(ui.available_width());
                timeline_toolbar(w, ui);
                let rect = ui.available_rect_before_wrap();
                w.timeline_rect = Some(egui::Rect::from_min_size(rect.min, egui::vec2(rect.width(), tl::HEIGHT as f32)));
                timeline::timeline_view(w, ui);
            });
        });
    egui::CentralPanel::default()
        .frame(egui::Frame::new().fill(p.window).inner_margin(egui::Margin { left: 8, right: 8, top: 8, bottom: 0 }))
        .show(ui, |ui| {
            if w.preview.is_compact {
                // The video list and the inspector side by side.
                egui::Panel::right("inspector-compact")
                    .exact_size(338.0)
                    .frame(egui::Frame::new().inner_margin(egui::Margin { left: 8, ..Default::default() }))
                    .show(ui, |ui| {
                        theme::panel_frame(ui.ctx()).inner_margin(10).show(ui, |ui| {
                            ui.set_min_size(ui.available_size());
                            egui::ScrollArea::vertical().id_salt("inspector").show(ui, |ui| inspector::inspector(w, ui));
                        });
                    });
                let h = ui.available_height();
                files_card(w, ui, h);
            } else {
                let left = egui::Panel::left("left")
                    .resizable(true)
                    .default_size(w.left_pane_width)
                    .size_range(220.0..=600.0)
                    .frame(egui::Frame::new())
                    .show(ui, |ui| {
                        egui::Panel::bottom("inspector")
                            .resizable(false)
                            .frame(egui::Frame::new().inner_margin(egui::Margin { left: 0, right: 0, top: 8, bottom: 0 }))
                            .show(ui, |ui| {
                                theme::panel_frame(ui.ctx()).inner_margin(10).show(ui, |ui| {
                                    ui.set_width(ui.available_width());
                                    inspector::inspector(w, ui);
                                });
                            });
                        let h = ui.available_height();
                        files_card(w, ui, h);
                    });
                w.left_pane_width = left.response.rect.width();
                ui.add_space(0.0);
                egui::CentralPanel::default().frame(egui::Frame::new().inner_margin(egui::Margin { left: 6, ..Default::default() })).show(
                    ui,
                    |ui| {
                        theme::panel_frame(ui.ctx()).inner_margin(egui::Margin { left: 0, right: 0, top: 6, bottom: 0 }).show(ui, |ui| {
                            ui.set_min_size(ui.available_size());
                            preview::preview_panel(w, ui);
                        });
                    },
                );
            }
        });
    audio_drag(w, &ctx);
    handle_drops(w, &ctx);
    modals(w, &ctx);
    crate::dialogs::show(w, &ctx);
    if let Some(mut automation) = w.debug.take() {
        automation.step(w, &ctx);
        w.debug = Some(automation);
    }
    // Keep painting while playing or exporting so the playhead and progress move.
    if w.preview.is_playing || w.export.is_exporting {
        ctx.request_repaint_after(std::time::Duration::from_millis(50));
    }
}

fn files_card(w: &mut ProjectWindow, ui: &mut egui::Ui, height: f32) {
    let r = theme::panel_frame(ui.ctx()).show(ui, |ui| {
        ui.set_min_height((height - 18.0).max(120.0));
        ui.set_width(ui.available_width());
        files::files_panel(w, ui);
    });
    w.files_rect = Some(r.response.rect);
}

fn timeline_toolbar(w: &mut ProjectWindow, ui: &mut egui::Ui) {
    // The right-hand group needs about this much room; with less, it moves to a row of its own.
    const RIGHT_WIDTH: f32 = 360.0;
    egui::Frame::new().inner_margin(egui::Margin::symmetric(8, 6)).show(ui, |ui| {
        let mut wrapped = false;
        ui.horizontal(|ui| {
            if w.preview.is_compact {
                preview::play_button(w, ui, 40.0);
                ui.label(RichText::new(w.position_text()).monospace().size(11.5));
                ui.add_space(8.0);
            }
            if ui.add(theme::tool_button("Split (S)")).clicked() {
                w.split();
            }
            if ui.add(theme::tool_button("Delete")).clicked() {
                w.delete_selection();
            }
            if ui.add_enabled(w.history.can_undo(), theme::tool_button("Undo")).clicked() {
                w.undo();
            }
            if ui.add_enabled(w.history.can_redo(), theme::tool_button("Redo")).clicked() {
                w.redo();
            }
            ui.add_space(10.0);
            let mut snap = w.snap_to_keyframes;
            if ui
                .checkbox(&mut snap, RichText::new("Snap to keyframes").size(12.0))
                .on_hover_text("Show keyframes and split on them — needed for exact stream-copy cuts")
                .changed()
            {
                w.set_snap_to_keyframes(snap);
            }
            if ui.max_rect().right() - ui.cursor().min.x < RIGHT_WIDTH {
                wrapped = true;
                return;
            }
            toolbar_right(w, ui, true);
        });
        if wrapped {
            ui.horizontal(|ui| toolbar_right(w, ui, true));
        }
    });
}

fn toolbar_right(w: &mut ProjectWindow, ui: &mut egui::Ui, with_status: bool) {
    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
        if ui.add(theme::tool_button("+")).on_hover_text("Zoom in (+)").clicked() {
            w.zoom_in();
        }
        if ui.add(theme::tool_button("Fit")).on_hover_text("Fit timeline").clicked() {
            w.zoom_fit();
        }
        if ui.add(theme::tool_button("−")).on_hover_text("Zoom out (−)").clicked() {
            w.zoom_out();
        }
        ui.add_space(8.0);
        if ui.add(theme::tool_button(w.preview_toggle_text())).on_hover_text("Show or hide the video preview (⇧⌘C)").clicked() {
            w.toggle_compact();
        }
        ui.add_space(8.0);
        theme::dim(ui, w.timeline_duration_text());
        if with_status && ui.available_width() > 40.0 {
            ui.add_space(12.0);
            ui.with_layout(egui::Layout::left_to_right(egui::Align::Center), |ui| {
                ui.add_space(12.0);
                if let Some(s) = &w.status_message {
                    let c = theme::palette(ui.ctx()).dim;
                    ui.add(egui::Label::new(RichText::new(s).size(12.0).color(c)).truncate());
                }
            });
        }
    });
}

// ---- Menu --------------------------------------------------------------------------------------------

fn item(ui: &mut egui::Ui, text: &str, sc: Option<KeyboardShortcut>, enabled: bool) -> bool {
    let mut b = egui::Button::new(text);
    if let Some(sc) = sc {
        b = b.shortcut_text(ui.ctx().format_shortcut(&sc));
    }
    let clicked = ui.add_enabled(enabled, b).clicked();
    if clicked {
        ui.close();
    }
    clicked
}

fn menu_bar(w: &mut ProjectWindow, ui: &mut egui::Ui) {
    let shift_cmd = Modifiers::COMMAND | Modifiers::SHIFT;
    egui::MenuBar::new().ui(ui, |ui| {
        ui.menu_button("File", |ui| {
            if item(ui, "New Window", Some(shortcut(cmd(), Key::N)), true) {
                w.app.send(AppCommand::NewWindow { from: Some(w.id) });
            }
            if item(ui, "Open Project…", Some(shortcut(cmd(), Key::O)), true) {
                w.open_project_dialog();
            }
            ui.menu_button("Open Recent", |ui| {
                let recent: Vec<String> =
                    w.app.settings().current().recent_projects.iter().filter(|p| Path::new(p).is_file()).cloned().collect();
                if recent.is_empty() {
                    ui.add_enabled(false, egui::Button::new("No recent projects"));
                }
                for p in recent {
                    if ui.button(file_name(&p)).on_hover_text(&p).clicked() {
                        w.app.send(AppCommand::OpenProject { path: p.clone(), requester: Some(w.id) });
                        ui.close();
                    }
                }
            });
            ui.separator();
            if item(ui, "Add Videos…", Some(shortcut(cmd(), Key::I)), true) {
                w.pick_media();
            }
            if item(ui, "Add Audio…", Some(shortcut(shift_cmd, Key::I)), true) {
                w.pick_audio();
            }
            ui.separator();
            if item(ui, "Save Project", Some(shortcut(cmd(), Key::S)), true) {
                w.save(false, |_, _| {});
            }
            if item(ui, "Save Project As…", Some(shortcut(shift_cmd, Key::S)), true) {
                w.save(true, |_, _| {});
            }
            ui.separator();
            if item(ui, "Export", Some(shortcut(cmd(), Key::E)), w.export.is_idle()) {
                w.export();
            }
            if item(ui, "Pause Export", None, w.export.is_exporting && !w.export.is_paused) {
                w.pause_export();
            }
            if item(ui, "Resume Export", None, w.export.is_paused) {
                w.resume_export();
            }
            if item(ui, "Cancel Export", None, w.export.is_exporting) {
                w.cancel_export();
            }
            ui.separator();
            if item(ui, "Close Window", Some(shortcut(cmd(), Key::W)), true) {
                ui.ctx().send_viewport_cmd(ViewportCommand::Close);
            }
            if item(ui, "Settings…", Some(shortcut(cmd(), Key::Comma)), true) {
                w.show_settings();
            }
            ui.separator();
            if item(ui, "Quit", Some(shortcut(cmd(), Key::Q)), true) {
                w.app.send(AppCommand::Quit);
            }
        });
        ui.menu_button("Edit", |ui| {
            if item(ui, "Undo", Some(shortcut(cmd(), Key::Z)), w.history.can_undo()) {
                w.undo();
            }
            if item(ui, "Redo", Some(shortcut(shift_cmd, Key::Z)), w.history.can_redo()) {
                w.redo();
            }
            if item(ui, "Select All", Some(shortcut(cmd(), Key::A)), true) {
                w.select_all();
            }
            ui.separator();
            if item(ui, "Split at Playhead", None, true) {
                w.split();
            }
            if item(ui, "Delete Selected Clips", None, true) {
                w.delete_selection();
            }
            ui.separator();
            if item(ui, "Audio 10 ms Earlier", None, true) {
                w.nudge_audio(Time::from_ms(-10));
            }
            if item(ui, "Audio 10 ms Later", None, true) {
                w.nudge_audio(Time::from_ms(10));
            }
            if item(ui, "Audio 1 Frame Earlier", None, true) {
                w.nudge_audio_frames(-1);
            }
            if item(ui, "Audio 1 Frame Later", None, true) {
                w.nudge_audio_frames(1);
            }
            if item(ui, "Reset Audio Offset", None, true) {
                w.reset_audio_offset();
            }
            if item(ui, "Use Original Audio", None, true) {
                w.restore_original_audio();
            }
            ui.separator();
            if item(ui, "Crop Picture", None, w.can_crop() || w.preview.is_cropping) {
                w.toggle_crop();
            }
            if item(ui, "Reset Crop", None, w.has_crop()) {
                w.reset_crop();
            }
        });
        ui.menu_button("View", |ui| {
            let mut compact = w.preview.is_compact;
            let text = format!("Compact Mode    {}", ui.ctx().format_shortcut(&shortcut(shift_cmd, Key::C)));
            if ui.checkbox(&mut compact, text).clicked() {
                w.toggle_compact();
                ui.close();
            }
            ui.separator();
            if item(ui, "Play / Pause", None, true) {
                w.play_pause();
            }
            if item(ui, "Zoom In", Some(shortcut(cmd(), Key::Plus)), true) {
                w.zoom_in();
            }
            if item(ui, "Zoom Out", Some(shortcut(cmd(), Key::Minus)), true) {
                w.zoom_out();
            }
            if item(ui, "Fit Timeline", Some(shortcut(cmd(), Key::Num0)), true) {
                w.zoom_fit();
            }
        });
        ui.menu_button("Presets", |ui| {
            if item(ui, "Edit Presets…", None, true) {
                w.show_presets();
            }
        });
        ui.menu_button("Help", |ui| {
            let busy = w.app.update_busy.load(std::sync::atomic::Ordering::Acquire);
            if item(ui, if busy { "Checking for Update…" } else { "Check for Updates…" }, None, !busy) {
                w.app.send(AppCommand::CheckForUpdates { manual: true });
            }
            if item(ui, "About Mane FFmpeg", None, true) {
                w.app.send(AppCommand::ShowAbout);
            }
        });
    });
}

// ---- Keyboard ----------------------------------------------------------------------------------------

/// Menu shortcuts (work everywhere) and the single-key editor shortcuts (not while typing).
fn handle_shortcuts(w: &mut ProjectWindow, ctx: &egui::Context) {
    if !w.dialogs.is_empty() {
        return;
    }
    let shift_cmd = Modifiers::COMMAND | Modifiers::SHIFT;
    let typing = ctx.egui_wants_keyboard_input();
    let consume = |m: Modifiers, k: Key| ctx.input_mut(|i| i.consume_shortcut(&shortcut(m, k)));
    if consume(shift_cmd, Key::S) {
        w.save(true, |_, _| {});
    } else if consume(cmd(), Key::S) {
        w.save(false, |_, _| {});
    }
    if consume(shift_cmd, Key::I) {
        w.pick_audio();
    } else if consume(cmd(), Key::I) {
        w.pick_media();
    }
    if consume(cmd(), Key::N) {
        w.app.send(AppCommand::NewWindow { from: Some(w.id) });
    }
    if consume(cmd(), Key::O) {
        w.open_project_dialog();
    }
    if consume(cmd(), Key::E) {
        w.export();
    }
    if consume(cmd(), Key::W) {
        ctx.send_viewport_cmd(ViewportCommand::Close);
    }
    if consume(cmd(), Key::Q) {
        w.app.send(AppCommand::Quit);
    }
    if consume(cmd(), Key::Comma) {
        w.show_settings();
    }
    if consume(shift_cmd, Key::C) {
        w.toggle_compact();
    }
    if consume(cmd(), Key::Plus) || consume(cmd(), Key::Equals) {
        w.zoom_in();
    }
    if consume(cmd(), Key::Minus) {
        w.zoom_out();
    }
    if consume(cmd(), Key::Num0) {
        w.zoom_fit();
    }
    if !typing {
        if consume(shift_cmd, Key::Z) {
            w.redo();
        } else if consume(cmd(), Key::Z) {
            w.undo();
        }
        if consume(cmd(), Key::A) {
            w.select_all();
        }
    }
    // Releases end the "held" state that stops auto-repeat.
    let released: Vec<Key> = ctx.input(|i| {
        i.events
            .iter()
            .filter_map(|e| match e {
                egui::Event::Key { key, pressed: false, .. } => Some(*key),
                _ => None,
            })
            .collect()
    });
    for k in released {
        w.keys_down.remove(&k);
    }
    if typing {
        return;
    }
    let presses: Vec<(Key, Modifiers, bool)> = ctx.input(|i| {
        i.events
            .iter()
            .filter_map(|e| match e {
                egui::Event::Key { key, pressed: true, repeat, modifiers, .. } => Some((*key, *modifiers, *repeat)),
                _ => None,
            })
            .collect()
    });
    for (key, m, repeat) in presses {
        if m.command || m.ctrl || m.mac_cmd {
            continue;
        }
        // Holding a toggle key auto-repeats; only the first press counts.
        if matches!(key, Key::Space | Key::K) && (repeat || w.keys_down.contains(&key)) {
            continue;
        }
        let handled = editor_key(w, ctx, key, m.shift, m.alt);
        if handled {
            w.keys_down.insert(key);
            ctx.input_mut(|i| i.consume_key(m, key));
        }
    }
    // Shift alone changes how a scrub snaps.
}

fn editor_key(w: &mut ProjectWindow, _ctx: &egui::Context, key: Key, shift: bool, alt: bool) -> bool {
    match key {
        Key::Space | Key::K => w.play_pause(),
        Key::ArrowLeft if alt => {
            if shift {
                w.nudge_audio_frames(-1)
            } else {
                w.nudge_audio(Time::from_ms(-10))
            }
        }
        Key::ArrowRight if alt => {
            if shift {
                w.nudge_audio_frames(1)
            } else {
                w.nudge_audio(Time::from_ms(10))
            }
        }
        Key::ArrowLeft => {
            if shift {
                w.jump(Time::from_secs(-1))
            } else {
                w.step_frame(true)
            }
        }
        Key::ArrowRight => {
            if shift {
                w.jump(Time::from_secs(1))
            } else {
                w.step_frame(false)
            }
        }
        Key::J => w.jump(Time::from_secs(-5)),
        Key::L => w.jump(Time::from_secs(5)),
        Key::ArrowUp => w.jump_to_cut(true),
        Key::ArrowDown => w.jump_to_cut(false),
        Key::Home => w.go_to_start(),
        Key::End => w.go_to_end(),
        Key::S | Key::B => w.split(),
        Key::Delete | Key::Backspace => w.delete_selection(),
        Key::I if w.can_mark() => w.set_mark_in(),
        Key::O if w.can_mark() => w.set_mark_out(),
        Key::C if !shift && !alt => w.toggle_crop(),
        Key::Enter if w.preview.is_cropping => w.finish_crop(),
        Key::Enter if w.can_mark() => w.add_marked_range(),
        Key::Escape => {
            if w.audio_drag.is_dragging {
                let effects = w.audio_drag.cancel();
                apply_audio_effects(w, effects);
            } else if w.preview.is_cropping {
                w.finish_crop();
            } else if w.is_source_mode() {
                w.back_to_timeline();
            } else {
                w.timeline.clear_selection();
                w.reset_fields();
            }
        }
        Key::Plus | Key::Equals => w.zoom_in(),
        Key::Minus => w.zoom_out(),
        _ => return false,
    }
    true
}

// ---- Dragging an audio file onto a clip ----------------------------------------------------------------

/// The clip under a point of the timeline (either row), or None.
pub fn clip_at(w: &ProjectWindow, p: egui::Pos2) -> Option<String> {
    let rect = w.timeline_rect?;
    if w.timeline.zoom <= 0.0 || !rect.contains(p) {
        return None;
    }
    let (x, y) = ((p.x - rect.min.x) as f64, (p.y - rect.min.y) as f64);
    if x < tl::HEADER {
        return None;
    }
    let hit = tl::hit_test(&w.state().timeline, w.timeline.zoom, w.timeline.scroll, x, y);
    if matches!(hit.zone, TimelineZone::Ruler | TimelineZone::Empty) { None } else { hit.clip_id }
}

fn apply_audio_effects(w: &mut ProjectWindow, effects: Vec<AudioDragEffect>) {
    for e in effects {
        match e {
            AudioDragEffect::ShowDrop(t) => w.timeline.audio_drop = t,
            AudioDragEffect::Status(s) => w.status_message = s,
            AudioDragEffect::Replace { clip_id, file_id } => w.replace_clip_audio(&clip_id, &file_id),
        }
    }
}

fn audio_drag(w: &mut ProjectWindow, ctx: &egui::Context) {
    let (down, pos, origin) = ctx.input(|i| (i.pointer.primary_down(), i.pointer.interact_pos(), i.pointer.press_origin()));
    if down {
        if let (Some(p), Some(o)) = (pos, origin) {
            let distance = ((p.x - o.x).abs() + (p.y - o.y).abs()) as f64;
            let clip = clip_at(w, p);
            let (on, effects) = w.audio_drag.move_to(distance, clip);
            apply_audio_effects(w, effects);
            if on {
                ctx.set_cursor_icon(if w.audio_drag.target_clip.is_some() { egui::CursorIcon::Copy } else { egui::CursorIcon::NoDrop });
            }
        }
    } else {
        let effects = w.audio_drag.release();
        apply_audio_effects(w, effects);
    }
}

// ---- Files dropped from outside ------------------------------------------------------------------------

fn handle_drops(w: &mut ProjectWindow, ctx: &egui::Context) {
    let (hovered, dropped, pointer) = ctx.input(|i| {
        (
            i.raw.hovered_files.iter().filter_map(|f| f.path.as_ref().map(|p| p.to_string_lossy().into_owned())).collect::<Vec<_>>(),
            i.raw.dropped_files.iter().map(|f| f.path().to_string_lossy().into_owned()).filter(|p| !p.is_empty()).collect::<Vec<_>>(),
            i.pointer.latest_pos(),
        )
    });
    // Audio files over a clip replace its sound when dropped: show which clip.
    if !hovered.is_empty() {
        let audio = hovered.iter().find(|p| looks_like_audio(p)).cloned();
        let clip = pointer.and_then(|p| clip_at(w, p));
        w.timeline.audio_drop = match (audio, clip) {
            (Some(a), Some(c)) => Some(crate::logic::audio_drag::AudioDropTarget {
                name: file_name(&a),
                color: w.audio_file_color(&std::path::absolute(&a).map(|p| p.to_string_lossy().into_owned()).unwrap_or(a.clone())),
                clip_id: Some(c),
            }),
            _ => None,
        };
    } else if w.pending_drop.is_none() && !w.audio_drag.is_dragging && w.timeline.audio_drop.as_ref().is_some_and(|_| dropped.is_empty()) {
        w.timeline.audio_drop = None;
    }
    if !dropped.is_empty() {
        w.timeline.audio_drop = None;
        let at = pointer.or_else(|| drop_position(ctx));
        place_drop(w, dropped, at);
    }
}

/// Where the pointer is now, asked of the system: during a drag from Finder the window gets no pointer moves.
fn drop_position(ctx: &egui::Context) -> Option<egui::Pos2> {
    #[cfg(target_os = "macos")]
    {
        crate::platform_mac::pointer_in_window(ctx)
    }
    #[cfg(not(target_os = "macos"))]
    {
        let _ = ctx;
        None
    }
}

fn place_drop(w: &mut ProjectWindow, paths: Vec<String>, at: Option<egui::Pos2>) {
    if !paths.iter().any(|p| is_project(p))
        && let Some(p) = at
    {
        // Onto the Audio tab: everything with sound becomes an audio file, videos too.
        if w.files_tab == FileKind::Audio && w.files_rect.is_some_and(|r| r.contains(p)) {
            w.add_audio(paths);
            return;
        }
        // Onto a clip: audio files are added and the first replaces that clip's sound.
        if let Some(clip) = clip_at(w, p).filter(|_| paths.iter().any(|p| looks_like_audio(p))) {
            w.replace_clip_audio_from_files(paths, clip);
            return;
        }
    }
    w.app.send(AppCommand::OpenFiles { paths, requester: Some(w.id) });
}

// ---- Window ------------------------------------------------------------------------------------------

fn track_window(w: &mut ProjectWindow, ctx: &egui::Context) {
    ctx.input(|i| {
        let v = i.viewport();
        if let Some(r) = v.outer_rect {
            w.outer_rect = Some(r);
        }
        w.maximized = v.maximized.unwrap_or(false);
    });
}

fn handle_close_request(w: &mut ProjectWindow, ctx: &egui::Context) {
    if !ctx.input(|i| i.viewport().close_requested()) {
        return;
    }
    if w.close_confirmed {
        return;
    }
    ctx.send_viewport_cmd(ViewportCommand::CancelClose);
    if w.closing {
        return;
    }
    w.closing = true;
    w.confirm_close(true, |w, ok| {
        w.closing = false;
        if ok {
            w.close_confirmed = true;
            w.app.send(AppCommand::WindowClosed { id: w.id });
        }
    });
}

/// The layout read from a project: window bounds, the left pane and compact mode.
fn apply_layout(w: &mut ProjectWindow, ctx: &egui::Context) {
    if !w.layout_pending {
        return;
    }
    w.layout_pending = false;
    let layout = w.layout.clone();
    if let Some(b) = layout.window {
        ctx.send_viewport_cmd(ViewportCommand::OuterPosition(egui::pos2(b.x as f32, b.y as f32)));
        ctx.send_viewport_cmd(ViewportCommand::InnerSize(egui::vec2(
            (b.width as f32).max(MIN_WIDTH),
            (b.height as f32).max(NORMAL_MIN_HEIGHT),
        )));
        if b.maximized {
            ctx.send_viewport_cmd(ViewportCommand::Maximized(true));
        }
        w.expanded_height = Some(b.height as f32);
    }
    w.left_pane_width = (layout.left_pane_width as f32).clamp(220.0, 600.0);
    ctx.memory_mut(|m| m.data.remove::<egui::containers::panel::PanelState>(egui::Id::unique("left")));
    w.set_compact(layout.compact);
    // Compact mode shrinks from the full-size bounds just set.
    w.compact_applied = Some(false);
}

/// Hides the preview, puts the video list and inspector side by side and shrinks the window to fit; turning it off
/// restores the previous height. The window changes height at its top edge, so the timeline and export controls
/// along the bottom stay where they are on screen.
fn apply_compact(w: &mut ProjectWindow, ctx: &egui::Context) {
    let compact = w.preview.is_compact;
    let previous = w.compact_applied.replace(compact);
    if previous == Some(compact) || previous.is_none() && !compact {
        return;
    }
    let min = if compact { COMPACT_MIN_HEIGHT } else { NORMAL_MIN_HEIGHT };
    ctx.send_viewport_cmd(ViewportCommand::MinInnerSize(egui::vec2(MIN_WIDTH, min)));
    let (Some(outer), false) = (w.outer_rect, w.maximized) else { return };
    let inner = ctx.input(|i| i.viewport().inner_rect).unwrap_or(outer);
    let chrome = outer.height() - inner.height();
    let height = inner.height();
    let target = if compact {
        w.expanded_height = Some(height);
        // The panel row loses the preview; the rest keeps its size.
        let top_row = ctx.memory(|_| 0.0) + (height - tl::HEIGHT as f32 - 220.0).max(0.0);
        if top_row <= COMPACT_TOP_HEIGHT {
            return;
        }
        (height - top_row + COMPACT_TOP_HEIGHT).max(COMPACT_MIN_HEIGHT)
    } else {
        w.expanded_height.unwrap_or(height + 420.0).max(NORMAL_MIN_HEIGHT)
    };
    resize_keeping_bottom(ctx, outer, chrome, height, target);
}

fn resize_keeping_bottom(ctx: &egui::Context, outer: egui::Rect, chrome: f32, from: f32, to: f32) {
    let top = (outer.min.y + (from - to)).max(ctx.input(|i| i.viewport().monitor_size).map(|_| 0.0).unwrap_or(0.0));
    #[cfg(target_os = "macos")]
    if crate::platform_mac::change_frame(ctx, top - outer.min.y, to - from) {
        return;
    }
    let _ = chrome;
    let width = ctx.input(|i| i.viewport().inner_rect.map(|r| r.width())).unwrap_or(outer.width());
    ctx.send_viewport_cmd(ViewportCommand::InnerSize(egui::vec2(width, to)));
    ctx.send_viewport_cmd(ViewportCommand::OuterPosition(egui::pos2(outer.min.x, top)));
}

// ---- Message boxes -----------------------------------------------------------------------------------

fn modals(w: &mut ProjectWindow, ctx: &egui::Context) {
    if w.dialogs.is_empty() {
        return;
    }
    let mut result: Option<i32> = None;
    let mut checked = w.dialogs[0].checkbox.as_ref().map(|c| c.1).unwrap_or(false);
    {
        let ask = &mut w.dialogs[0];
        let modal = egui::Modal::new(egui::Id::unique(("ask", w.id))).show(ctx, |ui| {
            ui.set_width(420.0);
            ui.add(egui::Label::new(RichText::new(&ask.title).font(theme::semibold(16.0))).wrap());
            ui.add_space(8.0);
            let dim = theme::palette(ui.ctx()).text.gamma_multiply(0.85);
            ui.add(egui::Label::new(RichText::new(&ask.message).color(dim)).wrap().selectable(true));
            if let Some((text, url)) = &ask.link {
                ui.hyperlink_to(text, url);
            }
            if let Some((label, _)) = &ask.checkbox {
                ui.checkbox(&mut checked, label.as_str());
            }
            ui.add_space(12.0);
            ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                for (i, b) in ask.buttons.iter().enumerate().rev() {
                    let button = if i == 0 { theme::accent_button(b.as_str()) } else { egui::Button::new(b.as_str()) };
                    if ui.add(button.min_size(egui::vec2(90.0, 28.0))).clicked() {
                        result = Some(i as i32);
                    }
                }
            });
            if ui.input(|i| i.key_pressed(Key::Enter)) {
                result = Some(0);
            }
        });
        if modal.should_close() && result.is_none() {
            result = Some(-1);
        }
        if let Some((_, c)) = &mut ask.checkbox {
            *c = checked;
        }
    }
    if let Some(r) = result {
        let ask = w.dialogs.remove(0);
        (ask.then)(w, (r, checked));
        w.wake();
    }
}
