//! The editing timeline: a ruler, the video row (V1) and the audio row (A1). Click to seek and select, drag clips to
//! reorder, drag clip edges to trim, drag on A1 to slip a clip's audio.

use crate::logic::timeline_gestures::TimelineZone;
use crate::logic::timeline_layout::{self as tl, AUDIO_HEIGHT, AUDIO_TOP, HEADER, RULER_HEIGHT, VIDEO_HEIGHT, VIDEO_TOP};
use crate::project::ProjectWindow;
use crate::project::files::{audio_color, video_color};
use crate::theme;
use egui::{Align2, Color32, CornerRadius, CursorIcon, Pos2, Rect, Sense, Shape, Stroke, StrokeKind, Vec2, pos2, vec2};
use mane_core::model::time::{self, Time};
use mane_core::model::{AudioLayout, Clip};

const fn hex(v: u32) -> Color32 {
    Color32::from_rgb((v >> 16) as u8, (v >> 8) as u8, v as u8)
}

const BACKGROUND: Color32 = hex(0x17181C);
const RULER: Color32 = hex(0x202127);
const TRACK: Color32 = hex(0x1D1E23);
const HEADER_FILL: Color32 = hex(0x26272E);
const TEXT: Color32 = hex(0xA9ADB7);
const LABEL: Color32 = Color32::from_rgba_premultiplied(235, 235, 235, 235);
const SILENCE: Color32 = Color32::from_rgba_premultiplied(0, 0, 0, 70);
const BADGE: Color32 = hex(0xF5A623);
const BADGE_TEXT: Color32 = hex(0x1D1E22);
const MISSING: Color32 = hex(0x7A2E2E);
const TICK: Color32 = hex(0x4A4C55);
const MINOR_TICK: Color32 = hex(0x34363D);
const PLAYHEAD: Color32 = hex(0xFF5252);
const DROP: Color32 = hex(0xFFD54F);

fn white(a: u8) -> Color32 {
    Color32::from_white_alpha(a)
}

fn ink(a: u8) -> Color32 {
    Color32::from_rgba_unmultiplied(29, 30, 34, a)
}

/// Draws the timeline into the space left and handles its input.
pub fn timeline_view(w: &mut ProjectWindow, ui: &mut egui::Ui) {
    let size = vec2(ui.available_width(), tl::HEIGHT as f32);
    let (rect, response) = ui.allocate_exact_size(size, Sense::click_and_drag());
    w.timeline.viewport_width = (rect.width() as f64 - HEADER).max(100.0);
    if w.timeline.zoom <= 0.0 && w.has_clips() && rect.width() as f64 > HEADER {
        let d = w.state().timeline.duration();
        w.timeline.fit(d);
    }
    handle_input(w, ui, rect, &response);
    draw(w, ui, rect);
}

fn local(rect: Rect, p: Pos2) -> (f64, f64) {
    ((p.x - rect.min.x) as f64, (p.y - rect.min.y) as f64)
}

fn command_held(m: &egui::Modifiers) -> bool {
    if cfg!(target_os = "macos") { m.mac_cmd } else { m.ctrl }
}

fn handle_input(w: &mut ProjectWindow, ui: &mut egui::Ui, rect: Rect, response: &egui::Response) {
    if w.timeline.zoom <= 0.0 {
        return;
    }
    let (zoom, scroll) = (w.timeline.zoom, w.timeline.scroll);
    let modifiers = ui.input(|i| i.modifiers);
    let pointer = ui.input(|i| i.pointer.interact_pos());
    let time_at = |p: Pos2| tl::time(zoom, scroll, local(rect, p).0);
    // Press
    if response.is_pointer_button_down_on()
        && ui.input(|i| i.pointer.primary_pressed())
        && let Some(p) = ui.input(|i| i.pointer.press_origin())
    {
        let (x, y) = local(rect, p);
        if x >= HEADER && !w.timeline.gestures.is_active() {
            let timeline = w.state().timeline.clone();
            let hit = tl::hit_test(&timeline, zoom, scroll, x, y);
            ui.memory_mut(|m| m.request_focus(response.id));
            w.with_gestures(|g, h| g.press(h, hit, command_held(&modifiers), modifiers.shift));
        }
    }
    if w.timeline.gestures.is_active() && !ui.input(|i| i.focused) {
        // The pointer was taken away mid-gesture: keep what was dragged so far.
        w.with_gestures(|g, h| g.abort(h));
        w.last_shift = None;
    }
    if w.timeline.gestures.is_active() {
        if ui.input(|i| i.pointer.primary_down()) {
            if let Some(p) = pointer {
                let moved = ui.input(|i| i.pointer.delta() != Vec2::ZERO);
                let shift_changed = w.last_shift.is_some_and(|s| s != modifiers.shift);
                if moved {
                    let t = time_at(p);
                    w.with_gestures(|g, h| g.move_to(h, t, modifiers.shift));
                } else if shift_changed {
                    w.with_gestures(|g, h| g.shift_changed(h, modifiers.shift));
                }
            }
            w.last_shift = Some(modifiers.shift);
            if w.timeline.gestures.is_reordering() {
                ui.ctx().set_cursor_icon(CursorIcon::Grabbing);
            }
        } else {
            let t = pointer.map(time_at).unwrap_or(w.timeline.playhead);
            w.with_gestures(|g, h| g.release(h, t));
            w.last_shift = None;
        }
    } else if let Some(p) = response.hover_pos() {
        let (x, y) = local(rect, p);
        if x >= HEADER {
            let timeline = &w.state().timeline;
            let cursor = match tl::hit_test(timeline, zoom, scroll, x, y).zone {
                TimelineZone::TrimIn | TimelineZone::TrimOut | TimelineZone::Audio => CursorIcon::ResizeHorizontal,
                TimelineZone::Clip => CursorIcon::PointingHand,
                _ => CursorIcon::Default,
            };
            ui.ctx().set_cursor_icon(cursor);
        }
    }
    // Wheel: zoom with Cmd/Ctrl or Alt, else scroll.
    if response.hovered() {
        let (delta, zoom_delta) = ui.input(|i| (i.smooth_scroll_delta, i.zoom_delta()));
        let duration = w.state().timeline.duration();
        if zoom_delta != 1.0 {
            let anchor = pointer.map(|p| (local(rect, p).0 - HEADER).max(0.0)).unwrap_or(0.0);
            w.timeline.zoom_by(zoom_delta as f64, anchor, duration);
        } else if modifiers.alt && delta.y != 0.0 {
            let anchor = pointer.map(|p| (local(rect, p).0 - HEADER).max(0.0)).unwrap_or(0.0);
            w.timeline.zoom_by(1.15f64.powf(delta.y as f64 / 40.0), anchor, duration);
        } else if delta != Vec2::ZERO {
            let d = if delta.x.abs() > delta.y.abs() { delta.x } else { delta.y } as f64;
            let t = &mut w.timeline;
            let max_scroll = (duration.total_seconds() - t.viewport_width / t.zoom * 0.5).max(0.0);
            t.scroll = (t.scroll - d * 1.5 / t.zoom).clamp(0.0, max_scroll);
        }
    }
}

fn draw(w: &ProjectWindow, ui: &egui::Ui, rect: Rect) {
    let painter = ui.painter_at(rect);
    painter.rect_filled(rect, 0, BACKGROUND);
    let o = rect.min;
    let at = |x: f64, y: f64| pos2(o.x + x as f32, o.y + y as f32);
    let t = &w.timeline;
    if t.zoom <= 0.0 {
        painter.text(
            at(HEADER + 12.0, VIDEO_TOP + VIDEO_HEIGHT / 2.0),
            Align2::LEFT_CENTER,
            "Add videos to build a timeline. Drag clips to reorder, drag edges to trim, drag the audio row to fix sync.",
            theme::regular(13.0),
            TEXT,
        );
        return;
    }
    let width = rect.width() as f64;
    draw_ruler(w, &painter, rect);
    painter.rect_filled(Rect::from_min_max(at(HEADER, VIDEO_TOP), at(width, VIDEO_TOP + VIDEO_HEIGHT)), 0, TRACK);
    painter.rect_filled(Rect::from_min_max(at(HEADER, AUDIO_TOP), at(width, AUDIO_TOP + AUDIO_HEIGHT)), 0, TRACK);
    let tracks = painter.with_clip_rect(Rect::from_min_max(at(HEADER, 0.0), rect.max));
    let mut start = Time::ZERO;
    for clip in w.state().timeline.clips() {
        let x0 = tl::x(t.zoom, t.scroll, start);
        let x1 = tl::x(t.zoom, t.scroll, start + clip.duration());
        if x1 >= HEADER && x0 <= width {
            draw_clip(w, &tracks, rect, clip, start, x0, x1);
        }
        start += clip.duration();
    }
    if let (true, Some(i)) = (t.gestures.is_reordering(), t.gestures.insert_index) {
        let x = tl::x(t.zoom, t.scroll, w.state().timeline.start_of(i));
        tracks.line_segment([at(x, VIDEO_TOP - 4.0), at(x, AUDIO_TOP + AUDIO_HEIGHT + 4.0)], Stroke::new(3.0, DROP));
    }
    let ph = tl::x(t.zoom, t.scroll, t.playhead);
    if ph >= HEADER - 1.0 && ph <= width + 1.0 {
        tracks.line_segment([at(ph, RULER_HEIGHT - 6.0), at(ph, tl::HEIGHT)], Stroke::new(1.5, PLAYHEAD));
        tracks.add(Shape::convex_polygon(
            vec![at(ph - 6.0, RULER_HEIGHT - 12.0), at(ph + 6.0, RULER_HEIGHT - 12.0), at(ph, RULER_HEIGHT - 4.0)],
            PLAYHEAD,
            Stroke::NONE,
        ));
    }
    painter.rect_filled(Rect::from_min_max(o, at(HEADER, tl::HEIGHT)), 0, HEADER_FILL);
    painter.text(at(14.0, VIDEO_TOP + VIDEO_HEIGHT / 2.0), Align2::LEFT_CENTER, "V1", theme::regular(12.0), TEXT);
    painter.text(at(14.0, AUDIO_TOP + AUDIO_HEIGHT / 2.0), Align2::LEFT_CENTER, "A1", theme::regular(12.0), TEXT);
}

fn draw_ruler(w: &ProjectWindow, painter: &egui::Painter, rect: Rect) {
    let o = rect.min;
    let at = |x: f64, y: f64| pos2(o.x + x as f32, o.y + y as f32);
    let width = rect.width() as f64;
    painter.rect_filled(Rect::from_min_max(o, at(width, RULER_HEIGHT)), 0, RULER);
    let t = &w.timeline;
    const STEPS: [f64; 17] = [0.1, 0.2, 0.5, 1.0, 2.0, 5.0, 10.0, 15.0, 30.0, 60.0, 120.0, 300.0, 600.0, 900.0, 1800.0, 3600.0, 7200.0];
    let step = STEPS.iter().copied().find(|s| s * t.zoom >= 90.0).unwrap_or(7200.0);
    let ratio: i64 = if [15.0, 30.0, 900.0, 1800.0].contains(&step) {
        3
    } else if [2.0, 0.2, 120.0, 7200.0].contains(&step) {
        4
    } else {
        5
    };
    let minor = step / ratio as f64;
    let last = t.scroll + (width - HEADER) / t.zoom;
    let end = w.state().timeline.duration().total_seconds();
    // Integer tick indexes: accumulating floating-point steps would drift and mislabel ticks.
    let mut k = (t.scroll / minor).floor() as i64;
    while (k as f64) * minor <= last {
        let s = k as f64 * minor;
        let x = HEADER + (s - t.scroll) * t.zoom;
        if x >= HEADER {
            let major = k % ratio == 0;
            let xr = (x.round() + 0.5) as f32 - o.x;
            painter.line_segment(
                [at(xr as f64, if major { 8.0 } else { 17.0 }), at(xr as f64, RULER_HEIGHT)],
                Stroke::new(1.0, if major { TICK } else { MINOR_TICK }),
            );
            if major && s <= end + step {
                painter.text(at(x + 4.0, 4.0), Align2::LEFT_TOP, ruler_label(s, step), theme::regular(10.0), TEXT);
            }
        }
        k += 1;
    }
}

fn ruler_label(seconds: f64, step: f64) -> String {
    let ms = (seconds * 1000.0).round() as i64;
    let (h, m, s, f) = (ms / 3_600_000, ms / 60_000 % 60, ms / 1000 % 60, ms % 1000 / 100);
    match (h > 0, step < 1.0) {
        (true, true) => format!("{h}:{m:02}:{s:02}.{f}"),
        (false, true) => format!("{}:{s:02}.{f}", ms / 60_000),
        (true, false) => format!("{h}:{m:02}:{s:02}"),
        (false, false) => format!("{}:{s:02}", ms / 60_000),
    }
}

fn darken(c: Color32, f: f32) -> Color32 {
    Color32::from_rgb((c.r() as f32 * f) as u8, (c.g() as f32 * f) as u8, (c.b() as f32 * f) as u8)
}

fn draw_clip(w: &ProjectWindow, painter: &egui::Painter, rect: Rect, clip: &Clip, start: Time, x0: f64, x1: f64) {
    let o = rect.min;
    let width = rect.width() as f64;
    let at = |x: f64, y: f64| pos2(o.x + x as f32, o.y + y as f32);
    let t = &w.timeline;
    let state = w.state();
    let source = state.source(&clip.source_id);
    let item = w.sources.iter().find(|s| s.id() == clip.source_id);
    let color = video_color(item.map(|i| i.color_index).unwrap_or(0));
    let missing = source.is_none() || item.is_none_or(|i| i.is_problem());
    let selected = t.selection.contains(&clip.id);
    let video_rect = Rect::from_min_size(at(x0 + 1.0, VIDEO_TOP + 1.0), vec2((x1 - x0 - 2.0).max(1.0) as f32, (VIDEO_HEIGHT - 2.0) as f32));
    let audio_rect = Rect::from_min_size(at(x0 + 1.0, AUDIO_TOP + 1.0), vec2((x1 - x0 - 2.0).max(1.0) as f32, (AUDIO_HEIGHT - 2.0) as f32));
    let dragged = t.gestures.is_reordering() && selected;
    let alpha = if dragged { 110 } else { 230 };
    let radius = CornerRadius::same(4);
    let clip_stroke = Stroke::new(1.0, Color32::from_black_alpha(90));
    let x_of = |time: Time| tl::x(t.zoom, t.scroll, time);

    // Video row
    let fill = if missing { MISSING } else { Color32::from_rgba_unmultiplied(color.r(), color.g(), color.b(), alpha) };
    painter.rect(video_rect, radius, fill, clip_stroke, StrokeKind::Inside);
    if w.snap_to_keyframes
        && let Some(keyframes) = source.and_then(|s| w.catalog.keyframes(s))
    {
        for &k in &keyframes.times {
            if k < clip.r#in || k > clip.out {
                continue;
            }
            let x = x_of(start + (k - clip.r#in));
            if x < HEADER || x > width {
                continue;
            }
            painter.line_segment(
                [pos2(o.x + x as f32, video_rect.top() + 1.0), pos2(o.x + x as f32, video_rect.top() + 8.0)],
                Stroke::new(1.0, white(200)),
            );
        }
    }
    let label_x = video_rect.left().max(o.x + HEADER as f32) + 6.0;
    let vp = painter.with_clip_rect(video_rect.intersect(painter.clip_rect()));
    let name = source.map(|s| s.display_name()).unwrap_or_else(|| "missing".into());
    vp.text(pos2(label_x, video_rect.top() + 6.0), Align2::LEFT_TOP, name, theme::semibold(12.0), LABEL);
    vp.text(
        pos2(label_x, video_rect.top() + 24.0),
        Align2::LEFT_TOP,
        format!("{} – {}  ·  {}", time::display(clip.r#in, false), time::display(clip.out, false), time::display(clip.duration(), false)),
        theme::regular(10.0),
        LABEL,
    );

    // Audio row: the clip's own sound, or an audio file dropped on it (pastel, dark ink)
    let replaced = &clip.replaced_audio;
    let heard_file = state.audio_source_of(clip);
    let audio_item = replaced.as_ref().and_then(|r| w.audio_files.iter().find(|a| a.id() == r.file_id));
    let audio_missing = match replaced {
        None => missing,
        Some(_) => heard_file.is_none() || audio_item.is_none_or(|a| a.is_problem()),
    };
    let audio_fill = match replaced {
        None => {
            let c = darken(color, 0.55);
            Color32::from_rgba_unmultiplied(c.r(), c.g(), c.b(), if dragged { 100 } else { 230 })
        }
        Some(_) => {
            let c = audio_color(audio_item.map(|a| a.color_index).unwrap_or(0));
            Color32::from_rgba_unmultiplied(c.r(), c.g(), c.b(), if dragged { 100 } else { 235 })
        }
    };
    painter.rect(audio_rect, radius, if audio_missing { MISSING } else { audio_fill }, clip_stroke, StrokeKind::Inside);
    let ap = painter.with_clip_rect(audio_rect.intersect(painter.clip_rect()));
    let mut text_x = audio_rect.left().max(o.x + HEADER as f32) + 5.0;
    let heard = state.heard_audio(clip, |id| w.catalog.info(id));
    if let Some(sound) = &heard {
        let layout = sound.layout(clip);
        let (hatch, wave) = if replaced.is_none() { (white(60), white(200)) } else { (ink(70), ink(185)) };
        draw_silence(&ap, w, rect, start, audio_rect, &layout, hatch);
        if let AudioLayout::Looped(lp) = &layout {
            draw_seams(&ap, w, rect, start, audio_rect, &layout, lp.period);
        }
        if let Some(waveform) = w.catalog.waveform(&sound.file) {
            draw_waveform(&ap, w, rect, start, audio_rect, &layout, &waveform, wave);
        }
    } else if heard_file.is_some_and(|f| w.catalog.info(&f.id).is_some()) {
        ap.text(pos2(text_x + 1.0, audio_rect.center().y), Align2::LEFT_CENTER, "no audio", theme::regular(11.0), TEXT);
    }
    if clip.active_audio_offset() != Time::ZERO {
        let galley = ap.layout_no_wrap(format!("♪ {}", time::offset(clip.active_audio_offset())), theme::semibold(10.0), BADGE_TEXT);
        let badge = Rect::from_min_size(pos2(text_x, audio_rect.center().y - 8.0), vec2(galley.size().x + 10.0, 16.0));
        ap.rect_filled(badge, 6, BADGE);
        ap.galley(pos2(badge.left() + 5.0, badge.center().y - galley.size().y / 2.0), galley, BADGE_TEXT);
        text_x += badge.width() + 6.0;
    }
    if replaced.is_some() {
        // On a chip of the file's color, so the name stays readable over the waveform.
        let name = heard_file.map(|f| f.display_name()).unwrap_or_else(|| "missing audio file".into());
        let label = format!("♫ {name}{}", if clip.loops_audio() { "  ↻" } else { "" });
        let color = if audio_missing { LABEL } else { ink(185) };
        let galley = ap.layout_no_wrap(label, theme::semibold(10.0), color);
        let chip = Rect::from_min_size(pos2(text_x, audio_rect.center().y - galley.size().y / 2.0 - 1.0), galley.size() + vec2(10.0, 2.0));
        if !audio_missing {
            ap.rect(chip, 6, audio_fill, Stroke::new(1.0, ink(70)), StrokeKind::Inside);
        }
        ap.galley(chip.min + vec2(5.0, 1.0), galley, color);
    }
    if let Some(drop) = t.audio_drop.as_ref().filter(|d| d.clip_id.as_deref() == Some(&clip.id)) {
        let drop_stroke = Stroke::new(3.0, DROP);
        painter.rect(audio_rect, radius, drop.color.gamma_multiply(0.95), drop_stroke, StrokeKind::Inside);
        painter.rect_stroke(video_rect, radius, drop_stroke, StrokeKind::Inside);
        ap.text(
            pos2(text_x + 1.0, audio_rect.center().y),
            Align2::LEFT_CENTER,
            format!("♫ {}", drop.name),
            theme::semibold(11.0),
            ink(185),
        );
    }
    if selected {
        let s = Stroke::new(2.0, Color32::WHITE);
        painter.rect_stroke(video_rect.shrink(1.0), radius, s, StrokeKind::Inside);
        painter.rect_stroke(audio_rect.shrink(1.0), radius, s, StrokeKind::Inside);
    }
}

/// Hatches the parts of the clip where the slipped audio runs past the file (silence on export).
fn draw_silence(p: &egui::Painter, w: &ProjectWindow, rect: Rect, start: Time, row: Rect, layout: &AudioLayout, hatch: Color32) {
    let t = &w.timeline;
    for span in layout.spans() {
        if !span.is_silence() {
            continue;
        }
        let a = rect.min.x + tl::x(t.zoom, t.scroll, start + span.at) as f32;
        let b = rect.min.x + tl::x(t.zoom, t.scroll, start + span.at + span.length) as f32;
        if b - a < 1.0 {
            continue;
        }
        let r = Rect::from_min_max(pos2(a, row.top()), pos2(b, row.bottom()));
        p.rect_filled(r, 0, SILENCE);
        let hp = p.with_clip_rect(r.intersect(p.clip_rect()));
        let mut x = a - row.height();
        while x < b {
            hp.line_segment([pos2(x, row.bottom()), pos2(x + row.height(), row.top())], Stroke::new(1.0, hatch));
            x += 8.0;
        }
    }
}

/// Marks where a looped audio file starts again; only the first seam when they would be under 2 px apart.
fn draw_seams(p: &egui::Painter, w: &ProjectWindow, rect: Rect, start: Time, row: Rect, layout: &AudioLayout, period: Time) {
    let t = &w.timeline;
    let dense = period.total_seconds() * t.zoom < 2.0;
    let left = (row.left() - rect.min.x).max(HEADER as f32) as f64;
    let right = (row.right() - rect.min.x).min(rect.width()) as f64;
    let from = if dense { Time::ZERO } else { tl::time(t.zoom, t.scroll, left) - start };
    let to = tl::time(t.zoom, t.scroll, right) - start;
    for at in layout.edges(from, to) {
        let x = rect.min.x + tl::x(t.zoom, t.scroll, start + at).round() as f32 + 0.5;
        if x >= row.left() && x <= row.right() {
            let mut y = row.top() + 2.0;
            while y < row.bottom() - 2.0 {
                p.line_segment([pos2(x, y), pos2(x, (y + 2.0).min(row.bottom() - 2.0))], Stroke::new(1.0, ink(150)));
                y += 4.0;
            }
        }
        if dense {
            break;
        }
    }
}

#[allow(clippy::too_many_arguments)]
fn draw_waveform(
    p: &egui::Painter,
    w: &ProjectWindow,
    rect: Rect,
    start: Time,
    row: Rect,
    layout: &AudioLayout,
    waveform: &mane_core::media::waveform::Waveform,
    color: Color32,
) {
    let t = &w.timeline;
    let mid = row.center().y;
    let half = row.height() / 2.0 - 3.0;
    let left = row.left().max(rect.min.x + HEADER as f32);
    let right = row.right().min(rect.max.x);
    let per_pixel = Time::from_seconds(1.0 / t.zoom);
    let duration = waveform.duration();
    let mut x = left.floor();
    let stroke = Stroke::new(1.0, color);
    let mut shapes = Vec::new();
    while x < right {
        let file_time = layout.file_time(tl::time(t.zoom, t.scroll, (x - rect.min.x) as f64) - start);
        if file_time + per_pixel >= Time::ZERO && file_time <= duration {
            let h = (waveform.max(file_time, file_time + per_pixel) as f32 / 255.0 * half).max(0.5);
            shapes.push(Shape::line_segment([pos2(x + 0.5, mid - h), pos2(x + 0.5, mid + h)], stroke));
        }
        x += 1.0;
    }
    p.extend(shapes);
}
