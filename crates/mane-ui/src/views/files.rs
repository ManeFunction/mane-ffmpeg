//! The Files panel: Video and Audio tabs with each file's color, details, status and audio-track choice.

use crate::logic::audio_drag::DraggedFile;
use crate::project::ProjectWindow;
use crate::project::files::FileItem;
use crate::theme;
use egui::{Color32, RichText, Sense, Vec2};
use mane_core::model::FileKind;

/// What a row asked for this frame (applied after the list is drawn).
enum RowAction {
    Preview(String),
    Append(String),
    Relink(String),
    Remove(String),
    Stream(String, i32),
    PressAudio(Option<DraggedFile>),
}

pub fn files_panel(w: &mut ProjectWindow, ui: &mut egui::Ui) {
    ui.horizontal(|ui| {
        theme::title(ui, "Files");
        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
            if w.files_tab == FileKind::Video {
                if ui.add(theme::tool_button("+ Add videos…")).clicked() {
                    w.pick_media();
                }
            } else if ui
                .add(theme::tool_button("+ Add audio…"))
                .on_hover_text("Audio files, or videos whose sound you want to use")
                .clicked()
            {
                w.pick_audio();
            }
        });
    });
    // Tabs
    ui.horizontal(|ui| {
        for (kind, name) in [(FileKind::Video, "Video"), (FileKind::Audio, "Audio")] {
            let selected = w.files_tab == kind;
            let text = RichText::new(name).size(13.0);
            let r = ui.add(egui::Button::selectable(selected, text).min_size(Vec2::new(0.0, 26.0)));
            if r.clicked() {
                w.files_tab = kind;
            }
        }
    });
    ui.separator();
    if let Some(err) = w.app.tools_error() {
        ui.add(egui::Label::new(RichText::new(err).size(12.0).color(theme::ERROR)).wrap());
    }
    let kind = w.files_tab;
    let items: Vec<FileItem> = if kind == FileKind::Video { w.sources.clone() } else { w.audio_files.clone() };
    let mut actions = Vec::new();
    let avail = ui.available_size();
    if items.is_empty() {
        let text = if kind == FileKind::Video {
            "Drop video files here or use “Add videos…”."
        } else {
            "Drop audio files here or use “Add audio…”, then drag one onto a clip to replace its sound."
        };
        ui.allocate_ui_with_layout(avail, egui::Layout::centered_and_justified(egui::Direction::TopDown), |ui| {
            theme::dim_wrapped(ui, text);
        });
        return;
    }
    egui::ScrollArea::vertical().id_salt(("files", kind as u8)).auto_shrink([false, false]).show(ui, |ui| {
        for item in &items {
            file_row(ui, w, item, &mut actions);
        }
    });
    for a in actions {
        match a {
            RowAction::Preview(id) => w.preview_source(&id),
            RowAction::Append(id) => w.append_source(&id),
            RowAction::Relink(id) => w.relink(kind, &id),
            RowAction::Remove(id) => w.remove_source(kind, &id),
            RowAction::Stream(id, index) => w.choose_audio_stream(kind, &id, index),
            RowAction::PressAudio(item) => {
                let _ = w.audio_drag.press(item);
            }
        }
    }
}

fn file_row(ui: &mut egui::Ui, _w: &ProjectWindow, item: &FileItem, actions: &mut Vec<RowAction>) {
    let id = item.id().to_string();
    let response = ui
        .scope_builder(egui::UiBuilder::new().sense(Sense::click_and_drag()), |ui| {
            ui.horizontal(|ui| {
                let (bar, _) = ui.allocate_exact_size(Vec2::new(4.0, 34.0), Sense::hover());
                ui.painter().rect_filled(bar, 2, item.color());
                ui.vertical(|ui| {
                    ui.add(egui::Label::new(RichText::new(item.name()).font(theme::semibold(13.0))).truncate())
                        .on_hover_text(&item.source.path);
                    let details = item.details();
                    if !details.is_empty() {
                        ui.add(egui::Label::new(RichText::new(details).size(12.0).color(theme::palette(ui.ctx()).dim)).truncate());
                    }
                    let status = item.status_text();
                    if !status.is_empty() {
                        ui.add(egui::Label::new(RichText::new(status).size(11.0).color(theme::WARN)).wrap());
                    }
                    if item.has_audio_choice() {
                        let choices = item.audio_choices();
                        let mut index = item.source.audio_stream_index;
                        let selected = choices.get(index as usize).cloned().unwrap_or_default();
                        egui::ComboBox::from_id_salt(("stream", &id))
                            .selected_text(RichText::new(selected).size(11.0))
                            .width(ui.available_width().min(240.0))
                            .show_ui(ui, |ui| {
                                for (i, c) in choices.iter().enumerate() {
                                    ui.selectable_value(&mut index, i as i32, RichText::new(c).size(11.0));
                                }
                            })
                            .response
                            .on_hover_text(if item.kind == FileKind::Video {
                                "Audio track used for this video"
                            } else {
                                "Audio track used from this file"
                            });
                        if index != item.source.audio_stream_index {
                            actions.push(RowAction::Stream(id.clone(), index));
                        }
                    }
                });
            });
        })
        .response;
    if response.hovered() {
        ui.painter().rect_filled(response.rect, 4, Color32::from_white_alpha(6));
    }
    if response.double_clicked() {
        actions.push(RowAction::Preview(id.clone()));
    }
    if item.kind == FileKind::Audio {
        if response.drag_started_by(egui::PointerButton::Primary) {
            let dragged = (!item.is_problem()).then(|| DraggedFile { id: id.clone(), name: item.name(), color: item.color() });
            actions.push(RowAction::PressAudio(dragged));
        }
        response.clone().on_hover_text("Drag onto a clip on the timeline to replace its sound");
    }
    response.context_menu(|ui| {
        let preview = if item.kind == FileKind::Audio { "Preview this audio" } else { "Preview this video" };
        if ui.button(preview).clicked() {
            actions.push(RowAction::Preview(id.clone()));
            ui.close();
        }
        if item.kind == FileKind::Video && ui.button("Add whole video to timeline").clicked() {
            actions.push(RowAction::Append(id.clone()));
            ui.close();
        }
        if ui.button("Relink…").clicked() {
            actions.push(RowAction::Relink(id.clone()));
            ui.close();
        }
        ui.separator();
        if ui.button("Remove from project").clicked() {
            actions.push(RowAction::Remove(id.clone()));
            ui.close();
        }
    });
    ui.add_space(2.0);
}
