//! The Selection inspector: in/out, replaced audio, and the audio offset with its nudges.

use crate::project::{FieldEdit, ProjectWindow};
use crate::theme;
use egui::{RichText, Vec2};
use mane_core::model::Time;

/// A text field that applies on Enter or when it loses focus; Escape drops what was typed. Returns the text to
/// apply.
fn field(ui: &mut egui::Ui, edit: &mut FieldEdit, current: String, id: &str, width: f32) -> (Option<String>, egui::Response) {
    if !edit.editing {
        edit.text = current.clone();
    }
    let r = ui.add(egui::TextEdit::singleline(&mut edit.text).id_salt(id).desired_width(width).font(egui::FontId::proportional(13.0)));
    if r.gained_focus() {
        edit.editing = true;
    }
    let mut apply = None;
    if r.lost_focus() {
        let escaped = ui.input(|i| i.key_pressed(egui::Key::Escape));
        if !escaped && edit.text != current {
            apply = Some(edit.text.clone());
        }
        edit.editing = false;
    }
    (apply, r)
}

pub fn inspector(w: &mut ProjectWindow, ui: &mut egui::Ui) {
    theme::title(ui, "Selection");
    ui.add(egui::Label::new(RichText::new(w.selection_title()).font(theme::semibold(13.0))).truncate());
    theme::dim_wrapped(ui, w.selection_details());
    if w.has_selection() {
        let half = ((ui.available_width() - 14.0) / 2.0).max(40.0);
        ui.horizontal(|ui| {
            ui.vertical(|ui| {
                theme::dim(ui, "In");
                let current = w.clip_in_text();
                let (apply, _) = field(ui, &mut w.clip_in, current, "clip-in", half);
                if let Some(t) = apply {
                    w.set_clip_in(&t);
                }
            });
            ui.vertical(|ui| {
                theme::dim(ui, "Out");
                let current = w.clip_out_text();
                let (apply, _) = field(ui, &mut w.clip_out, current, "clip-out", half);
                if let Some(t) = apply {
                    w.set_clip_out(&t);
                }
            });
        });
    }
    if w.has_replaced_audio() {
        ui.horizontal(|ui| {
            let (bar, _) = ui.allocate_exact_size(Vec2::new(4.0, 16.0), egui::Sense::hover());
            if let Some(c) = w.replaced_audio_color() {
                ui.painter().rect_filled(bar, 2, c);
            }
            ui.add(egui::Label::new(RichText::new(w.replaced_audio_name()).font(theme::semibold(13.0))).truncate());
        });
        ui.horizontal(|ui| {
            let mut looped = w.loop_audio();
            if ui
                .checkbox(&mut looped, RichText::new("Loop audio").size(12.0))
                .on_hover_text("Repeat the audio file so this clip has no silence")
                .changed()
            {
                w.set_loop_audio(looped);
            }
            ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                if ui.add(theme::tool_button("Use original audio")).on_hover_text("Play the clip's own sound again").clicked() {
                    w.restore_original_audio();
                }
            });
        });
    }
    if !w.has_selection() {
        return;
    }
    theme::dim_wrapped(ui, "Audio offset (ms) — positive plays audio later");
    ui.horizontal(|ui| {
        if ui.add(theme::tool_button("−10")).clicked() {
            w.nudge_audio(Time::from_ms(-10));
        }
        let width = (ui.available_width() - 48.0).max(40.0);
        let current = w.audio_offset_text();
        let (apply, r) = field(ui, &mut w.offset, current, "offset", width);
        // The arrows and the wheel step by 1 ms from what's typed in the field.
        if r.has_focus() {
            let step = ui.input(|i| {
                if i.key_pressed(egui::Key::ArrowUp) {
                    1
                } else if i.key_pressed(egui::Key::ArrowDown) {
                    -1
                } else {
                    0
                }
            });
            if step != 0 {
                let typed = w.offset.text.clone();
                w.set_audio_offset_text(&typed);
                w.nudge_audio(Time::from_ms(step));
                w.offset.editing = true;
                w.offset.text = w.audio_offset_text();
            }
        }
        if r.hovered() {
            let scroll = ui.input(|i| i.smooth_scroll_delta.y);
            if scroll.abs() > 0.5 {
                w.nudge_audio(Time::from_ms(if scroll > 0.0 { 1 } else { -1 }));
            }
        }
        if let Some(t) = apply {
            w.set_audio_offset_text(&t);
        }
        if ui.add(theme::tool_button("+10")).clicked() {
            w.nudge_audio(Time::from_ms(10));
        }
    });
    ui.horizontal(|ui| {
        let width = ((ui.available_width() - 8.0) / 3.0).max(40.0);
        if ui.add_sized([width, 26.0], theme::tool_button("−1 frame")).clicked() {
            w.nudge_audio_frames(-1);
        }
        if ui.add_sized([width, 26.0], theme::tool_button("Reset")).clicked() {
            w.reset_audio_offset();
        }
        if ui.add_sized([width, 26.0], theme::tool_button("+1 frame")).clicked() {
            w.nudge_audio_frames(1);
        }
    });
    let hint = w.audio_offset_hint();
    if !hint.is_empty() {
        theme::dim_wrapped(ui, hint);
    }
    if ui.add_sized([ui.available_width(), 26.0], theme::tool_button(w.apply_offset_text())).clicked() {
        w.apply_offset_to_source();
    }
}
