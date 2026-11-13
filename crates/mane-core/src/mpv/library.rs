//! Finds and loads libmpv. Order: explicit path, Homebrew, MacPorts, IINA's bundled copy (macOS), then the
//! system loader (Linux sonames). IINA's libmpv resolves its dependencies through @rpath, which only works
//! when DYLD_FALLBACK_LIBRARY_PATH points at IINA's Frameworks folder before the process starts, so the
//! app relaunches itself once with that variable set (see [`relaunch_environment`]).

use super::ffi::Api;
use std::path::{Path, PathBuf};
use std::sync::Mutex;

pub const IINA_FRAMEWORKS: &str = "/Applications/IINA.app/Contents/Frameworks";

/// Oldest client API with the software render API (mpv 0.33).
const MINIMUM_API: u32 = (1 << 16) | 107;

struct State {
    loaded_path: Option<String>,
    load_error: Option<String>,
}

static STATE: Mutex<State> = Mutex::new(State { loaded_path: None, load_error: None });

pub fn loaded_path() -> Option<String> {
    STATE.lock().unwrap().loaded_path.clone()
}

pub fn load_error() -> Option<String> {
    STATE.lock().unwrap().load_error.clone()
}

pub fn is_loaded() -> bool {
    Api::get().is_some()
}

pub fn api_version() -> Option<String> {
    Api::get().map(|api| {
        // SAFETY: plain getter.
        let v = unsafe { (api.client_api_version)() };
        format!("{}.{}", v >> 16, v & 0xffff)
    })
}

/// Candidate files, most preferred first.
pub fn candidates(explicit: Option<&str>) -> Vec<PathBuf> {
    let mut out = Vec::new();
    if let Some(p) = explicit.filter(|p| !p.trim().is_empty()) {
        out.push(PathBuf::from(p));
    }
    if cfg!(target_os = "macos") {
        for dir in ["/opt/homebrew/lib", "/usr/local/lib", "/opt/local/lib", IINA_FRAMEWORKS] {
            out.push(Path::new(dir).join("libmpv.2.dylib"));
            out.push(Path::new(dir).join("libmpv.dylib"));
        }
    } else {
        for dir in ["/usr/lib/x86_64-linux-gnu", "/usr/lib/aarch64-linux-gnu", "/usr/lib64", "/usr/lib", "/usr/local/lib"] {
            out.push(Path::new(dir).join("libmpv.so.2"));
            out.push(Path::new(dir).join("libmpv.so.1"));
            out.push(Path::new(dir).join("libmpv.so"));
        }
    }
    out
}

/// The file that [`try_load`] would pick, or None.
pub fn find(explicit: Option<&str>) -> Option<PathBuf> {
    candidates(explicit).into_iter().find(|p| p.is_file())
}

/// When the chosen libmpv is IINA's (or any copy bundled in an .app) and the variable isn't set yet, returns the
/// DYLD_FALLBACK_LIBRARY_PATH value to relaunch with; otherwise None.
pub fn relaunch_environment(explicit: Option<&str>) -> Option<String> {
    if !cfg!(target_os = "macos") {
        return None;
    }
    let path = find(explicit)?;
    // Only copies bundled inside an .app use @rpath; Homebrew/MacPorts dylibs have absolute install names.
    let dir = path.parent()?.to_string_lossy().into_owned();
    if !dir.ends_with("/Contents/Frameworks") {
        return None;
    }
    let current = std::env::var("DYLD_FALLBACK_LIBRARY_PATH").unwrap_or_default();
    if current.split(':').any(|d| d == dir) {
        return None;
    }
    let home = std::env::var("HOME").unwrap_or_default();
    let defaults = if current.is_empty() { format!("{home}/lib:/usr/local/lib:/usr/lib") } else { current };
    Some(format!("{dir}:{defaults}"))
}

fn open(path: &str) -> Result<Api, String> {
    // SAFETY: loading libmpv runs its initializers, which are well-behaved.
    let lib = unsafe { libloading::Library::new(path) }.map_err(|e| format!("{path}: could not be loaded ({e})"))?;
    // SAFETY: the file is a libmpv (it resolved mpv_* symbols or we fail).
    unsafe { Api::from_library(lib) }.map_err(|e| format!("{path}: {e}"))
}

pub fn try_load(explicit: Option<&str>) -> bool {
    let mut state = STATE.lock().unwrap();
    if Api::get().is_some() {
        return true;
    }
    let mut errors = Vec::new();
    let mut found: Option<(Api, String)> = None;
    for candidate in candidates(explicit).into_iter().filter(|p| p.is_file()) {
        let text = candidate.to_string_lossy().into_owned();
        match open(&text) {
            Ok(api) => {
                found = Some((api, text));
                break;
            }
            Err(e) => errors.push(e),
        }
    }
    if found.is_none() && cfg!(target_os = "linux") {
        for soname in ["libmpv.so.2", "libmpv.so.1"] {
            if let Ok(api) = open(soname) {
                found = Some((api, soname.to_string()));
                break;
            }
        }
    }
    let Some((api, path)) = found else {
        state.load_error = Some(if errors.is_empty() { "libmpv was not found.".into() } else { errors.join("; ") });
        return false;
    };
    // SAFETY: plain getter.
    let version = unsafe { (api.client_api_version)() };
    if version < MINIMUM_API {
        state.load_error =
            Some(format!("libmpv is too old (client API {}.{}); mpv 0.33 or newer is needed.", version >> 16, version & 0xffff));
        return false;
    }
    Api::install(api);
    state.loaded_path = Some(path);
    state.load_error = None;
    true
}

/// A hint for installing libmpv on this platform.
pub fn install_hint() -> &'static str {
    if cfg!(target_os = "macos") {
        "Install IINA (brew install --cask iina) or mpv (brew install mpv), or set the libmpv path in Settings."
    } else {
        "Install libmpv (Debian/Ubuntu: sudo apt install libmpv2, Fedora: sudo dnf install mpv-libs, Arch: sudo pacman -S mpv)."
    }
}
