//! The export bar along the bottom: preset, output file, export/pause/cancel, progress or the last result, and the
//! ffmpeg command.

use crate::project::ProjectWindow;
use crate::theme;
use egui::{RichText, Vec2};

/// The width a tool button with `text` takes.
fn button_width(ui: &egui::Ui, text: &str, size: f32) -> f32 {
    let w = ui.fonts_mut(|f| f.layout_no_wrap(text.to_string(), theme::regular(size), egui::Color32::WHITE).size().x);
    w + 2.0 * ui.spacing().button_padding.x
}

pub fn export_bar(w: &mut ProjectWindow, ui: &mut egui::Ui) {
    let idle = w.export.is_idle();
    ui.horizontal(|ui| {
        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
            // Export / Pause⇄Resume / Cancel keep their widths, so the path field doesn't jump.
            if idle {
                let b = theme::accent_button(RichText::new("Export").size(13.0)).min_size(Vec2::new(96.0, 28.0));
                if ui.add(b).clicked() {
                    w.export();
                }
            } else {
                if ui.add(egui::Button::new("Cancel").min_size(Vec2::new(80.0, 28.0))).clicked() {
                    w.cancel_export();
                }
                let (label, paused) = if w.export.is_paused { ("Resume", true) } else { ("Pause", false) };
                if ui.add(egui::Button::new(label).min_size(Vec2::new(88.0, 28.0))).clicked() {
                    if paused {
                        w.resume_export();
                    } else {
                        w.pause_export();
                    }
                }
            }
            ui.add_space(6.0);
            ui.with_layout(egui::Layout::left_to_right(egui::Align::Center), |ui| {
                theme::title(ui, "Preset");
                ui.add_enabled_ui(idle, |ui| {
                    let selected = w.export.selected.as_ref().map(|p| p.name.clone()).unwrap_or_default();
                    let mut chosen: Option<String> = None;
                    egui::ComboBox::from_id_salt("preset").selected_text(selected).width(170.0).show_ui(ui, |ui| {
                        for p in &w.export.presets {
                            let is = w.export.selected.as_ref().is_some_and(|s| s.id == p.id);
                            if ui.selectable_label(is, &p.name).clicked() {
                                chosen = Some(p.id.clone());
                            }
                        }
                    });
                    if let Some(id) = chosen {
                        w.select_preset(&id);
                    }
                });
                if ui.add(theme::tool_button("Edit presets…")).clicked() {
                    w.show_presets();
                }
                ui.add_space(12.0);
                theme::title(ui, "Save to");
                let spacing = ui.spacing().item_spacing.x;
                let buttons = button_width(ui, "Choose…", 12.0) + button_width(ui, "Auto", 12.0) + 3.0 * spacing + 18.0;
                let field_width = (ui.available_width() - buttons).max(60.0);
                let display = w.output_display();
                let p = theme::palette(ui.ctx());
                egui::Frame::new()
                    .fill(p.field)
                    .stroke(egui::Stroke::new(1.0, p.panel_border))
                    .corner_radius(4)
                    .inner_margin(egui::Margin::symmetric(8, 5))
                    .show(ui, |ui| {
                        ui.set_width(field_width);
                        ui.add(egui::Label::new(RichText::new(leading_ellipsis(&display, field_width, ui)).size(12.5)).truncate())
                            .on_hover_text(&display);
                    });
                ui.add_enabled_ui(idle, |ui| {
                    if ui.add(theme::tool_button("Choose…")).clicked() {
                        w.choose_output();
                    }
                    if ui.add(theme::tool_button("Auto")).on_hover_text("Name the file after the project / first video").clicked() {
                        w.set_output_path(None);
                    }
                });
            });
        });
    });
    // Progress and the last result share one row, so starting an export doesn't shift the layout.
    ui.horizontal(|ui| {
        ui.set_min_height(28.0);
        let mut show = w.export.show_command;
        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
            if ui.add(egui::Button::selectable(show, RichText::new("ffmpeg command").size(12.0))).clicked() {
                show = !show;
            }
            if idle && w.export.can_reveal && ui.add(theme::tool_button("Show in folder")).clicked() {
                w.reveal_output();
            }
            ui.with_layout(egui::Layout::left_to_right(egui::Align::Center), |ui| {
                if w.export.is_exporting {
                    let text = w.export.progress_text.clone().unwrap_or_default();
                    let galley_width = 260.0f32.min(ui.available_width() / 2.0);
                    let bar_width = (ui.available_width() - galley_width - 12.0).max(40.0);
                    ui.add(
                        egui::ProgressBar::new((w.export.progress / 100.0) as f32)
                            .desired_width(bar_width)
                            .desired_height(8.0)
                            .corner_radius(4),
                    );
                    ui.add_space(12.0);
                    ui.label(RichText::new(text).monospace().size(12.0));
                } else if let Some(status) = &w.export.status {
                    let color = if w.export.failed { theme::ERROR } else { theme::OK };
                    ui.add(egui::Label::new(RichText::new(status).color(color)).wrap().selectable(true));
                }
            });
        });
        if show != w.export.show_command {
            w.set_show_command(show);
        }
    });
    if w.export.show_command {
        let mut text = w.export.command_preview.clone().unwrap_or_default();
        egui::ScrollArea::vertical().id_salt("command").max_height(110.0).show(ui, |ui| {
            ui.add(
                egui::TextEdit::multiline(&mut text).font(theme::mono(11.0)).desired_width(f32::INFINITY).interactive(true).desired_rows(3),
            );
        });
    }
    if let Some(log) = w.export.log.clone() {
        let mut text = log;
        egui::ScrollArea::vertical().id_salt("log").max_height(110.0).show(ui, |ui| {
            ui.add(egui::TextEdit::multiline(&mut text).font(theme::mono(11.0)).desired_width(f32::INFINITY).desired_rows(3));
        });
    }
}

/// Cuts the start of a long path with "…" so its end (the file name) stays visible.
fn leading_ellipsis(text: &str, width: f32, ui: &egui::Ui) -> String {
    let font = theme::regular(12.5);
    let fits = |s: &str| ui.fonts_mut(|f| f.layout_no_wrap(s.to_string(), font.clone(), egui::Color32::WHITE).size().x) <= width - 4.0;
    if fits(text) {
        return text.to_string();
    }
    let chars: Vec<char> = text.chars().collect();
    let (mut lo, mut hi) = (0, chars.len());
    while lo < hi {
        let mid = (lo + hi) / 2;
        let candidate: String = std::iter::once('…').chain(chars[mid..].iter().copied()).collect();
        if fits(&candidate) { hi = mid } else { lo = mid + 1 }
    }
    std::iter::once('…').chain(chars[lo.min(chars.len())..].iter().copied()).collect()
}
