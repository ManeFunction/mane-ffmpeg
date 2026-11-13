//! The app: its windows, the requests they send, the startup steps and the updater. The first window is drawn in
//! eframe's root viewport, the others in viewports of their own; when the first one closes, the next takes over the
//! root window, so the app runs while any window is open.

use crate::dialogs::{setup, update};
use crate::project::{ProjectWindow, is_project};
use crate::shared::{AppCommand, AppShared};
use crate::theme;
use mane_core::persistence::{app_log, project_file};
use mane_core::platform::file_association;
use mane_core::updates::checker::{self, UpdateOffer};
use mane_core::updates::installer::{self, PreparedUpdate};
use mane_core::updates::parse_version;
use mane_core::version::Version;
use std::path::{Path, PathBuf};
use std::sync::atomic::Ordering;
use std::sync::mpsc::{Receiver, channel};
use std::sync::{Arc, Mutex};

pub const RELAUNCHED_VARIABLE: &str = "MANE_FFMPEG_RELAUNCHED";
/// Debug builds look for updates only with this set, pretending to be the given version (e.g. 0.8.0).
const PRETEND_VERSION_VARIABLE: &str = "MANE_FFMPEG_PRETEND_VERSION";

pub fn icon() -> egui::IconData {
    static ICON: std::sync::OnceLock<egui::IconData> = std::sync::OnceLock::new();
    ICON.get_or_init(|| eframe::icon_data::from_png_bytes(include_bytes!("../../../assets/app.png")).unwrap_or_default()).clone()
}

pub fn window_builder(title: &str) -> egui::ViewportBuilder {
    egui::ViewportBuilder::default()
        .with_title(title)
        .with_inner_size([1400.0, 900.0])
        .with_min_inner_size([960.0, 640.0])
        .with_icon(icon())
        .with_drag_and_drop(true)
        .with_app_id("mane-ffmpeg")
}

struct WindowEntry {
    id: u64,
    viewport: egui::ViewportId,
    window: Arc<Mutex<ProjectWindow>>,
    /// Where a new window opens (cascaded from the one it came from).
    position: Option<egui::Pos2>,
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
enum Startup {
    LocatingTools,
    Setup,
    Association,
    Done,
}

/// Closing every window for an update or Quit: each is asked in turn, nothing closes until all agree.
struct ClosingAll {
    remaining: Vec<u64>,
    asked: Option<u64>,
    for_update: bool,
}

pub struct ManeApp {
    app: Arc<AppShared>,
    ctx: egui::Context,
    windows: Vec<WindowEntry>,
    next_id: u64,
    // On macOS file arguments arrive as "open document" events instead.
    #[cfg_attr(target_os = "macos", allow(dead_code))]
    args: Vec<String>,
    startup: Startup,
    // Updater
    current_version: Version,
    installed: Option<PathBuf>,
    update_rx: Option<Receiver<Option<UpdateOffer>>>,
    offer: Option<UpdateOffer>,
    outcome: Arc<Mutex<Option<update::UpdateOutcome>>>,
    pending: Option<PreparedUpdate>,
    applied: bool,
    closing_all: Option<ClosingAll>,
    quitting: bool,
    association_answer: Arc<Mutex<Option<(i32, bool)>>>,
}

impl ManeApp {
    pub fn new(cc: &eframe::CreationContext, app: Arc<AppShared>, args: Vec<String>) -> ManeApp {
        let ctx = cc.egui_ctx.clone();
        app.set_root(ctx.clone());
        if let Some(rs) = cc.wgpu_render_state.clone() {
            let _ = app.render_state.set(rs);
        }
        theme::install_fonts(&ctx);
        theme::configure(&ctx);
        theme::apply(&ctx, app.settings().current().theme);
        let current = if cfg!(debug_assertions) {
            std::env::var(PRETEND_VERSION_VARIABLE).ok().and_then(|v| parse_version(&v)).unwrap_or_else(Version::current)
        } else {
            Version::current()
        };
        let mut me = ManeApp {
            app: app.clone(),
            ctx: ctx.clone(),
            windows: Vec::new(),
            next_id: 1,
            args,
            startup: Startup::LocatingTools,
            current_version: current,
            installed: installer::installed_path(None),
            update_rx: None,
            offer: None,
            outcome: Arc::new(Mutex::new(None)),
            pending: None,
            applied: false,
            closing_all: None,
            quitting: false,
            association_answer: Arc::new(Mutex::new(None)),
        };
        me.open_window(None, egui::ViewportId::ROOT);
        app.locate_tools(None);
        // Looked up while the startup dialogs are open; offered after them.
        me.check_for_updates(false);
        me
    }

    fn open_window(&mut self, from: Option<u64>, viewport: egui::ViewportId) -> u64 {
        let id = self.next_id;
        self.next_id += 1;
        let viewport = if viewport == egui::ViewportId::ROOT && self.windows.is_empty() {
            viewport
        } else {
            egui::ViewportId::from_hash_of(("window", id))
        };
        let position = from.and_then(|f| self.find(f)).and_then(|w| w.lock().unwrap().outer_rect).map(|r| r.min + egui::vec2(32.0, 32.0));
        let window = ProjectWindow::new(id, self.app.clone(), self.ctx.clone(), viewport);
        self.windows.push(WindowEntry { id, viewport, window: Arc::new(Mutex::new(window)), position });
        self.app.set_viewports(self.windows.iter().map(|w| w.viewport).collect());
        self.ctx.request_repaint_of(viewport);
        id
    }

    fn find(&self, id: u64) -> Option<Arc<Mutex<ProjectWindow>>> {
        self.windows.iter().find(|w| w.id == id).map(|w| w.window.clone())
    }

    fn last(&self) -> Option<Arc<Mutex<ProjectWindow>>> {
        self.windows.last().map(|w| w.window.clone())
    }

    fn process_commands(&mut self) {
        for command in self.app.take_commands() {
            match command {
                AppCommand::NewWindow { from } => {
                    self.open_window(from, egui::ViewportId::ROOT);
                }
                AppCommand::OpenFiles { paths, requester } => self.open_files(paths, requester),
                AppCommand::OpenProject { path, requester } => self.open_project(&path, requester),
                AppCommand::CheckForUpdates { manual } => self.check_for_updates(manual),
                AppCommand::WindowClosed { id } => self.window_closed(id),
                AppCommand::SettingsApplied => {
                    self.app.locate_tools(None);
                    self.app.repaint_all();
                }
                AppCommand::UpdateClosed => self.update_closed(),
                AppCommand::ShowAbout => self.show_about(),
                AppCommand::Quit => self.begin_closing_all(false),
                AppCommand::ForceQuit => self.quit_now(),
            }
        }
    }

    /// Opens dropped or passed files: project files open as projects, media is added to the requester.
    fn open_files(&mut self, paths: Vec<String>, requester: Option<u64>) {
        for p in paths.iter().filter(|p| is_project(p)) {
            self.open_project(p, requester);
        }
        let media: Vec<String> = paths.into_iter().filter(|p| !is_project(p) && Path::new(p).is_file()).collect();
        if media.is_empty() {
            return;
        }
        let target = requester.and_then(|r| self.find(r)).or_else(|| self.last());
        let target = match target {
            Some(t) => t,
            None => {
                let id = self.open_window(None, egui::ViewportId::ROOT);
                self.find(id).unwrap()
            }
        };
        target.lock().unwrap().add_media(media);
    }

    /// Opens a project: focuses its window if it's already open, reuses the requester when it is still empty, and
    /// otherwise opens a new window.
    fn open_project(&mut self, path: &str, requester: Option<u64>) {
        let full = std::path::absolute(path).map(|p| p.to_string_lossy().into_owned()).unwrap_or_else(|_| path.to_string());
        if let Some(existing) = self.windows.iter().find(|w| w.window.lock().unwrap().file_path.as_deref() == Some(&full)) {
            self.ctx.send_viewport_cmd_to(existing.viewport, egui::ViewportCommand::Focus);
            return;
        }
        let reuse = requester.and_then(|r| self.find(r)).filter(|w| w.lock().unwrap().is_empty());
        let (target, is_new) = match reuse {
            Some(w) => (w, false),
            None => {
                let from = requester.or_else(|| self.windows.last().map(|w| w.id));
                let id = self.open_window(from, egui::ViewportId::ROOT);
                (self.find(id).unwrap(), true)
            }
        };
        let app = self.app.clone();
        let path = full.clone();
        target.lock().unwrap().load_project(&full, move |w, result| match result {
            Ok(()) => app.settings().add_recent_project(&path),
            Err(e) => {
                app_log::write(&format!("Open project {path} failed: {e}"));
                w.ask("Couldn't open the project", e, &["OK"], move |w, _| {
                    if is_new && w.is_empty() {
                        w.close_confirmed = true;
                        w.app.send(AppCommand::WindowClosed { id: w.id });
                    }
                });
            }
        });
    }

    fn window_closed(&mut self, id: u64) {
        let Some(index) = self.windows.iter().position(|w| w.id == id) else { return };
        {
            let mut w = self.windows[index].window.lock().unwrap();
            w.shut_down_preview();
            w.export.cancel();
        }
        if index == 0 {
            if self.windows.len() == 1 {
                self.quit_now();
                return;
            }
            // The next window takes over the root window, where it was.
            let next = &self.windows[1];
            let rect = next.window.lock().unwrap().outer_rect;
            next.window.lock().unwrap().waker.set_viewport(egui::ViewportId::ROOT);
            if let Some(r) = rect {
                self.ctx.send_viewport_cmd_to(egui::ViewportId::ROOT, egui::ViewportCommand::OuterPosition(r.min));
                self.ctx.send_viewport_cmd_to(egui::ViewportId::ROOT, egui::ViewportCommand::InnerSize(r.size()));
            }
            self.windows[1].viewport = egui::ViewportId::ROOT;
        }
        self.windows.remove(index);
        self.app.set_viewports(self.windows.iter().map(|w| w.viewport).collect());
        self.ctx.request_repaint();
    }

    /// Asks every window in turn, then quits (installing the update when asked).
    fn begin_closing_all(&mut self, for_update: bool) {
        if self.closing_all.is_some() {
            return;
        }
        self.closing_all = Some(ClosingAll { remaining: self.windows.iter().map(|w| w.id).collect(), asked: None, for_update });
    }

    fn step_closing_all(&mut self) {
        let Some(state) = &mut self.closing_all else { return };
        if let Some(asked) = state.asked {
            // Waiting for that window's answer.
            let Some(w) = self.windows.iter().find(|w| w.id == asked) else {
                state.asked = None;
                return;
            };
            let mut win = w.window.lock().unwrap();
            match win.close_answer.take() {
                None => return,
                Some(true) => {
                    drop(win);
                    state.asked = None;
                }
                Some(false) => {
                    let for_update = state.for_update;
                    self.closing_all = None;
                    if for_update {
                        app_log::write("Update: restart cancelled; installing on quit instead");
                        let v = self.pending.as_ref().map(|p| p.version.to_string()).unwrap_or_default();
                        win.info("Update postponed", format!("Mane FFmpeg {v} will be installed when you quit the app."));
                    }
                    return;
                }
            }
        }
        let state = self.closing_all.as_mut().unwrap();
        if let Some(next) = state.remaining.first().copied() {
            state.remaining.remove(0);
            state.asked = Some(next);
            if let Some(w) = self.windows.iter().find(|w| w.id == next) {
                self.ctx.send_viewport_cmd_to(w.viewport, egui::ViewportCommand::Focus);
                w.window.lock().unwrap().confirm_close(false, |w, ok| w.close_answer = Some(ok));
            }
            return;
        }
        // Every window agreed.
        let for_update = state.for_update;
        self.closing_all = None;
        if for_update {
            let reopen: Vec<String> = {
                let mut v: Vec<String> = self.windows.iter().filter_map(|w| w.window.lock().unwrap().file_path.clone()).collect();
                v.dedup();
                v
            };
            if !self.try_apply(true, reopen) {
                if let Some(w) = self.windows.first() {
                    w.window
                        .lock()
                        .unwrap()
                        .info("Couldn't install the update", format!("See {} for details.", installer::log_path().display()));
                }
                return;
            }
        }
        self.quit_now();
    }

    fn quit_now(&mut self) {
        self.quitting = true;
        for w in &self.windows {
            let mut w = w.window.lock().unwrap();
            w.export.cancel();
            w.close_confirmed = true;
            w.shut_down_preview();
        }
        self.ctx.send_viewport_cmd_to(egui::ViewportId::ROOT, egui::ViewportCommand::Close);
    }

    // ---- Startup --------------------------------------------------------------------------------------

    fn step_startup(&mut self) {
        match self.startup {
            Startup::LocatingTools => {
                if !self.app.is_tools_located() {
                    return;
                }
                let first = self.windows.first().map(|w| w.window.clone());
                // Before opening files, so they can be read with what gets installed.
                if setup::should_ask_at_startup(&self.app)
                    && let Some(w) = &first
                {
                    let mut w = w.lock().unwrap();
                    let waker = w.waker.clone();
                    w.open.setup = Some(setup::SetupDialog::new(&self.app, waker));
                }
                self.startup = Startup::Setup;
            }
            Startup::Setup => {
                if self.windows.first().is_some_and(|w| w.window.lock().unwrap().open.setup.is_some()) {
                    return;
                }
                self.offer_project_file_association();
                self.startup = Startup::Association;
            }
            Startup::Association => {
                let answer = self.association_answer.lock().unwrap().take();
                let asking = self.windows.first().is_some_and(|w| !w.window.lock().unwrap().dialogs.is_empty());
                if let Some((choice, do_not_show)) = answer {
                    if do_not_show {
                        self.app.settings().update(|s| s.ask_to_register_project_files = false);
                    }
                    if choice == 0
                        && let Err(e) = file_association::register()
                    {
                        app_log::write(&format!("Registering .{} files failed: {e}", project_file::EXTENSION));
                        if let Some(w) = self.windows.first() {
                            w.window.lock().unwrap().info("Couldn't register project files", e);
                        }
                    }
                } else if asking {
                    return;
                }
                // macOS turns file arguments into "open document" events as well; they're opened once, from there.
                #[cfg(target_os = "macos")]
                let files: Vec<String> = crate::platform_mac::take_opened_files();
                #[cfg(not(target_os = "macos"))]
                let files: Vec<String> = self.args.iter().filter(|a| Path::new(a).is_file()).cloned().collect();
                if !files.is_empty() {
                    let first = self.windows.first().map(|w| w.id);
                    self.open_files(files, first);
                }
                self.startup = Startup::Done;
                if cfg!(debug_assertions)
                    && let Some(w) = self.windows.first()
                {
                    crate::debug_automation::start_if_requested(&mut w.window.lock().unwrap());
                }
            }
            Startup::Done => {
                #[cfg(target_os = "macos")]
                {
                    let files = crate::platform_mac::take_opened_files();
                    if !files.is_empty() {
                        let last = self.windows.last().map(|w| w.id);
                        self.open_files(files, last);
                    }
                }
            }
        }
    }

    fn offer_project_file_association(&mut self) {
        // Scripted runs aren't interrupted by the offer.
        let scripted = cfg!(debug_assertions) && std::env::var("MANE_FFMPEG_SCRIPT").is_ok();
        if scripted
            || !self.app.settings().current().ask_to_register_project_files
            || !file_association::is_supported()
            || file_association::is_registered()
        {
            return;
        }
        let Some(first) = self.windows.first() else { return };
        app_log::write(&format!("Offering to open .{} files with this app", project_file::EXTENSION));
        let answer = self.association_answer.clone();
        let ext = project_file::EXTENSION;
        first.window.lock().unwrap().dialogs.push(crate::project::tasks::Ask {
            title: format!("Open .{ext} projects with Mane FFmpeg?"),
            message: "Projects don't open in Mane FFmpeg on double-click yet. You can change this later in Settings.".into(),
            buttons: vec!["Register".into(), "Not Now".into()],
            checkbox: Some(("Do not show anymore".into(), false)),
            link: None,
            then: Box::new(move |_, r| *answer.lock().unwrap() = Some(r)),
        });
    }

    fn show_about(&self) {
        let Some(w) = self.last() else { return };
        let tools = self.app.tools().map(|t| t.version_text).unwrap_or_else(|| "not found".into());
        let mpv = mane_core::mpv::library::loaded_path().unwrap_or_else(|| "not found".into());
        let mut w = w.lock().unwrap();
        w.dialogs.push(crate::project::tasks::Ask {
            title: "Mane FFmpeg".into(),
            message: format!(
                "A small video editor on top of your system ffmpeg.\n\nVersion {}\nffmpeg: {tools}\nlibmpv: {mpv}\n\nPowered by Ilia Petrov-Komotskii (Mane Function)",
                Version::current()
            ),
            buttons: vec!["OK".into()],
            checkbox: None,
            link: Some(("github.com/ManeFunction/mane-ffmpeg".into(), "https://github.com/ManeFunction/mane-ffmpeg".into())),
            then: Box::new(|_, _| {}),
        });
        w.wake();
    }

    // ---- Updates ----------------------------------------------------------------------------------------

    /// Looks for a newer release and offers it. Does nothing when a check is already running, or when automatic
    /// checks are off and the user didn't ask. `manual` also offers a release the user skipped.
    fn check_for_updates(&mut self, manual: bool) {
        if self.app.update_busy.load(Ordering::Acquire) {
            return;
        }
        if cfg!(debug_assertions) && std::env::var(PRETEND_VERSION_VARIABLE).is_err() {
            return;
        }
        let settings = self.app.settings().current().clone();
        if !manual && !settings.check_for_updates {
            return;
        }
        self.app.update_busy.store(true, Ordering::Release);
        self.app.repaint_all();
        let (tx, rx) = channel();
        self.update_rx = Some(rx);
        let (current, ctx) = (self.current_version, self.ctx.clone());
        std::thread::spawn(move || {
            installer::clean_up();
            let offer = match checker::check(&checker::http_agent(), current) {
                Ok(None) => {
                    app_log::write(&format!("Update: {current} is the latest release"));
                    None
                }
                Ok(Some(o)) if !manual && settings.skipped_update_version.as_deref() == Some(&o.version.to_string()) => {
                    app_log::write(&format!("Update: {} is available, but the user skipped it", o.version));
                    None
                }
                Ok(Some(o)) => {
                    app_log::write(&format!(
                        "Update: {} is available ({})",
                        o.version,
                        o.asset.as_ref().map(|a| a.name.as_str()).unwrap_or("no download for this system")
                    ));
                    Some(o)
                }
                Err(e) => {
                    app_log::write(&format!("Update: couldn't check for updates: {e}"));
                    None
                }
            };
            let _ = tx.send(offer);
            ctx.request_repaint_of(egui::ViewportId::ROOT);
        });
    }

    fn step_updates(&mut self) {
        if let Some(rx) = &self.update_rx
            && let Ok(offer) = rx.try_recv()
        {
            self.update_rx = None;
            match offer {
                Some(o) => self.offer = Some(o),
                None => {
                    self.app.update_busy.store(false, Ordering::Release);
                    self.app.repaint_all();
                }
            }
        }
        // The window waits for the startup dialogs.
        if self.startup != Startup::Done {
            return;
        }
        if let Some(offer) = self.offer.take() {
            match self.last() {
                Some(w) => {
                    let mut w = w.lock().unwrap();
                    w.open.update =
                        Some(update::UpdateDialog::new(offer, self.current_version, self.installed.clone(), self.outcome.clone()));
                    w.wake();
                }
                None => self.app.update_busy.store(false, Ordering::Release),
            }
        }
    }

    fn update_closed(&mut self) {
        self.app.update_busy.store(false, Ordering::Release);
        self.app.repaint_all();
        let Some(outcome) = self.outcome.lock().unwrap().take() else { return };
        if outcome.choice == update::UpdateChoice::None && outcome.do_not_remind {
            app_log::write(&format!("Update: not reminding about {} again", outcome.version));
            let v = outcome.version.to_string();
            self.app.settings().update(|s| s.skipped_update_version = Some(v));
        }
        let Some(prepared) = outcome.prepared.filter(|_| outcome.choice != update::UpdateChoice::None) else {
            installer::clean_up();
            return;
        };
        self.pending = Some(prepared);
        if outcome.choice == update::UpdateChoice::RestartNow {
            self.begin_closing_all(true);
        }
    }

    fn try_apply(&mut self, relaunch: bool, reopen: Vec<String>) -> bool {
        let (Some(pending), false, Some(installed)) = (&self.pending, self.applied, &self.installed) else { return false };
        // This process, and on macOS the one it was relaunched from: it waits for this one and exits too.
        let mut pids = vec![std::process::id()];
        if std::env::var(RELAUNCHED_VARIABLE).as_deref() == Ok("1") {
            pids.push(installer::parent_process_id());
        }
        match installer::apply(pending, installed, pids, relaunch, reopen) {
            Ok(()) => {
                self.applied = true;
                true
            }
            Err(e) => {
                app_log::write(&format!("Update: couldn't start installing: {e}"));
                false
            }
        }
    }
}

impl eframe::App for ManeApp {
    fn logic(&mut self, _ctx: &egui::Context, _frame: &mut eframe::Frame) {
        self.process_commands();
        self.step_startup();
        self.step_updates();
        self.step_closing_all();
    }

    fn ui(&mut self, ui: &mut egui::Ui, _frame: &mut eframe::Frame) {
        if self.windows.is_empty() {
            return;
        }
        // The other windows, each in a viewport of its own (shown every frame of the root, or they close).
        for entry in self.windows.iter().skip(1) {
            let window = entry.window.clone();
            let mut builder = window_builder("Mane FFmpeg");
            if let Some(p) = entry.position {
                builder = builder.with_position(p);
            }
            self.ctx.show_viewport_deferred(entry.viewport, builder, move |ui, _class| {
                let mut w = window.lock().unwrap();
                theme::apply(ui.ctx(), w.app.settings().current().theme);
                crate::views::window_ui(&mut w, ui);
            });
        }
        let first = self.windows[0].window.clone();
        let mut w = first.lock().unwrap();
        theme::apply(ui.ctx(), w.app.settings().current().theme);
        if self.quitting {
            return;
        }
        crate::views::window_ui(&mut w, ui);
    }

    fn on_exit(&mut self) {
        // "Install on Quit".
        self.try_apply(false, vec![]);
    }
}
