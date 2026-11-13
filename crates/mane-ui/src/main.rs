//! Mane FFmpeg: a small macOS/Linux video editor on top of the system ffmpeg, with a libmpv preview.

mod app;
mod controls;
mod debug_automation;
mod debug_automation_impl;
mod dialogs;
mod logic;
#[cfg(target_os = "macos")]
mod platform_mac;
mod project;
mod shared;
mod theme;
mod views;
mod waker;

use mane_core::mpv::library;
use mane_core::persistence::app_log;
use mane_core::persistence::settings::SettingsStore;

/// A libmpv bundled inside another app (IINA) finds its dependencies only when DYLD_FALLBACK_LIBRARY_PATH is set at
/// process start, so restart once with it and wait.
fn relaunch_for_bundled_mpv(args: &[String]) -> Option<i32> {
    if !cfg!(target_os = "macos") || std::env::var(app::RELAUNCHED_VARIABLE).as_deref() == Ok("1") {
        return None;
    }
    let settings = SettingsStore::new(None);
    let explicit = settings.current().lib_mpv_path.clone();
    let fallback = library::relaunch_environment(explicit.as_deref())?;
    let exe = std::env::current_exe().ok()?;
    app_log::write(&format!("Relaunching with DYLD_FALLBACK_LIBRARY_PATH for {:?}", library::find(explicit.as_deref())));
    let status = std::process::Command::new(exe)
        .args(args)
        .env("DYLD_FALLBACK_LIBRARY_PATH", fallback)
        .env(app::RELAUNCHED_VARIABLE, "1")
        .status()
        .ok()?;
    Some(status.code().unwrap_or(0))
}

fn main() -> eframe::Result {
    let args: Vec<String> = std::env::args().skip(1).collect();
    std::panic::set_hook(Box::new(|info| {
        let backtrace = std::backtrace::Backtrace::capture();
        app_log::write(&format!("Panic: {info}\n{backtrace}"));
        eprintln!("{info}");
    }));
    if let Some(code) = relaunch_for_bundled_mpv(&args) {
        std::process::exit(code);
    }
    app_log::write(&format!("Start {} args=[{}]", env!("CARGO_PKG_VERSION"), args.join(", ")));
    let shared = shared::AppShared::new();
    shared.load_mpv();
    app_log::write(&match library::loaded_path() {
        Some(p) => format!("libmpv {} from {p}", library::api_version().unwrap_or_default()),
        None => format!("libmpv not loaded: {}", library::load_error().unwrap_or_default()),
    });
    let options =
        eframe::NativeOptions { viewport: app::window_builder("Mane FFmpeg"), renderer: eframe::Renderer::Wgpu, ..Default::default() };
    eframe::run_native(
        "Mane FFmpeg",
        options,
        Box::new(move |cc| {
            #[cfg(target_os = "macos")]
            platform_mac::install_open_handler();
            Ok(Box::new(app::ManeApp::new(cc, shared, args)))
        }),
    )
}
