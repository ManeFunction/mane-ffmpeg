//! The preset editor: a list on the left, the selected preset's fields on the right.

use crate::project::ProjectWindow;
use crate::shared::AppShared;
use crate::theme::{self, Icon};
use egui::{RichText, Vec2};
use mane_core::ffmpeg::preset_analysis::PresetAnalysis;
use mane_core::model::{Preset, ids, preset::DEFAULT_AUDIO_FALLBACK};
use std::sync::Arc;

pub const EXTENSION_SUGGESTIONS: [&str; 7] = ["", "mp4", "mkv", "mov", "webm", "m4v", "ts"];

/// What to do once unsaved edits are dealt with.
#[derive(Clone, Debug)]
enum After {
    Select(String),
    Add,
    Duplicate,
    Restore,
    Close,
}

pub struct PresetsDialog {
    app: Arc<AppShared>,
    pub presets: Vec<Preset>,
    pub selected: Option<String>,
    pub name: String,
    pub arguments: String,
    pub extension: String,
    pub audio_fallback: String,
    pub is_modified: bool,
    /// Unsaved edits are being asked about before `After`.
    asking: Option<After>,
    confirm_restore: bool,
    seen_generation: u64,
    /// A preset row being dragged to a new place, and where it would land.
    dragging: Option<String>,
    drop_index: Option<usize>,
}

impl PresetsDialog {
    pub fn new(app: &Arc<AppShared>) -> PresetsDialog {
        let mut d = PresetsDialog {
            app: app.clone(),
            presets: vec![],
            selected: None,
            name: String::new(),
            arguments: String::new(),
            extension: String::new(),
            audio_fallback: String::new(),
            is_modified: false,
            asking: None,
            confirm_restore: false,
            seen_generation: 0,
            dragging: None,
            drop_index: None,
        };
        d.reload(None);
        d
    }

    fn reload(&mut self, select: Option<String>) {
        let store = self.app.presets();
        self.presets = store.presets().to_vec();
        self.seen_generation = store.generation;
        drop(store);
        let id = select.filter(|id| self.presets.iter().any(|p| &p.id == id)).or_else(|| self.presets.first().map(|p| p.id.clone()));
        self.load(id);
    }

    fn load(&mut self, id: Option<String>) {
        let p = id.as_ref().and_then(|id| self.presets.iter().find(|p| &p.id == id)).cloned();
        self.selected = id;
        self.name = p.as_ref().map(|p| p.name.clone()).unwrap_or_default();
        self.arguments = p.as_ref().map(|p| p.arguments.clone()).unwrap_or_default();
        self.extension = p.as_ref().map(|p| p.extension.clone()).unwrap_or_default();
        self.audio_fallback = p.as_ref().map(|p| p.audio_fallback_arguments.clone()).unwrap_or_default();
        self.is_modified = false;
    }

    fn index(&self) -> Option<usize> {
        self.presets.iter().position(|p| Some(&p.id) == self.selected.as_ref())
    }

    pub fn analysis(&self) -> (String, String) {
        let a = PresetAnalysis::of_text(&self.arguments, &self.audio_fallback);
        let text = match &a.arguments {
            Some(args) => {
                let filters: Vec<&str> = [args.video_filter.as_deref(), args.audio_filter.as_deref()].into_iter().flatten().collect();
                let filters = if filters.is_empty() {
                    String::new()
                } else {
                    format!("\nFilters applied after joining clips: {}", filters.join(", "))
                };
                let ext = if self.extension.trim().is_empty() {
                    "Output: same container as the first video.".to_string()
                } else {
                    format!("Output: .{} file.", self.extension.trim().trim_start_matches('.'))
                };
                format!(
                    "{}\n{}\n{ext}{filters}",
                    a.video_summary().unwrap_or_default(),
                    a.audio_summary(&self.audio_fallback).unwrap_or_default()
                )
            }
            None => String::new(),
        };
        let mut warnings: Vec<String> = self.app.presets().save_error.iter().cloned().collect();
        warnings.extend(a.errors);
        warnings.extend(a.warnings);
        (text, warnings.join("\n"))
    }

    pub fn save(&mut self) {
        let Some(id) = self.selected.clone() else { return };
        let Some(preset) = self.presets.iter().find(|p| p.id == id).cloned() else { return };
        let updated = Preset {
            name: if self.name.trim().is_empty() { "Untitled preset".into() } else { self.name.trim().into() },
            arguments: self.arguments.trim().into(),
            extension: self.extension.trim().trim_start_matches('.').into(),
            audio_fallback_arguments: if self.audio_fallback.trim().is_empty() {
                DEFAULT_AUDIO_FALLBACK.into()
            } else {
                self.audio_fallback.trim().into()
            },
            ..preset
        };
        self.app.presets().save(updated.clone());
        self.reload(Some(updated.id));
    }

    fn add(&mut self) {
        let preset = Preset {
            id: ids::new(),
            name: "New preset".into(),
            arguments: "-c:v libx264 -crf 20 -preset medium -c:a aac -b:a 192k".into(),
            extension: "mp4".into(),
            audio_fallback_arguments: DEFAULT_AUDIO_FALLBACK.into(),
        };
        self.app.presets().save(preset.clone());
        self.reload(Some(preset.id));
    }

    fn duplicate(&mut self) {
        let Some(p) = self.index().map(|i| self.presets[i].clone()) else { return };
        let copy = Preset { id: ids::new(), name: format!("{} copy", p.name), ..p };
        self.app.presets().save(copy.clone());
        self.reload(Some(copy.id));
    }

    fn delete(&mut self) {
        let Some(index) = self.index() else { return };
        let id = self.presets[index].id.clone();
        self.app.presets().delete(&id);
        let next = if index > 0 { self.app.presets().presets().get(index - 1).map(|p| p.id.clone()) } else { None };
        self.reload(next);
    }

    pub fn move_selected_to(&mut self, index: usize) {
        let Some(id) = self.selected.clone() else { return };
        // Unsaved edits stay in the editor.
        let edits = (self.name.clone(), self.arguments.clone(), self.extension.clone(), self.audio_fallback.clone(), self.is_modified);
        self.app.presets().move_to(&id, index);
        self.reload(Some(id));
        (self.name, self.arguments, self.extension, self.audio_fallback, self.is_modified) = edits;
    }

    fn restore(&mut self) {
        self.app.presets().restore_built_ins();
        self.reload(self.selected.clone());
    }

    /// Runs `after`, asking about unsaved edits first.
    fn then(&mut self, after: After) -> bool {
        if self.is_modified {
            self.asking = Some(after);
            return false;
        }
        self.run(after)
    }

    /// Returns true when the dialog should close.
    fn run(&mut self, after: After) -> bool {
        match after {
            After::Select(id) => self.load(Some(id)),
            After::Add => self.add(),
            After::Duplicate => self.duplicate(),
            After::Restore => self.restore(),
            After::Close => return true,
        }
        false
    }
}

pub fn show(w: &mut ProjectWindow, ctx: &egui::Context) {
    let id = egui::ViewportId::from_hash_of(("presets", w.id));
    let mut close = false;
    let d = w.open.presets.as_mut().unwrap();
    if d.app.presets().generation != d.seen_generation {
        let sel = d.selected.clone();
        let edits = (d.name.clone(), d.arguments.clone(), d.extension.clone(), d.audio_fallback.clone(), d.is_modified);
        d.reload(sel);
        if edits.4 {
            (d.name, d.arguments, d.extension, d.audio_fallback, d.is_modified) = edits;
        }
    }
    let mut shot = w.dialog_shot.take();
    ctx.show_viewport_immediate(id, super::builder("Export Presets", Vec2::new(860.0, 560.0), true), |ui, _| {
        let ctx = ui.ctx().clone();
        super::take_shot(&mut shot, "presets", &ctx);
        crate::theme::apply(&ctx, d.app.settings().current().theme);
        egui::CentralPanel::default().frame(egui::Frame::new().fill(theme::palette(&ctx).window).inner_margin(12)).show(ui, |ui| {
            close |= body(d, ui);
        });
        if ctx.input(|i| i.viewport().close_requested()) || super::escape_pressed(&ctx) {
            ctx.send_viewport_cmd(egui::ViewportCommand::CancelClose);
            close |= d.then(After::Close);
        }
        close |= edits_modal(d, &ctx);
        restore_modal(d, &ctx);
    });
    w.dialog_shot = w.dialog_shot.take().or(shot);
    if close {
        w.open.presets = None;
    }
}

fn edits_modal(d: &mut PresetsDialog, ctx: &egui::Context) -> bool {
    let Some(after) = d.asking.clone() else { return false };
    let mut choice: Option<i32> = None;
    egui::Modal::new(egui::Id::unique("preset-edits")).show(ctx, |ui| {
        ui.set_width(400.0);
        let name = d.selected.as_ref().and_then(|id| d.presets.iter().find(|p| &p.id == id)).map(|p| p.name.clone()).unwrap_or_default();
        ui.label(RichText::new(format!("Save changes to “{name}”?")).font(theme::semibold(15.0)));
        ui.label("Your edits to this preset will be lost if you don't save them.");
        ui.horizontal(|ui| {
            if ui.add(theme::accent_button("Save")).clicked() {
                choice = Some(0);
            }
            if ui.button("Don't save").clicked() {
                choice = Some(1);
            }
            if ui.button("Cancel").clicked() {
                choice = Some(2);
            }
        });
    });
    let Some(c) = choice else { return false };
    d.asking = None;
    match c {
        0 => {
            d.save();
            d.run(after)
        }
        1 => {
            d.is_modified = false;
            d.run(after)
        }
        _ => false,
    }
}

fn restore_modal(d: &mut PresetsDialog, ctx: &egui::Context) {
    if !d.confirm_restore {
        return;
    }
    let mut choice: Option<bool> = None;
    egui::Modal::new(egui::Id::unique("restore-presets")).show(ctx, |ui| {
        ui.set_width(400.0);
        ui.label(RichText::new("Restore the built-in presets?").font(theme::semibold(15.0)));
        let names: Vec<String> = Preset::built_ins().iter().map(|p| format!("“{}”", p.name)).collect();
        ui.add(egui::Label::new(format!(
            "The built-in presets {} go back to their original settings, and deleted ones come back. Presets you added stay as they are.",
            names.join(", ")
        )).wrap());
        ui.horizontal(|ui| {
            if ui.add(theme::accent_button("Restore")).clicked() {
                choice = Some(true);
            }
            if ui.button("Cancel").clicked() {
                choice = Some(false);
            }
        });
    });
    if let Some(c) = choice {
        d.confirm_restore = false;
        if c {
            d.then(After::Restore);
        }
    }
}

/// Returns true when the dialog should close.
pub fn body(d: &mut PresetsDialog, ui: &mut egui::Ui) -> bool {
    let close = false;
    egui::Panel::left("preset-list")
        .exact_size(270.0)
        .frame(egui::Frame::new().inner_margin(egui::Margin { right: 12, ..Default::default() }))
        .show(ui, |ui| {
            egui::Panel::bottom("preset-buttons")
                .frame(egui::Frame::new().inner_margin(egui::Margin { top: 8, ..Default::default() }))
                .show(ui, |ui| {
                    ui.columns(3, |c| {
                        if c[0].add_sized([c[0].available_width(), 28.0], egui::Button::new("Add")).clicked() {
                            d.then(After::Add);
                        }
                        if c[1].add_sized([c[1].available_width(), 28.0], egui::Button::new("Duplicate")).clicked() {
                            d.then(After::Duplicate);
                        }
                        if c[2].add_sized([c[2].available_width(), 28.0], egui::Button::new("Delete")).clicked() {
                            d.delete();
                        }
                    });
                    ui.horizontal(|ui| {
                        let index = d.index();
                        let w = (ui.available_width() - 2.0 * 36.0 - 12.0).max(60.0);
                        if ui.add_sized([w, 28.0], egui::Button::new("Restore built-in presets")).clicked() {
                            d.confirm_restore = true;
                        }
                        let up = ui
                            .add_enabled(index.is_some_and(|i| i > 0), egui::Button::new("").min_size(Vec2::new(36.0, 28.0)))
                            .on_hover_text("Move up");
                        theme::paint_icon(
                            ui.painter(),
                            egui::Rect::from_center_size(up.rect.center(), Vec2::splat(14.0)),
                            Icon::ArrowUp,
                            theme::palette(ui.ctx()).text,
                        );
                        if up.clicked() {
                            d.move_selected_to(index.unwrap() - 1);
                        }
                        let down = ui
                            .add_enabled(
                                index.is_some_and(|i| i + 1 < d.presets.len()),
                                egui::Button::new("").min_size(Vec2::new(36.0, 28.0)),
                            )
                            .on_hover_text("Move down");
                        theme::paint_icon(
                            ui.painter(),
                            egui::Rect::from_center_size(down.rect.center(), Vec2::splat(14.0)),
                            Icon::ArrowDown,
                            theme::palette(ui.ctx()).text,
                        );
                        if down.clicked() {
                            d.move_selected_to(index.unwrap() + 1);
                        }
                    });
                });
            theme::panel_frame(ui.ctx()).show(ui, |ui| {
                ui.set_min_size(ui.available_size());
                egui::ScrollArea::vertical().auto_shrink([false, false]).show(ui, |ui| preset_list(d, ui));
            });
        });
    egui::CentralPanel::default().frame(egui::Frame::new()).show(ui, |ui| {
        if d.selected.is_none() {
            return;
        }
        egui::ScrollArea::vertical().show(ui, |ui| editor(d, ui));
    });
    close
}

fn preset_list(d: &mut PresetsDialog, ui: &mut egui::Ui) {
    let mut rows = Vec::new();
    for p in d.presets.clone() {
        let selected = d.selected.as_ref() == Some(&p.id);
        let r = ui
            .scope_builder(egui::UiBuilder::new().sense(egui::Sense::click_and_drag()), |ui| {
                ui.set_width(ui.available_width());
                ui.vertical(|ui| {
                    ui.label(RichText::new(&p.name).font(theme::semibold(13.0)));
                    let c = theme::palette(ui.ctx()).dim;
                    ui.add(egui::Label::new(RichText::new(&p.arguments).size(11.0).color(c)).truncate());
                });
            })
            .response;
        if selected {
            ui.painter().rect_filled(r.rect.expand(2.0), 4, ui.visuals().selection.bg_fill.gamma_multiply(0.6));
            // Draw the row again over its highlight.
            ui.painter().text(r.rect.left_top(), egui::Align2::LEFT_TOP, &p.name, theme::semibold(13.0), theme::palette(ui.ctx()).text);
        }
        if r.clicked() && !selected {
            d.then(After::Select(p.id.clone()));
        }
        if r.drag_started() {
            if !selected {
                d.then(After::Select(p.id.clone()));
            }
            d.dragging = Some(p.id.clone());
        }
        rows.push(r.rect);
        ui.add_space(4.0);
    }
    // Dragging a preset: a line where it would land.
    if d.dragging.is_some() {
        if let Some(pos) = ui.input(|i| i.pointer.interact_pos()) {
            let index = rows.iter().position(|r| pos.y < r.center().y).unwrap_or(rows.len());
            d.drop_index = Some(index);
            let y = if index < rows.len() { rows[index].top() - 2.0 } else { rows.last().map(|r| r.bottom() + 2.0).unwrap_or(0.0) };
            let x = rows.first().map(|r| (r.left(), r.right())).unwrap_or((0.0, 0.0));
            ui.painter().line_segment([egui::pos2(x.0, y), egui::pos2(x.1, y)], egui::Stroke::new(2.0, theme::ACCENT));
        }
        if !ui.input(|i| i.pointer.primary_down())
            && let (Some(_), Some(drop)) = (d.dragging.take(), d.drop_index.take())
            && let Some(from) = d.index()
        {
            let to = if drop > from { drop - 1 } else { drop };
            if to != from {
                d.move_selected_to(to);
            }
        }
    }
}

fn editor(d: &mut PresetsDialog, ui: &mut egui::Ui) {
    let mut changed = false;
    theme::title(ui, "Name");
    changed |= ui.add(egui::TextEdit::singleline(&mut d.name).desired_width(f32::INFINITY)).changed();
    ui.add_space(4.0);
    theme::title(ui, "ffmpeg output options");
    changed |= ui
        .add(
            egui::TextEdit::multiline(&mut d.arguments)
                .font(theme::mono(12.0))
                .desired_width(f32::INFINITY)
                .desired_rows(3)
                .hint_text("-c:v libx265 -crf 28 -preset slow -c:a copy"),
        )
        .changed();
    // The options are one line: Enter doesn't add line breaks.
    if d.arguments.contains('\n') {
        d.arguments = d.arguments.replace('\n', " ");
    }
    theme::dim_wrapped(
        ui,
        "Everything ffmpeg accepts between the inputs and the output file. Inputs, -map, -ss/-t and the output name are filled in by the editor; -vf/-af are applied after the clips are joined.",
    );
    ui.columns(2, |c| {
        theme::title(&mut c[0], "File extension");
        c[0].horizontal(|ui| {
            changed |= ui
                .add(
                    egui::TextEdit::singleline(&mut d.extension)
                        .desired_width((ui.available_width() - 34.0).max(40.0))
                        .hint_text("empty = same as the first video"),
                )
                .changed();
            let menu = egui::containers::menu::MenuButton::new("   ").ui(ui, |ui| {
                for s in EXTENSION_SUGGESTIONS {
                    if ui.button(if s.is_empty() { "(same as source)" } else { s }).clicked() {
                        d.extension = s.into();
                        changed = true;
                        ui.close();
                    }
                }
            });
            theme::paint_icon(
                ui.painter(),
                egui::Rect::from_center_size(menu.0.rect.center(), Vec2::splat(13.0)),
                Icon::ArrowDown,
                theme::palette(ui.ctx()).text,
            );
        });
        theme::title(&mut c[1], "Audio when copying isn't possible");
        changed |=
            c[1].add(egui::TextEdit::singleline(&mut d.audio_fallback).font(theme::mono(12.0)).desired_width(f32::INFINITY)).changed();
    });
    if changed {
        d.is_modified = d.selected.is_some();
    }
    let (analysis, warnings) = d.analysis();
    theme::panel_frame(ui.ctx()).inner_margin(10).show(ui, |ui| {
        ui.set_width(ui.available_width());
        theme::title(ui, "What this preset does");
        ui.add(egui::Label::new(RichText::new(analysis).size(12.0)).wrap());
        if !warnings.is_empty() {
            ui.add(egui::Label::new(RichText::new(warnings).size(12.0).color(theme::WARN)).wrap());
        }
    });
    ui.horizontal(|ui| {
        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
            // Both labels take the same width, so the button doesn't change size with the state.
            let label = if d.is_modified { "Save preset" } else { "Preset saved" };
            if ui.add_enabled(d.is_modified, theme::accent_button(label).min_size(Vec2::new(110.0, 28.0))).clicked() {
                d.save();
            }
            if ui.add_enabled(d.is_modified, egui::Button::new("Revert").min_size(Vec2::new(0.0, 28.0))).clicked() {
                let sel = d.selected.clone();
                d.load(sel);
            }
        })
    });
}
