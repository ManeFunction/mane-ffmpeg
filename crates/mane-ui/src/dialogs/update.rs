//! Offers a newer release: shows what changed since this version, downloads it, and asks before the app restarts to
//! install it. A copy that can't replace itself sends the user to the release page instead.

use crate::project::ProjectWindow;
use crate::shared::AppCommand;
use crate::theme;
use egui::{RichText, Vec2};
use mane_core::persistence::app_log;
use mane_core::updates::checker::UpdateOffer;
use mane_core::updates::installer::{self, PreparedUpdate};
use mane_core::version::Version;
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{Receiver, channel};
use std::sync::{Arc, Mutex};

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum UpdateStep {
    Offered,
    Downloading,
    /// Downloaded and unpacked; restarting installs it.
    Ready,
    Failed,
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum UpdateChoice {
    None,
    RestartNow,
    InstallOnQuit,
}

enum Msg {
    Progress(f64),
    Done(Result<PreparedUpdate, String>),
}

pub struct UpdateDialog {
    pub offer: UpdateOffer,
    pub current: Version,
    cannot_install: Option<String>,
    pub step: UpdateStep,
    pub do_not_remind: bool,
    pub progress: f64,
    pub status: Option<String>,
    pub status_is_error: bool,
    pub prepared: Option<PreparedUpdate>,
    pub choice: UpdateChoice,
    pub closed: bool,
    cancel: Arc<AtomicBool>,
    rx: Option<Receiver<Msg>>,
    /// Where the outcome goes once the window closes: the app's updater.
    pub outcome: Arc<Mutex<Option<UpdateOutcome>>>,
}

/// What the user chose, handed back to the updater.
#[derive(Clone, Debug)]
pub struct UpdateOutcome {
    pub version: Version,
    pub choice: UpdateChoice,
    pub do_not_remind: bool,
    pub prepared: Option<PreparedUpdate>,
}

impl UpdateDialog {
    pub fn new(
        offer: UpdateOffer,
        current: Version,
        installed: Option<PathBuf>,
        outcome: Arc<Mutex<Option<UpdateOutcome>>>,
    ) -> UpdateDialog {
        let cannot_install = if offer.asset.is_none() {
            Some(format!("This release has no download for {}.", installer::runtime_id().unwrap_or_else(|| "this system".into())))
        } else {
            installer::cannot_install_reason(installed.as_deref())
        };
        UpdateDialog {
            status: cannot_install.clone(),
            cannot_install,
            offer,
            current,
            step: UpdateStep::Offered,
            do_not_remind: false,
            progress: 0.0,
            status_is_error: false,
            prepared: None,
            choice: UpdateChoice::None,
            closed: false,
            cancel: Arc::new(AtomicBool::new(false)),
            rx: None,
            outcome,
        }
    }

    fn can_install(&self) -> bool {
        self.cannot_install.is_none()
    }

    pub fn primary_text(&self) -> &'static str {
        match self.step {
            UpdateStep::Ready => "Restart Now",
            UpdateStep::Failed => "Open Download Page",
            _ if self.can_install() => "Update",
            _ => "Open Download Page",
        }
    }

    pub fn secondary_text(&self) -> &'static str {
        match self.step {
            UpdateStep::Downloading => "Cancel",
            UpdateStep::Ready => "Install on Quit",
            UpdateStep::Failed => "Close",
            UpdateStep::Offered => "Later",
        }
    }

    fn primary(&mut self, ctx: &egui::Context) {
        match self.step {
            UpdateStep::Ready => self.close(UpdateChoice::RestartNow),
            UpdateStep::Downloading => {}
            _ if self.step == UpdateStep::Failed || !self.can_install() => ctx.open_url(egui::OpenUrl::new_tab(&self.offer.page)),
            _ => self.download(ctx),
        }
    }

    fn secondary(&mut self) {
        match self.step {
            UpdateStep::Downloading => self.cancel.store(true, Ordering::Release),
            UpdateStep::Ready => self.close(UpdateChoice::InstallOnQuit),
            _ => self.close(UpdateChoice::None),
        }
    }

    fn download(&mut self, ctx: &egui::Context) {
        self.step = UpdateStep::Downloading;
        self.status_is_error = false;
        self.status = Some("Downloading…".into());
        self.progress = 0.0;
        self.cancel.store(false, Ordering::Release);
        let (tx, rx) = channel();
        self.rx = Some(rx);
        let (asset, version, cancel, ctx) = (self.offer.asset.clone().unwrap(), self.offer.version, self.cancel.clone(), ctx.clone());
        std::thread::spawn(move || {
            let agent = mane_core::updates::checker::http_agent();
            let progress_tx = tx.clone();
            let c2 = ctx.clone();
            let result = installer::prepare(
                &agent,
                &asset,
                version,
                &mut |p| {
                    let _ = progress_tx.send(Msg::Progress(p));
                    c2.request_repaint();
                },
                &|| cancel.load(Ordering::Acquire),
            );
            let _ = tx.send(Msg::Done(result));
            ctx.request_repaint();
        });
    }

    fn poll(&mut self) {
        let Some(rx) = &self.rx else { return };
        let msgs: Vec<Msg> = rx.try_iter().collect();
        for m in msgs {
            match m {
                Msg::Progress(p) => {
                    self.progress = p * 100.0;
                    let size = self.offer.asset.as_ref().map(|a| a.size).unwrap_or(0) as f64;
                    self.status = Some(format!("Downloading… {:.0} of {:.0} MB", p * size / 1e6, size / 1e6));
                }
                Msg::Done(Ok(prepared)) => {
                    self.prepared = Some(prepared);
                    self.step = UpdateStep::Ready;
                    self.status = Some(
                        "The update is ready. Mane FFmpeg restarts to install it: each window asks to save its changes first, and saved projects open again after the restart."
                            .into(),
                    );
                    self.rx = None;
                }
                Msg::Done(Err(e)) => {
                    installer::clean_up();
                    if self.cancel.load(Ordering::Acquire) {
                        self.step = UpdateStep::Offered;
                        self.status = None;
                    } else {
                        app_log::write(&format!("Update: downloading {} failed: {e}", self.offer.version));
                        self.step = UpdateStep::Failed;
                        self.status_is_error = true;
                        self.status = Some(format!("Couldn't download the update: {e}"));
                    }
                    self.rx = None;
                }
            }
        }
    }

    fn close(&mut self, choice: UpdateChoice) {
        self.choice = choice;
        self.closed = true;
        *self.outcome.lock().unwrap() =
            Some(UpdateOutcome { version: self.offer.version, choice, do_not_remind: self.do_not_remind, prepared: self.prepared.clone() });
    }
}

impl Drop for UpdateDialog {
    /// Stops a download in progress (the window is closing).
    fn drop(&mut self) {
        self.cancel.store(true, Ordering::Release);
        if !self.closed {
            self.close(UpdateChoice::None);
        }
    }
}

pub fn show(w: &mut ProjectWindow, ctx: &egui::Context) {
    let id = egui::ViewportId::from_hash_of(("update", w.id));
    let d = w.open.update.as_mut().unwrap();
    d.poll();
    let theme_choice = w.app.settings().current().theme;
    let mut shot = w.dialog_shot.take();
    ctx.show_viewport_immediate(id, super::builder("Update Available", Vec2::new(560.0, 560.0), false), |ui, _| {
        let ctx = ui.ctx().clone();
        super::take_shot(&mut shot, "update", &ctx);
        theme::apply(&ctx, theme_choice);
        egui::CentralPanel::default()
            .frame(egui::Frame::new().fill(theme::palette(&ctx).window).inner_margin(20))
            .show(ui, |ui| body(d, ui));
        if ctx.input(|i| i.viewport().close_requested()) || super::escape_pressed(&ctx) {
            if d.step == UpdateStep::Downloading {
                d.cancel.store(true, Ordering::Release);
            }
            d.close(UpdateChoice::None);
        }
    });
    w.dialog_shot = w.dialog_shot.take().or(shot);
    if d.closed {
        w.open.update = None;
        w.app.send(AppCommand::UpdateClosed);
    }
}

pub fn body(d: &mut UpdateDialog, ui: &mut egui::Ui) {
    ui.label(RichText::new(format!("Mane FFmpeg {} is available", d.offer.version)).font(theme::semibold(16.0)));
    ui.label(RichText::new(format!("You have {}. What's new:", d.current)).color(theme::palette(ui.ctx()).text.gamma_multiply(0.85)));
    ui.add_space(8.0);
    theme::panel_frame(ui.ctx()).inner_margin(egui::Margin::symmetric(14, 12)).show(ui, |ui| {
        ui.set_width(ui.available_width());
        egui::ScrollArea::vertical().max_height(300.0).min_scrolled_height(300.0).show(ui, |ui| {
            let changelog = d.offer.changelog.clone().unwrap_or_else(|| "See the release page for what's new.".into());
            super::changelog::show(ui, &changelog);
        });
    });
    if d.step == UpdateStep::Downloading {
        ui.add(egui::ProgressBar::new((d.progress / 100.0) as f32).desired_height(8.0));
    }
    if let Some(s) = &d.status {
        let color = if d.status_is_error { theme::ERROR } else { theme::palette(ui.ctx()).text };
        ui.add(egui::Label::new(RichText::new(s).size(12.0).color(color)).wrap().selectable(true));
    }
    ui.add_space(4.0);
    ui.horizontal(|ui| {
        if matches!(d.step, UpdateStep::Offered | UpdateStep::Failed) {
            ui.checkbox(&mut d.do_not_remind, "Do not remind for this version");
        }
        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
            let ctx = ui.ctx().clone();
            if ui
                .add_enabled(d.step != UpdateStep::Downloading, theme::accent_button(d.primary_text()).min_size(Vec2::new(90.0, 28.0)))
                .clicked()
            {
                d.primary(&ctx);
            }
            if ui.add(egui::Button::new(d.secondary_text()).min_size(Vec2::new(90.0, 28.0))).clicked() {
                d.secondary();
            }
        });
    });
}
