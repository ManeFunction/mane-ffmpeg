//! The dialog windows a project window opens: Presets, Settings, the Homebrew setup and the update offer. Each is a
//! native child window drawn in its owner's frame.

pub mod changelog;
pub mod presets;
pub mod settings;
pub mod setup;
pub mod update;

use crate::project::ProjectWindow;

/// Debug automation: draws an open dialog inside the window instead, so a snapshot shows it.
pub fn show_inline(w: &mut ProjectWindow, ui: &mut egui::Ui, which: &str) {
    egui::Frame::new().fill(crate::theme::palette(ui.ctx()).window).inner_margin(16).show(ui, |ui| match which {
        "presets" => {
            if let Some(d) = &mut w.open.presets {
                presets::body(d, ui);
            }
        }
        "settings" => {
            if let Some(d) = &mut w.open.settings {
                settings::body(d, ui);
            }
        }
        "setup" => {
            if let Some(d) = &mut w.open.setup {
                setup::body(d, ui);
            }
        }
        _ => {
            if let Some(d) = &mut w.open.update {
                update::body(d, ui);
            }
        }
    });
}

pub fn show(w: &mut ProjectWindow, ctx: &egui::Context) {
    if w.open.presets.is_some() {
        presets::show(w, ctx);
    }
    if w.open.settings.is_some() {
        settings::show(w, ctx);
    }
    if w.open.setup.is_some() {
        setup::show(w, ctx);
    }
    if w.open.update.is_some() {
        update::show(w, ctx);
    }
}

/// A child window's builder: a fixed title and size, its owner's icon, centred-ish.
pub fn builder(title: &str, size: egui::Vec2, resizable: bool) -> egui::ViewportBuilder {
    egui::ViewportBuilder::default()
        .with_title(title)
        .with_inner_size(size)
        .with_min_inner_size(if resizable { size * 0.8 } else { size })
        .with_resizable(resizable)
        .with_icon(crate::app::icon())
}

impl ProjectWindow {
    pub fn show_presets(&mut self) {
        if self.open.presets.is_none() {
            self.open.presets = Some(presets::PresetsDialog::new(&self.app));
        }
    }

    pub fn show_settings(&mut self) {
        if self.open.settings.is_none() {
            self.open.settings = Some(settings::SettingsDialog::new(&self.app));
        }
    }
}

/// Takes a screenshot debug automation asked of the dialog `which`, from inside its frame.
pub fn take_shot(shot: &mut Option<(String, egui::ScreenshotCallback)>, which: &str, ctx: &egui::Context) {
    if shot.as_ref().is_some_and(|(w, _)| w == which) {
        let (_, callback) = shot.take().unwrap();
        ctx.send_viewport_cmd(egui::ViewportCommand::Screenshot(callback));
    }
}

/// Escape closes a dialog, like Cancel.
pub fn escape_pressed(ctx: &egui::Context) -> bool {
    !ctx.egui_wants_keyboard_input() && ctx.input(|i| i.key_pressed(egui::Key::Escape))
}
