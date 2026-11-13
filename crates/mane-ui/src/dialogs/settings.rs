//! Settings: ffmpeg and libmpv paths and status, the theme, notifications, the .mfvp association and updates.

use crate::project::ProjectWindow;
use crate::shared::{AppCommand, AppShared};
use crate::theme;
use egui::{RichText, Vec2};
use mane_core::mpv::library;
use mane_core::persistence::project_file::EXTENSION;
use mane_core::persistence::settings::ThemeChoice;
use mane_core::platform::{file_association, notifier};
use std::sync::Arc;

pub struct SettingsDialog {
    app: Arc<AppShared>,
    pub ffmpeg_path: String,
    pub ffprobe_path: String,
    pub lib_mpv_path: String,
    pub hardware_decoding: bool,
    pub notifications: bool,
    pub theme: ThemeChoice,
    pub ask_to_register: bool,
    pub check_for_updates: bool,
    can_register: bool,
    registered: bool,
    project_files_status: String,
    busy: bool,
}

impl SettingsDialog {
    pub fn new(app: &Arc<AppShared>) -> SettingsDialog {
        let s = app.settings().current().clone();
        let mut d = SettingsDialog {
            app: app.clone(),
            ffmpeg_path: s.ffmpeg_path.unwrap_or_default(),
            ffprobe_path: s.ffprobe_path.unwrap_or_default(),
            lib_mpv_path: s.lib_mpv_path.unwrap_or_default(),
            hardware_decoding: s.hardware_decoding,
            notifications: s.notifications,
            theme: s.theme,
            ask_to_register: s.ask_to_register_project_files,
            check_for_updates: s.check_for_updates,
            can_register: file_association::is_supported(),
            registered: false,
            project_files_status: String::new(),
            busy: false,
        };
        d.refresh_project_files();
        d
    }

    /// The player reads these only at startup, so changing them needs a restart.
    fn player_restart_needed(&self) -> bool {
        let s = self.app.settings();
        s.current().lib_mpv_path.clone().unwrap_or_default() != self.lib_mpv_path.trim()
            || s.current().hardware_decoding != self.hardware_decoding
    }

    fn ffmpeg_status(&self) -> String {
        match self.app.tools() {
            Some(t) => format!("{}\n{}\n{}", t.version_text, t.ffmpeg, t.ffprobe),
            None => self.app.tools_error().unwrap_or_else(|| "Not found".into()),
        }
    }

    fn mpv_status(&self) -> String {
        if library::is_loaded() {
            format!("libmpv {} (client API)\n{}", library::api_version().unwrap_or_default(), library::loaded_path().unwrap_or_default())
        } else {
            format!("{}\n{}", library::load_error().unwrap_or_default(), library::install_hint())
        }
    }

    fn refresh_project_files(&mut self) {
        if !self.can_register {
            self.project_files_status = format!("Only the packaged app can open .{EXTENSION} projects on double-click.");
            return;
        }
        self.registered = file_association::is_registered();
        self.project_files_status = if self.registered {
            format!("Double-clicking a .{EXTENSION} project opens it in Mane FFmpeg.")
        } else {
            format!("Double-clicking a .{EXTENSION} project doesn't open it in Mane FFmpeg.")
        };
    }

    /// Applies at once, like the other buttons here, not with Apply.
    fn toggle_project_files(&mut self) {
        let result = if self.registered { file_association::unregister() } else { file_association::register() };
        match result {
            Ok(()) => self.refresh_project_files(),
            Err(e) => {
                mane_core::persistence::app_log::write(&format!("Changing the .{EXTENSION} association failed: {e}"));
                self.project_files_status = format!("Couldn't change it: {e}");
            }
        }
    }

    /// Saves everything; the tools are looked for again and the windows restyle.
    fn apply(&mut self) {
        self.busy = true;
        let none_if_empty = |s: &str| (!s.trim().is_empty()).then(|| s.trim().to_string());
        let (f, p, m) = (none_if_empty(&self.ffmpeg_path), none_if_empty(&self.ffprobe_path), none_if_empty(&self.lib_mpv_path));
        let (hw, n, t, a, c) = (self.hardware_decoding, self.notifications, self.theme, self.ask_to_register, self.check_for_updates);
        self.app.settings().update(|s| {
            s.ffmpeg_path = f;
            s.ffprobe_path = p;
            s.lib_mpv_path = m;
            s.hardware_decoding = hw;
            s.notifications = n;
            s.theme = t;
            s.ask_to_register_project_files = a;
            s.check_for_updates = c;
        });
        self.app.send(AppCommand::SettingsApplied);
    }
}

pub fn show(w: &mut ProjectWindow, ctx: &egui::Context) {
    let id = egui::ViewportId::from_hash_of(("settings", w.id));
    let mut close = false;
    let mut open_setup = false;
    let d = w.open.settings.as_mut().unwrap();
    let mut shot = w.dialog_shot.take();
    ctx.show_viewport_immediate(id, super::builder("Settings", Vec2::new(640.0, 640.0), false), |ui, _| {
        let ctx = ui.ctx().clone();
        super::take_shot(&mut shot, "settings", &ctx);
        // The theme shows as soon as it's picked; closing without Apply goes back to the saved one.
        theme::apply(&ctx, d.theme);
        egui::CentralPanel::default().frame(egui::Frame::new().fill(theme::palette(&ctx).window).inner_margin(16)).show(ui, |ui| {
            egui::ScrollArea::vertical().show(ui, |ui| {
                let (c, o) = body(d, ui);
                close |= c;
                open_setup |= o;
            });
        });
        if ctx.input(|i| i.viewport().close_requested()) || super::escape_pressed(&ctx) {
            close = true;
        }
    });
    w.dialog_shot = w.dialog_shot.take().or(shot);
    if close {
        w.open.settings = None;
        // Back to the saved theme in this window if it wasn't applied.
        let saved = w.app.settings().current().theme;
        theme::apply(ctx, saved);
    }
    if open_setup && w.open.setup.is_none() {
        w.open.setup = Some(super::setup::SetupDialog::new(&w.app, w.waker.clone()));
    }
}

fn status_box(ui: &mut egui::Ui, text: String) {
    let p = theme::palette(ui.ctx());
    egui::Frame::new().fill(p.window).stroke(egui::Stroke::new(1.0, p.panel_border)).corner_radius(4).inner_margin(10).show(ui, |ui| {
        ui.set_width(ui.available_width());
        ui.add(egui::Label::new(RichText::new(text).size(12.0)).wrap().selectable(true));
    });
}

fn section(ui: &mut egui::Ui, title: &str, add: impl FnOnce(&mut egui::Ui)) {
    theme::panel_frame(ui.ctx()).inner_margin(14).corner_radius(8).show(ui, |ui| {
        ui.set_width(ui.available_width());
        theme::title(ui, title);
        add(ui);
    });
    ui.add_space(12.0);
}

fn path_row(ui: &mut egui::Ui, label: &str, value: &mut String, hint: &str) {
    ui.horizontal(|ui| {
        ui.add_sized([110.0, 26.0], egui::Label::new(label));
        ui.add(egui::TextEdit::singleline(value).hint_text(hint).desired_width(f32::INFINITY));
    });
}

/// Returns (close, open the setup dialog).
pub fn body(d: &mut SettingsDialog, ui: &mut egui::Ui) -> (bool, bool) {
    let (mut close, mut setup) = (false, false);
    section(ui, "FFMPEG", |ui| {
        status_box(ui, d.ffmpeg_status());
        path_row(ui, "ffmpeg path", &mut d.ffmpeg_path, "automatic (PATH, Homebrew, /usr/bin…)");
        path_row(ui, "ffprobe path", &mut d.ffprobe_path, "automatic (next to ffmpeg)");
    });
    section(ui, "PREVIEW (LIBMPV)", |ui| {
        status_box(ui, d.mpv_status());
        path_row(ui, "libmpv path", &mut d.lib_mpv_path, "automatic (Homebrew, MacPorts, IINA, system)");
        ui.checkbox(&mut d.hardware_decoding, "Hardware video decoding for preview");
        if d.player_restart_needed() {
            ui.label(RichText::new("Restart the app after applying to use the new player settings.").size(12.0).color(theme::WARN));
        }
    });
    section(ui, "GENERAL", |ui| {
        ui.horizontal(|ui| {
            ui.add_sized([110.0, 26.0], egui::Label::new("Theme"));
            egui::ComboBox::from_id_salt("theme").selected_text(d.theme.name()).width(120.0).show_ui(ui, |ui| {
                for t in ThemeChoice::ALL {
                    ui.selectable_value(&mut d.theme, t, t.name());
                }
            });
        });
        ui.horizontal(|ui| {
            ui.checkbox(&mut d.notifications, "System notification when an export ends");
            ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                if ui.add(egui::Button::new("Test").min_size(Vec2::new(96.0, 26.0))).clicked() {
                    notifier::notify("Mane FFmpeg", "Notifications work. You'll see one when an export finishes.");
                }
            });
        });
        ui.horizontal(|ui| {
            let action = if d.registered { "Unregister" } else { "Register" };
            ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                if ui.add_enabled(d.can_register, egui::Button::new(action).min_size(Vec2::new(96.0, 26.0))).clicked() {
                    d.toggle_project_files();
                }
                ui.with_layout(egui::Layout::left_to_right(egui::Align::Center), |ui| {
                    ui.add(egui::Label::new(d.project_files_status.clone()).wrap());
                });
            });
        });
        ui.add_enabled_ui(d.can_register, |ui| ui.checkbox(&mut d.ask_to_register, "Offer to register project files at startup"));
        ui.checkbox(&mut d.check_for_updates, "Check for updates at startup");
    });
    ui.horizontal(|ui| {
        if super::setup::is_needed(&d.app) && ui.button("Install Missing Components…").clicked() {
            setup = true;
        }
        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
            if ui.add_enabled(!d.busy, theme::accent_button("Apply").min_size(Vec2::new(80.0, 28.0))).clicked() {
                d.apply();
                close = true;
            }
            if ui.add(egui::Button::new("Cancel").min_size(Vec2::new(80.0, 28.0))).clicked() {
                close = true;
            }
        });
    });
    (close, setup)
}
