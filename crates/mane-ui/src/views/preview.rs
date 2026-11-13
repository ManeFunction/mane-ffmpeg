//! The preview panel: its header (title, crop tools, back to timeline, close), the video with the crop overlay, and
//! the transport.

use crate::controls::video_view;
use crate::logic::crop_gesture::{BOTTOM, LEFT, RIGHT, TOP, hit_edges};
use crate::logic::preview_geometry as geo;
use crate::logic::rect::{Pt, R};
use crate::project::ProjectWindow;
use crate::project::preview::CROP_ASPECTS;
use crate::theme::{self, Icon};
use egui::{Align2, Color32, CursorIcon, Rect, RichText, Sense, Stroke, StrokeKind, Vec2, pos2, vec2};
use mane_core::model::CropRect;

pub fn preview_panel(w: &mut ProjectWindow, ui: &mut egui::Ui) {
    // Header
    ui.horizontal(|ui| {
        ui.add_space(4.0);
        let title = w.preview_title();
        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
            if theme::close_button(ui, w.close_preview_tip()).clicked() {
                w.close_preview();
            }
            if w.is_source_mode() {
                if ui.add(theme::tool_button("← Back to timeline")).clicked() {
                    w.back_to_timeline();
                }
            } else if w.preview.is_cropping {
                if ui.add(theme::small_button("Done").fill(theme::ACCENT)).on_hover_text("Show the cropped picture (Enter)").clicked() {
                    w.finish_crop();
                }
                if ui.add_enabled(w.has_crop(), theme::small_button("Reset")).on_hover_text("Export the whole frame").clicked() {
                    w.reset_crop();
                }
                let mut aspect = w.preview.aspect;
                egui::ComboBox::from_id_salt("crop-aspect")
                    .selected_text(RichText::new(CROP_ASPECTS[aspect].name).size(11.0))
                    .width(90.0)
                    .show_ui(ui, |ui| {
                        for (i, a) in CROP_ASPECTS.iter().enumerate() {
                            ui.selectable_value(&mut aspect, i, RichText::new(a.name).size(11.0));
                        }
                    })
                    .response
                    .on_hover_text("Shape of the crop (hold Shift while dragging to keep the current one)");
                if aspect != w.preview.aspect {
                    w.select_crop_aspect(aspect);
                }
            } else if ui.add_enabled(w.can_crop(), theme::small_button("Crop")).on_hover_text("Crop the exported picture (C)").clicked() {
                w.toggle_crop();
            }
            ui.with_layout(egui::Layout::left_to_right(egui::Align::Center), |ui| {
                ui.add(
                    egui::Label::new(
                        RichText::new(title).font(theme::semibold(12.0)).color(theme::palette(ui.ctx()).text.gamma_multiply(0.7)),
                    )
                    .truncate(),
                );
            });
        });
    });
    // Transport at the bottom, the video in between.
    egui::Panel::bottom("transport").frame(egui::Frame::new().inner_margin(egui::Margin::symmetric(8, 6))).show(ui, |ui| transport(w, ui));
    egui::CentralPanel::default().frame(egui::Frame::new()).show(ui, |ui| video(w, ui));
}

pub fn play_button(w: &mut ProjectWindow, ui: &mut egui::Ui, width: f32) {
    let icon = if w.preview.is_playing { Icon::Pause } else { Icon::Play };
    if theme::icon_button(ui, icon, vec2(width, 30.0), 16.0, "Play / pause (Space)").clicked() {
        w.play_pause();
    }
}

fn transport(w: &mut ProjectWindow, ui: &mut egui::Ui) {
    ui.horizontal(|ui| {
        ui.spacing_mut().item_spacing.x = 2.0;
        let b = vec2(34.0, 30.0);
        if theme::icon_button(ui, Icon::ToStart, b, 16.0, "To start (Home)").clicked() {
            w.go_to_start();
        }
        if theme::icon_button(ui, Icon::StepBack, b, 16.0, "Previous frame (←)").clicked() {
            w.step_frame(true);
        }
        play_button(w, ui, 44.0);
        if theme::icon_button(ui, Icon::StepForward, b, 16.0, "Next frame (→)").clicked() {
            w.step_frame(false);
        }
        if theme::icon_button(ui, Icon::ToEnd, b, 16.0, "To end (End)").clicked() {
            w.go_to_end();
        }
        ui.add_space(8.0);
        ui.label(RichText::new(w.position_text()).monospace().size(11.5));
        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
            ui.spacing_mut().item_spacing.x = 4.0;
            let mut volume = w.preview.volume as f32;
            ui.spacing_mut().slider_width = if ui.available_width() > 360.0 { 90.0 } else { 60.0 };
            if ui.add(egui::Slider::new(&mut volume, 0.0..=100.0).show_value(false)).changed() {
                w.set_volume(volume as f64);
            }
            let (r, _) = ui.allocate_exact_size(Vec2::splat(14.0), Sense::hover());
            theme::paint_icon(ui.painter(), r, Icon::Speaker, theme::palette(ui.ctx()).text.gamma_multiply(0.7));
            if w.can_mark() {
                ui.add_space(12.0);
                if ui.add(theme::tool_button("Add to timeline").fill(theme::ACCENT)).clicked() {
                    w.add_marked_range();
                }
                if ui.add(theme::tool_button("Mark Out (O)")).clicked() {
                    w.set_mark_out();
                }
                if ui.add(theme::tool_button("Mark In (I)")).clicked() {
                    w.set_mark_in();
                }
                theme::dim(ui, w.marks_text());
            }
        });
    });
}

/// The video, drawn from mpv's latest frame, with the crop overlay on top while cropping.
fn video(w: &mut ProjectWindow, ui: &mut egui::Ui) {
    let (rect, response) = ui.allocate_exact_size(ui.available_size(), Sense::click_and_drag());
    let painter = ui.painter_at(rect);
    painter.rect_filled(rect, 0, Color32::BLACK);
    let bounds = R::from_egui(rect);
    let (picture_aspect, frame_aspect) = (w.preview.picture_aspect, w.preview.frame_aspect);
    let shown_crop = w.shown_crop();
    if let Some(renderer) = &w.preview.renderer {
        // A cropped part is shown enlarged: render that much bigger (up to the pixel limit) so it isn't blurry.
        let ppp = ui.ctx().pixels_per_point();
        let scale = ppp as f64
            * geo::render_scale(R::new(0.0, 0.0, bounds.width, bounds.height), picture_aspect, frame_aspect, shown_crop.as_ref());
        renderer.set_target((bounds.width * scale) as f32, (bounds.height * scale) as f32);
        if let Some(state) = w.app.render_state.get()
            && let Some((id, size)) = w.preview.texture.update(state, renderer)
            && w.preview.message.is_none()
        {
            let full = Rect::from_min_size(egui::Pos2::ZERO, size);
            if shown_crop.is_none() && (frame_aspect <= 0.0 || (frame_aspect - picture_aspect).abs() < 1e-3) {
                video_view::paint(&painter, id, size, full, rect);
            } else {
                let (src, dst) = geo::placement(
                    size.x as f64,
                    size.y as f64,
                    bounds,
                    picture_aspect,
                    frame_aspect,
                    &shown_crop.unwrap_or(CropRect::FULL),
                );
                if src.width > 0.0 && src.height > 0.0 {
                    video_view::paint(&painter, id, size, src.to_egui(), dst.to_egui());
                }
            }
        }
    }
    if let Some(message) = &w.preview.message {
        let galley =
            painter.layout(message.clone(), theme::regular(14.0), Color32::from_white_alpha(200), (rect.width() - 40.0).max(100.0));
        let pos = rect.center() - galley.size() / 2.0;
        painter.galley(pos, galley, Color32::from_white_alpha(200));
    }
    if w.preview.is_cropping {
        crop_overlay(w, ui, rect, &response);
    }
}

fn crop_overlay(w: &mut ProjectWindow, ui: &mut egui::Ui, rect: Rect, response: &egui::Response) {
    let (picture_aspect, frame_aspect) = (w.preview.picture_aspect, w.preview.frame_aspect);
    if picture_aspect <= 0.0 {
        return;
    }
    let bounds = R::from_egui(rect);
    let picture = geo::fit(picture_aspect, bounds);
    let frame = geo::frame_around(picture, picture_aspect, if frame_aspect > 0.0 { frame_aspect } else { picture_aspect });
    let crop = w.state().crop().unwrap_or(CropRect::FULL);
    let shown = w.preview.crop_drag.live.unwrap_or_else(|| geo::inside(frame, &crop));
    let to_pt = |p: egui::Pos2| Pt::new(p.x as f64, p.y as f64);
    let shift = ui.input(|i| i.modifiers.shift);
    if response.drag_started_by(egui::PointerButton::Primary)
        && let Some(p) = ui.input(|i| i.pointer.press_origin())
    {
        let lock = w.aspect_lock();
        w.preview.crop_drag.press(to_pt(p), frame, shown, lock);
    }
    if w.preview.crop_drag.is_dragging() {
        if let Some(p) = ui.input(|i| i.pointer.interact_pos()) {
            if response.dragged() {
                w.preview.crop_drag.move_to(to_pt(p), shift);
            }
            if response.drag_stopped()
                && let Some(c) = w.preview.crop_drag.release(to_pt(p))
            {
                w.commit_crop(Some(c));
            }
        }
    } else if let Some(p) = response.hover_pos() {
        let edges = hit_edges(shown, to_pt(p));
        let cursor = match edges {
            e if e == LEFT | TOP || e == RIGHT | BOTTOM => CursorIcon::ResizeNwSe,
            e if e == RIGHT | TOP || e == LEFT | BOTTOM => CursorIcon::ResizeNeSw,
            LEFT | RIGHT => CursorIcon::ResizeHorizontal,
            TOP | BOTTOM => CursorIcon::ResizeVertical,
            _ if shown.contains(to_pt(p)) => CursorIcon::Move,
            _ => CursorIcon::Crosshair,
        };
        ui.ctx().set_cursor_icon(cursor);
    }
    let shown = w.preview.crop_drag.live.unwrap_or(shown);
    let painter = ui.painter_at(rect);
    let r = shown.to_egui();
    let shade = Color32::from_black_alpha(150);
    // Everything outside the rectangle is dimmed: four bands around it.
    painter.rect_filled(Rect::from_min_max(rect.min, pos2(rect.max.x, r.min.y.max(rect.min.y))), 0, shade);
    painter.rect_filled(Rect::from_min_max(pos2(rect.min.x, r.max.y.min(rect.max.y)), rect.max), 0, shade);
    painter.rect_filled(Rect::from_min_max(pos2(rect.min.x, r.min.y), pos2(r.min.x, r.max.y)), 0, shade);
    painter.rect_filled(Rect::from_min_max(pos2(r.max.x, r.min.y), pos2(rect.max.x, r.max.y)), 0, shade);
    let thirds = Stroke::new(1.0, Color32::from_white_alpha(90));
    for i in 1..=2 {
        let x = r.min.x + r.width() * i as f32 / 3.0;
        let y = r.min.y + r.height() * i as f32 / 3.0;
        painter.line_segment([pos2(x, r.min.y), pos2(x, r.max.y)], thirds);
        painter.line_segment([pos2(r.min.x, y), pos2(r.max.x, y)], thirds);
    }
    painter.rect_stroke(r, 0, Stroke::new(1.5, Color32::WHITE), StrokeKind::Middle);
    for x in [r.min.x, r.center().x, r.max.x] {
        for y in [r.min.y, r.center().y, r.max.y] {
            if x == r.center().x && y == r.center().y {
                continue;
            }
            let h = Rect::from_center_size(pos2(x, y), Vec2::splat(8.0));
            painter.rect(h, 0, Color32::WHITE, Stroke::new(1.0, Color32::from_black_alpha(160)), StrokeKind::Inside);
        }
    }
    // The size in output pixels, on the rectangle.
    let (fw, fh) = w.preview.frame_pixels;
    if fw > 0 && fh > 0 {
        let c = geo::to_crop(frame, shown).clamped().unwrap_or(CropRect::FULL);
        let text = c.to_pixels(fw, fh).size_text();
        let galley = painter.layout_no_wrap(text, theme::regular(12.0), Color32::WHITE);
        let label = Rect::from_center_size(r.center(), galley.size() + vec2(12.0, 6.0));
        painter.rect_filled(label, 4, Color32::from_black_alpha(180));
        painter.galley(label.min + vec2(6.0, 3.0), galley, Color32::WHITE);
    }
    let _ = Align2::CENTER_CENTER;
}
