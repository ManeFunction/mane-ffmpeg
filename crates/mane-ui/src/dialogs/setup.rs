//! Installs missing components (ffmpeg, libmpv) with Homebrew on macOS, and Homebrew itself first when it's missing.
//! Homebrew's installer runs in Terminal; `brew install` runs here with its output shown.

use crate::shared::AppShared;
use crate::theme;
use egui::{RichText, Vec2};
use mane_core::mpv::library;
use mane_core::persistence::{app_log, app_paths};
use mane_core::platform::homebrew;
use std::collections::VecDeque;
use std::path::PathBuf;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{Receiver, channel};

const LOG_LINES: usize = 300;

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum SetupStep {
    /// Homebrew is missing; its installer is offered first.
    NeedsHomebrew,
    /// Homebrew's installer runs in Terminal.
    WaitingForHomebrew,
    Ready,
    Installing,
    Installed,
    Failed,
}

/// A missing tool and the Homebrew formula that provides it.
#[derive(Clone, Debug)]
pub struct SetupComponent {
    pub name: &'static str,
    pub formula: &'static str,
    pub purpose: &'static str,
    pub selected: bool,
}

pub fn all_components() -> Vec<SetupComponent> {
    vec![
        SetupComponent { name: "ffmpeg", formula: "ffmpeg", purpose: "Reads, analyzes and exports your videos. Required.", selected: true },
        SetupComponent {
            name: "libmpv",
            formula: "mpv",
            purpose: "Plays the preview. Editing and exporting work without it.",
            selected: true,
        },
    ]
}

pub fn missing_components(app: &AppShared) -> Vec<SetupComponent> {
    all_components().into_iter().filter(|c| if c.formula == "ffmpeg" { app.tools().is_none() } else { !library::is_loaded() }).collect()
}

/// Whether something this dialog installs is missing. Homebrew installs are offered on macOS only.
pub fn is_needed(app: &AppShared) -> bool {
    cfg!(target_os = "macos") && !missing_components(app).is_empty()
}

pub fn should_ask_at_startup(app: &AppShared) -> bool {
    is_needed(app) && app.settings().current().ask_to_install_at_startup
}

enum Msg {
    InstallerExited(i32),
    Line(String),
    BrewExited(Result<i32, String>),
    Located,
}

type FindBrew = Arc<dyn Fn() -> Option<String> + Send + Sync>;
type OpenInTerminal = Arc<dyn Fn(&std::path::Path) -> std::io::Result<()> + Send + Sync>;

pub struct SetupDialog {
    app: Arc<AppShared>,
    pub components: Vec<SetupComponent>,
    pub step: SetupStep,
    pub status: Option<String>,
    pub status_is_error: bool,
    pub log: Option<VecDeque<String>>,
    pub ask_at_startup: bool,
    brew: Option<String>,
    work: PathBuf,
    cancel: Arc<AtomicBool>,
    rx: Receiver<Msg>,
    tx: std::sync::mpsc::Sender<Msg>,
    waker: crate::waker::Waker,
    confirm_stop: bool,
    chosen: Vec<SetupComponent>,
    /// Debug automation replaces finding brew and running scripts.
    pub find_brew: FindBrew,
    pub open_in_terminal: OpenInTerminal,
}

impl SetupDialog {
    pub fn new(app: &Arc<AppShared>, waker: crate::waker::Waker) -> SetupDialog {
        let (tx, rx) = channel();
        let find_brew: FindBrew = Arc::new(homebrew::find);
        let brew = find_brew();
        let components = missing_components(app);
        app_log::write(&format!(
            "Setup: offering {}; brew: {}",
            components.iter().map(|c| c.name).collect::<Vec<_>>().join(", "),
            brew.as_deref().unwrap_or("not found")
        ));
        SetupDialog {
            app: app.clone(),
            step: if brew.is_none() { SetupStep::NeedsHomebrew } else { SetupStep::Ready },
            components,
            status: None,
            status_is_error: false,
            log: None,
            ask_at_startup: app.settings().current().ask_to_install_at_startup,
            brew,
            work: app_paths::temp_directory().join("homebrew"),
            cancel: Arc::new(AtomicBool::new(false)),
            rx,
            tx,
            waker,
            confirm_stop: false,
            chosen: vec![],
            find_brew,
            open_in_terminal: Arc::new(homebrew::open_in_terminal),
        }
    }

    fn selected(&self) -> Vec<SetupComponent> {
        self.components.iter().filter(|c| c.selected).cloned().collect()
    }

    pub fn install_command(&self) -> String {
        format!("brew install {}", self.selected().iter().map(|c| c.formula).collect::<Vec<_>>().join(" "))
    }

    pub fn primary_text(&self) -> &'static str {
        match self.step {
            SetupStep::NeedsHomebrew => "Install Homebrew…",
            SetupStep::WaitingForHomebrew => "Waiting for Homebrew…",
            SetupStep::Installing => "Installing…",
            SetupStep::Installed => "Done",
            SetupStep::Failed => "Try Again",
            SetupStep::Ready => "Install",
        }
    }

    pub fn secondary_text(&self) -> &'static str {
        match self.step {
            SetupStep::Installing => "Stop",
            SetupStep::Failed => "Close",
            _ => "Not Now",
        }
    }

    fn can_continue(&self) -> bool {
        match self.step {
            SetupStep::WaitingForHomebrew | SetupStep::Installing => false,
            SetupStep::Installed => true,
            _ => !self.selected().is_empty(),
        }
    }

    fn show_status(&mut self, text: Option<String>, error: bool) {
        self.status = text;
        self.status_is_error = error;
    }

    /// Returns true when the dialog should close.
    pub fn continue_(&mut self) -> bool {
        match self.step {
            SetupStep::NeedsHomebrew => self.install_homebrew(),
            SetupStep::Installed => return true,
            _ => self.install_components(),
        }
        false
    }

    fn wake(&self) -> impl Fn() + Send + Clone + 'static {
        let w = self.waker.clone();
        move || w.wake()
    }

    fn install_homebrew(&mut self) {
        self.show_status(None, false);
        if let Err(e) = homebrew::write_installer_script(&self.work, homebrew::INSTALLER_URL).and_then(|s| (self.open_in_terminal)(&s)) {
            return self.show_status(Some(format!("Couldn't open Terminal: {e}")), true);
        }
        app_log::write("Setup: Homebrew's installer opened in Terminal");
        self.step = SetupStep::WaitingForHomebrew;
        self.show_status(Some("Follow the steps in Terminal. This window continues once Homebrew is installed.".into()), false);
        self.cancel.store(false, Ordering::Release);
        let (tx, work, cancel, wake) = (self.tx.clone(), self.work.clone(), self.cancel.clone(), self.wake());
        std::thread::spawn(move || {
            loop {
                if cancel.load(Ordering::Acquire) {
                    return;
                }
                if let Some(code) = homebrew::read_installer_status(&work) {
                    let _ = tx.send(Msg::InstallerExited(code));
                    wake();
                    return;
                }
                std::thread::sleep(std::time::Duration::from_secs(1));
            }
        });
    }

    fn install_components(&mut self) {
        let chosen = self.selected();
        let names = chosen.iter().map(|c| c.name).collect::<Vec<_>>().join(" and ");
        self.step = SetupStep::Installing;
        self.log = Some(VecDeque::new());
        self.show_status(
            Some(format!(
                "Installing {names}. This usually takes a few minutes; on older macOS versions Homebrew builds from source, which can take an hour or more."
            )),
            false,
        );
        app_log::write(&format!("Setup: {}", self.install_command()));
        self.cancel.store(false, Ordering::Release);
        let formulae: Vec<String> = chosen.iter().map(|c| c.formula.to_string()).collect();
        self.chosen = chosen;
        let (tx, brew, cancel, wake) = (self.tx.clone(), self.brew.clone().unwrap_or_default(), self.cancel.clone(), self.wake());
        std::thread::spawn(move || {
            let line_tx = tx.clone();
            let w2 = wake.clone_box();
            let result = homebrew::install(
                &brew,
                &formulae,
                &mut |l| {
                    let _ = line_tx.send(Msg::Line(l));
                    w2();
                },
                cancel,
            );
            let _ = tx.send(Msg::BrewExited(result.map_err(|e| e.to_string())));
            wake();
        });
    }

    /// Applies what the background work reported.
    fn poll(&mut self) {
        while let Ok(m) = self.rx.try_recv() {
            match m {
                Msg::InstallerExited(code) => {
                    app_log::write(&format!("Setup: Homebrew's installer exited with {code}"));
                    self.brew = if code == 0 { (self.find_brew)() } else { None };
                    if self.brew.is_some() {
                        self.step = SetupStep::Ready;
                        self.show_status(Some("Homebrew is installed. Choose Install to continue.".into()), false);
                    } else {
                        self.step = SetupStep::NeedsHomebrew;
                        let text = match code {
                            0 => "Homebrew's installer finished, but brew wasn't found in /opt/homebrew or /usr/local.".to_string(),
                            129 | 130 | 143 => "Homebrew's installer was stopped before it finished.".to_string(),
                            c => format!("Homebrew's installer failed (exit code {c}). Terminal shows what went wrong."),
                        };
                        self.show_status(Some(text), true);
                    }
                }
                Msg::Line(l) => {
                    let log = self.log.get_or_insert_with(VecDeque::new);
                    log.push_back(l);
                    while log.len() > LOG_LINES {
                        log.pop_front();
                    }
                }
                Msg::BrewExited(result) => {
                    let names = self.chosen.iter().map(|c| c.name).collect::<Vec<_>>().join(" and ");
                    match result {
                        _ if self.cancel.load(Ordering::Acquire) => {
                            self.step = SetupStep::Failed;
                            self.show_status(Some("Installation stopped.".into()), false);
                        }
                        Err(e) => {
                            self.step = SetupStep::Failed;
                            self.show_status(Some(format!("Couldn't run Homebrew: {e}")), true);
                        }
                        Ok(code) if code != 0 => {
                            app_log::write(&format!("Setup: brew exited with {code}"));
                            self.step = SetupStep::Failed;
                            self.show_status(
                                Some(format!("Homebrew couldn't install {names} (exit code {code}). Its output above shows why.")),
                                true,
                            );
                        }
                        Ok(_) => {
                            // libmpv first: windows start their previews when the tools are located again.
                            self.app.load_mpv();
                            let (tx, wake) = (self.tx.clone(), self.wake());
                            self.app.locate_tools(Some(Box::new(move || {
                                let _ = tx.send(Msg::Located);
                                wake();
                            })));
                        }
                    }
                }
                Msg::Located => {
                    let missing: Vec<&str> = missing_components(&self.app).iter().map(|c| c.formula).collect();
                    let unusable: Vec<SetupComponent> = self.chosen.iter().filter(|c| missing.contains(&c.formula)).cloned().collect();
                    if unusable.is_empty() {
                        self.step = SetupStep::Installed;
                        let version = self.app.tools().and_then(|t| t.version);
                        let ready: Vec<String> = self
                            .chosen
                            .iter()
                            .map(|c| match (c.formula, version) {
                                ("ffmpeg", Some(v)) => format!("ffmpeg {v}"),
                                _ => c.name.to_string(),
                            })
                            .collect();
                        let verb = if self.chosen.len() == 1 { "is" } else { "are" };
                        self.show_status(Some(format!("✓ {} {verb} ready.", ready.join(" and "))), false);
                    } else {
                        self.step = SetupStep::Failed;
                        let mpv = if unusable.iter().any(|c| c.formula == "mpv") {
                            format!(" {}", library::load_error().unwrap_or_default())
                        } else {
                            String::new()
                        };
                        self.show_status(
                            Some(format!(
                                "Homebrew installed {}, but the app still can't use it. Check the paths in Settings.{mpv}",
                                unusable.iter().map(|c| c.name).collect::<Vec<_>>().join(" and ")
                            )),
                            true,
                        );
                    }
                }
            }
        }
    }

    /// "Not Now" and "Close" close the dialog; "Stop" stops brew. Returns true when it should close.
    fn dismiss(&mut self) -> bool {
        if self.step == SetupStep::Installing {
            self.cancel.store(true, Ordering::Release);
            return false;
        }
        true
    }
}

trait CloneBox {
    fn clone_box(&self) -> Box<dyn Fn() + Send + 'static>;
}

impl<F: Fn() + Send + Clone + 'static> CloneBox for F {
    fn clone_box(&self) -> Box<dyn Fn() + Send + 'static> {
        Box::new(self.clone())
    }
}

impl Drop for SetupDialog {
    /// Stops waiting for Homebrew's installer, or stops brew.
    fn drop(&mut self) {
        self.cancel.store(true, Ordering::Release);
    }
}

pub fn show(w: &mut crate::project::ProjectWindow, ctx: &egui::Context) {
    let id = egui::ViewportId::from_hash_of(("setup", w.id));
    let mut close = false;
    let d = w.open.setup.as_mut().unwrap();
    d.poll();
    let mut shot = w.dialog_shot.take();
    ctx.show_viewport_immediate(id, super::builder("Install Missing Components", Vec2::new(560.0, 560.0), false), |ui, _| {
        let ctx = ui.ctx().clone();
        super::take_shot(&mut shot, "setup", &ctx);
        theme::apply(&ctx, d.app.settings().current().theme);
        egui::CentralPanel::default().frame(egui::Frame::new().fill(theme::palette(&ctx).window).inner_margin(20)).show(ui, |ui| {
            egui::ScrollArea::vertical().show(ui, |ui| close |= body(d, ui));
        });
        if ctx.input(|i| i.viewport().close_requested()) || super::escape_pressed(&ctx) {
            ctx.send_viewport_cmd(egui::ViewportCommand::CancelClose);
            if d.step == SetupStep::Installing {
                d.confirm_stop = true;
            } else {
                close = true;
            }
        }
        if d.confirm_stop {
            let mut choice = None;
            egui::Modal::new(egui::Id::unique("stop-install")).show(&ctx, |ui| {
                ui.set_width(380.0);
                ui.label(RichText::new("Stop installing?").font(theme::semibold(15.0)));
                ui.label("Homebrew is still installing. You can install again later from Settings.");
                ui.horizontal(|ui| {
                    if ui.add(theme::accent_button("Stop")).clicked() {
                        choice = Some(true);
                    }
                    if ui.button("Keep Installing").clicked() {
                        choice = Some(false);
                    }
                });
            });
            if let Some(c) = choice {
                d.confirm_stop = false;
                if c {
                    d.cancel.store(true, Ordering::Release);
                    close = true;
                }
            }
        }
    });
    w.dialog_shot = w.dialog_shot.take().or(shot);
    if close {
        w.open.setup = None;
    }
}

pub fn body(d: &mut SetupDialog, ui: &mut egui::Ui) -> bool {
    let mut close = false;
    ui.label(RichText::new("Install missing components").font(theme::semibold(16.0)));
    ui.add(
        egui::Label::new(
            RichText::new("Mane FFmpeg uses tools installed on your Mac, and these are missing. Homebrew, the usual package manager for macOS, can install them.")
                .color(theme::palette(ui.ctx()).text.gamma_multiply(0.85)),
        )
        .wrap(),
    );
    let editable = matches!(d.step, SetupStep::NeedsHomebrew | SetupStep::Ready | SetupStep::Failed);
    theme::panel_frame(ui.ctx()).inner_margin(egui::Margin::symmetric(12, 8)).show(ui, |ui| {
        ui.set_width(ui.available_width());
        ui.add_enabled_ui(editable, |ui| {
            for c in &mut d.components {
                ui.horizontal_top(|ui| {
                    ui.checkbox(&mut c.selected, "");
                    ui.vertical(|ui| {
                        ui.label(RichText::new(c.name).font(theme::semibold(13.0)));
                        theme::dim_wrapped(ui, c.purpose);
                    });
                });
            }
        });
    });
    if matches!(d.step, SetupStep::NeedsHomebrew | SetupStep::WaitingForHomebrew) {
        theme::panel_frame(ui.ctx()).inner_margin(egui::Margin::symmetric(12, 10)).show(ui, |ui| {
            ui.set_width(ui.available_width());
            theme::title(ui, "HOMEBREW ISN'T INSTALLED");
            ui.add(egui::Label::new(RichText::new("First, Homebrew's official installer opens in Terminal. It asks for your Mac password and may install Apple's Command Line Tools, which takes a few minutes. Come back here when it's done.").size(12.0)).wrap());
            ui.add(egui::Label::new(RichText::new(homebrew::installer_command()).monospace().size(11.0)).wrap().selectable(true));
        });
    }
    ui.add(egui::Label::new(RichText::new(d.install_command()).monospace().size(11.0)).selectable(true));
    if let Some(log) = &d.log {
        let mut text = log.iter().cloned().collect::<Vec<_>>().join("\n");
        egui::ScrollArea::vertical().id_salt("brew-log").max_height(180.0).stick_to_bottom(true).show(ui, |ui| {
            ui.add(egui::TextEdit::multiline(&mut text).font(theme::mono(11.0)).desired_width(f32::INFINITY).desired_rows(10));
        });
    }
    if let Some(s) = &d.status {
        let color = if d.status_is_error {
            theme::ERROR
        } else if d.step == SetupStep::Installed {
            theme::OK
        } else {
            theme::palette(ui.ctx()).text
        };
        ui.add(egui::Label::new(RichText::new(s).color(color)).wrap().selectable(true));
    }
    ui.add_space(4.0);
    ui.horizontal(|ui| {
        if ui.checkbox(&mut d.ask_at_startup, "Ask again at startup").changed() {
            let v = d.ask_at_startup;
            d.app.settings().update(|s| s.ask_to_install_at_startup = v);
        }
        ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
            if ui.add_enabled(d.can_continue(), theme::accent_button(d.primary_text()).min_size(Vec2::new(90.0, 28.0))).clicked() {
                close |= d.continue_();
            }
            if d.step != SetupStep::Installed && ui.add(egui::Button::new(d.secondary_text()).min_size(Vec2::new(90.0, 28.0))).clicked() {
                close |= d.dismiss();
            }
        });
    });
    close
}
