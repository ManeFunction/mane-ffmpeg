//! App-wide state shared by every window: settings, presets, located tools and media caches, plus the queue of
//! requests windows make of the app (open a window, open files, check for updates…).

use mane_core::ffmpeg::FfmpegTools;
use mane_core::media::analyzers::Analyzers;
use mane_core::mpv::library;
use mane_core::persistence::app_log;
use mane_core::persistence::presets::PresetStore;
use mane_core::persistence::settings::SettingsStore;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, MutexGuard, OnceLock};

/// What a window asks the app (the root) to do.
#[derive(Debug, Clone)]
pub enum AppCommand {
    /// A new, empty window, cascaded from `from` (a window id).
    NewWindow {
        from: Option<u64>,
    },
    /// Opens projects and adds media, as dropped or passed files; `requester` is the window they came from.
    OpenFiles {
        paths: Vec<String>,
        requester: Option<u64>,
    },
    /// Opens one project file (reusing `requester` when it is empty).
    OpenProject {
        path: String,
        requester: Option<u64>,
    },
    CheckForUpdates {
        manual: bool,
    },
    /// The window agreed to close (it asked about unsaved edits already).
    WindowClosed {
        id: u64,
    },
    /// Settings were applied: windows restyle, and the tools are located again.
    SettingsApplied,
    /// The update window closed; its outcome waits in the updater.
    UpdateClosed,
    ShowAbout,
    Quit,
    /// Quits without asking (scripts).
    ForceQuit,
}

pub struct AppShared {
    pub settings: Mutex<SettingsStore>,
    pub presets: Mutex<PresetStore>,
    pub analyzers: Arc<Analyzers>,
    /// ffmpeg has been looked for at least once.
    pub tools_located: AtomicBool,
    /// Bumped whenever the tools are (re)located; windows reanalyze when it changes.
    pub tools_generation: AtomicU64,
    commands: Mutex<Vec<AppCommand>>,
    root: OnceLock<egui::Context>,
    /// Viewports to wake when something app-wide changes.
    viewports: Mutex<Vec<egui::ViewportId>>,
    /// The GPU device, for freeing video textures.
    pub render_state: OnceLock<eframe::egui_wgpu::RenderState>,
    /// A check for updates or the update window is under way (the menu item greys out).
    pub update_busy: AtomicBool,
}

impl AppShared {
    pub fn new() -> Arc<AppShared> {
        Arc::new(AppShared {
            settings: Mutex::new(SettingsStore::new(None)),
            presets: Mutex::new(PresetStore::new(None)),
            analyzers: Arc::new(Analyzers::default()),
            tools_located: AtomicBool::new(false),
            tools_generation: AtomicU64::new(0),
            commands: Mutex::new(Vec::new()),
            root: OnceLock::new(),
            viewports: Mutex::new(vec![egui::ViewportId::ROOT]),
            render_state: OnceLock::new(),
            update_busy: AtomicBool::new(false),
        })
    }

    pub fn settings(&self) -> MutexGuard<'_, SettingsStore> {
        self.settings.lock().unwrap()
    }

    pub fn presets(&self) -> MutexGuard<'_, PresetStore> {
        self.presets.lock().unwrap()
    }

    pub fn tools(&self) -> Option<FfmpegTools> {
        self.analyzers.tools()
    }

    pub fn tools_error(&self) -> Option<String> {
        self.analyzers.tools_error()
    }

    pub fn set_root(&self, ctx: egui::Context) {
        let _ = self.root.set(ctx);
    }

    pub fn send(&self, command: AppCommand) {
        self.commands.lock().unwrap().push(command);
        if let Some(ctx) = self.root.get() {
            ctx.request_repaint_of(egui::ViewportId::ROOT);
        }
    }

    pub fn take_commands(&self) -> Vec<AppCommand> {
        std::mem::take(&mut *self.commands.lock().unwrap())
    }

    pub fn set_viewports(&self, ids: Vec<egui::ViewportId>) {
        *self.viewports.lock().unwrap() = ids;
    }

    /// Wakes every window (tools changed, presets changed…).
    pub fn repaint_all(&self) {
        if let Some(ctx) = self.root.get() {
            for id in self.viewports.lock().unwrap().iter() {
                ctx.request_repaint_of(*id);
            }
        }
    }

    pub fn load_mpv(&self) {
        let path = self.settings().current().lib_mpv_path.clone();
        library::try_load(path.as_deref());
    }

    /// Looks for ffmpeg in the background; windows pick the result up through `tools_generation`.
    pub fn locate_tools(self: &Arc<Self>, done: Option<Box<dyn FnOnce() + Send>>) {
        let me = self.clone();
        std::thread::spawn(move || {
            let (ffmpeg, ffprobe) = {
                let s = me.settings();
                (s.current().ffmpeg_path.clone(), s.current().ffprobe_path.clone())
            };
            let tools = FfmpegTools::locate(ffmpeg.as_deref(), ffprobe.as_deref());
            me.analyzers.set_tools(tools);
            app_log::write(&match me.tools() {
                Some(t) => format!("ffmpeg: {} ({})", t.version_text, t.ffmpeg),
                None => format!("ffmpeg: {}", me.tools_error().unwrap_or_default()),
            });
            me.tools_located.store(true, Ordering::Release);
            me.tools_generation.fetch_add(1, Ordering::AcqRel);
            if let Some(done) = done {
                done();
            }
            me.repaint_all();
        });
    }

    pub fn is_tools_located(&self) -> bool {
        self.tools_located.load(Ordering::Acquire)
    }
}
