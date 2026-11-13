//! Makes this app the one that opens .mfvp projects on double-click. On macOS the app bundle declares the type
//! (scripts/package-macos.sh), and this sets the Launch Services default handler for it. On Linux it installs the
//! shared-mime-info type and the default application for the current user, like the packaged install.sh.

use crate::executables;
#[cfg(not(target_os = "macos"))]
use crate::persistence::atomic_file;
use crate::persistence::{app_log, app_paths, project_file};
#[cfg(not(target_os = "macos"))]
use std::path::PathBuf;
use std::process::{Command, Stdio};

const MIME_TYPE: &str = "application/x-mane-ffmpeg-project";
#[cfg(not(target_os = "macos"))]
const DESKTOP_ID: &str = "mane-ffmpeg.desktop";

/// Whether this copy can register itself: on macOS only the packaged app.
pub fn is_supported() -> bool {
    #[cfg(target_os = "macos")]
    {
        mac::bundle_id().is_some()
    }
    #[cfg(not(target_os = "macos"))]
    {
        linux_executable().is_some()
    }
}

pub fn is_registered() -> bool {
    if !is_supported() {
        return false;
    }
    #[cfg(target_os = "macos")]
    {
        mac::default_handler(mac::CONTENT_TYPE).is_some_and(|h| Some(h.to_lowercase()) == mac::bundle_id().map(|b| b.to_lowercase()))
    }
    #[cfg(not(target_os = "macos"))]
    {
        if !mime_package().is_file() || !desktop_file().is_file() {
            return false;
        }
        // Without xdg-mime the installed desktop file is the only handler, so it's the default.
        let Some(xdg_mime) = executables::find("xdg-mime", None) else { return true };
        Command::new(xdg_mime)
            .args(["query", "default", MIME_TYPE])
            .stderr(Stdio::null())
            .output()
            .map(|o| String::from_utf8_lossy(&o.stdout).trim() == DESKTOP_ID)
            .unwrap_or(false)
    }
}

pub fn register() -> Result<(), String> {
    if !is_supported() {
        return Err("Only the packaged app can be registered.".into());
    }
    app_log::write(&format!("Registering .{} files", project_file::EXTENSION));
    #[cfg(target_os = "macos")]
    {
        // Copied apps aren't known to Launch Services until Finder sees them, and their type declaration with them.
        mac::register_bundle(&mac::bundle_path().ok_or("Not in an app bundle.")?)?;
        mac::set_default_handler(mac::CONTENT_TYPE, &mac::bundle_id().unwrap())
    }
    #[cfg(not(target_os = "macos"))]
    {
        atomic_file::write_all_text(&mime_package(), MIME_XML).map_err(|e| e.to_string())?;
        // install.sh puts one in place for the installed copy; keep it.
        if !desktop_file().is_file() {
            atomic_file::write_all_text(&desktop_file(), &desktop_entry(&linux_executable().unwrap())).map_err(|e| e.to_string())?;
        }
        run("update-mime-database", &[&data_home().join("mime").to_string_lossy()]);
        run("update-desktop-database", &[&desktop_file().parent().unwrap().to_string_lossy()]);
        run("xdg-mime", &["default", DESKTOP_ID, MIME_TYPE]);
        Ok(())
    }
}

/// Hands .mfvp files back to whatever opens JSON.
pub fn unregister() -> Result<(), String> {
    if !is_supported() {
        return Err("Only the packaged app can be registered.".into());
    }
    app_log::write(&format!("Unregistering .{} files", project_file::EXTENSION));
    #[cfg(target_os = "macos")]
    {
        // The bundle's claim stays in its Info.plist, so an explicit default is the only way to override it.
        let json = mac::default_handler("public.json");
        let own = mac::bundle_id().unwrap_or_default().to_lowercase();
        let handler = match json {
            Some(j) if j.to_lowercase() != own => j,
            _ => mac::FALLBACK_HANDLER.to_string(),
        };
        mac::set_default_handler(mac::CONTENT_TYPE, &handler)
    }
    #[cfg(not(target_os = "macos"))]
    {
        // Without the type, .mfvp files are plain JSON again; the desktop file stays as the menu entry.
        let _ = std::fs::remove_file(mime_package());
        run("update-mime-database", &[&data_home().join("mime").to_string_lossy()]);
        let list = mime_apps_list();
        if let Ok(text) = std::fs::read_to_string(&list) {
            atomic_file::write_all_text(&list, &without_associations(&text, MIME_TYPE)).map_err(|e| e.to_string())?;
        }
        Ok(())
    }
}

// Linux

#[cfg(not(target_os = "macos"))]
fn data_home() -> PathBuf {
    app_paths::xdg("XDG_DATA_HOME", ".local/share")
}

#[cfg(not(target_os = "macos"))]
fn mime_package() -> PathBuf {
    data_home().join("mime").join("packages").join("mane-ffmpeg-mime.xml")
}

#[cfg(not(target_os = "macos"))]
fn desktop_file() -> PathBuf {
    data_home().join("applications").join(DESKTOP_ID)
}

#[cfg(not(target_os = "macos"))]
fn mime_apps_list() -> PathBuf {
    app_paths::xdg("XDG_CONFIG_HOME", ".config").join("mimeapps.list")
}

#[cfg_attr(target_os = "macos", allow(dead_code))]
fn linux_executable() -> Option<String> {
    std::env::current_exe().ok().map(|p| p.to_string_lossy().into_owned())
}

// Same as the one scripts/package-linux.sh installs.
#[cfg_attr(target_os = "macos", allow(dead_code))]
const MIME_XML: &str = r#"<?xml version="1.0" encoding="UTF-8"?>
<mime-info xmlns="http://www.freedesktop.org/standards/shared-mime-info">
  <mime-type type="application/x-mane-ffmpeg-project">
    <comment>Mane FFmpeg project</comment>
    <sub-class-of type="application/json"/>
    <glob pattern="*.mfvp"/>
  </mime-type>
</mime-info>
"#;

pub fn desktop_entry(executable: &str) -> String {
    format!(
        "[Desktop Entry]\nType=Application\nName={}\nComment=Cut, reorder and export videos with your system ffmpeg\nExec={} %F\nIcon=mane-ffmpeg\nTerminal=false\nCategories=AudioVideo;Video;AudioVideoEditing;\nMimeType={MIME_TYPE};\n",
        app_paths::APP_NAME,
        exec_argument(executable)
    )
}

/// Quotes a path for a desktop entry's Exec key: " ` $ \ are escaped inside the quotes, then backslashes once more
/// for the string value, and % is doubled.
pub fn exec_argument(path: &str) -> String {
    let mut quoted = String::from("\"");
    for c in path.chars() {
        if matches!(c, '"' | '`' | '$' | '\\') {
            quoted.push('\\');
        }
        quoted.push(c);
    }
    quoted.push('"');
    quoted.replace('\\', "\\\\").replace('%', "%%")
}

/// A mimeapps.list without the lines that set handlers for `mime_type`.
pub fn without_associations(mime_apps: &str, mime_type: &str) -> String {
    let prefix = format!("{mime_type}=");
    mime_apps.split('\n').filter(|l| !l.trim_start().starts_with(&prefix)).collect::<Vec<_>>().join("\n")
}

#[cfg_attr(target_os = "macos", allow(dead_code))]
fn run(tool: &str, args: &[&str]) {
    let Some(path) = executables::find(tool, None) else {
        app_log::write(&format!("{tool} not found; skipped"));
        return;
    };
    match Command::new(path).args(args).stdin(Stdio::null()).output() {
        Ok(o) if !o.status.success() => {
            app_log::write(&format!("{tool} exited with {}: {}", o.status.code().unwrap_or(-1), String::from_utf8_lossy(&o.stderr).trim()))
        }
        Err(e) => app_log::write(&format!("{tool} failed: {e}")),
        _ => {}
    }
}

#[cfg(target_os = "macos")]
mod mac {
    use std::ffi::{CStr, CString, c_char, c_void};
    use std::path::PathBuf;

    pub const CONTENT_TYPE: &str = "com.manefunction.mfvp";
    /// Opens .mfvp files after unregistering when nothing else opens JSON.
    pub const FALLBACK_HANDLER: &str = "com.apple.TextEdit";
    const UTF8: u32 = 0x0800_0100;
    const ROLES_ALL: u32 = 0xFFFF_FFFF;

    #[link(name = "CoreFoundation", kind = "framework")]
    unsafe extern "C" {
        fn CFStringCreateWithCString(alloc: *const c_void, text: *const c_char, encoding: u32) -> *const c_void;
        fn CFStringGetCString(text: *const c_void, buffer: *mut c_char, size: isize, encoding: u32) -> bool;
        fn CFURLCreateFromFileSystemRepresentation(alloc: *const c_void, path: *const u8, len: isize, dir: bool) -> *const c_void;
        fn CFBundleGetMainBundle() -> *const c_void;
        fn CFBundleGetIdentifier(bundle: *const c_void) -> *const c_void;
        fn CFRelease(value: *const c_void);
    }

    #[link(name = "CoreServices", kind = "framework")]
    unsafe extern "C" {
        // Deprecated since macOS 12 in favor of NSWorkspace's block-based API, but still working.
        fn LSCopyDefaultRoleHandlerForContentType(kind: *const c_void, roles: u32) -> *const c_void;
        fn LSSetDefaultRoleHandlerForContentType(kind: *const c_void, roles: u32, bundle: *const c_void) -> i32;
        fn LSRegisterURL(url: *const c_void, update: bool) -> i32;
    }

    fn cf(s: &str) -> *const c_void {
        let c = CString::new(s).unwrap();
        unsafe { CFStringCreateWithCString(std::ptr::null(), c.as_ptr(), UTF8) }
    }

    fn to_string(s: *const c_void) -> Option<String> {
        if s.is_null() {
            return None;
        }
        let mut buf = [0 as c_char; 1024];
        unsafe { CFStringGetCString(s, buf.as_mut_ptr(), buf.len() as isize, UTF8) }
            .then(|| unsafe { CStr::from_ptr(buf.as_ptr()) }.to_string_lossy().into_owned())
    }

    /// The app bundle around the executable: <bundle>.app/Contents/MacOS/ManeFfmpeg.
    pub fn bundle_path() -> Option<PathBuf> {
        let exe = std::env::current_exe().ok()?;
        let bundle = exe.parent()?.parent()?.parent()?.to_path_buf();
        (bundle.extension().is_some_and(|e| e.eq_ignore_ascii_case("app")) && bundle.join("Contents/Info.plist").is_file())
            .then_some(bundle)
    }

    pub fn bundle_id() -> Option<String> {
        bundle_path()?;
        to_string(unsafe { CFBundleGetIdentifier(CFBundleGetMainBundle()) })
    }

    pub fn default_handler(content_type: &str) -> Option<String> {
        let t = cf(content_type);
        let h = unsafe { LSCopyDefaultRoleHandlerForContentType(t, ROLES_ALL) };
        let result = to_string(h);
        unsafe {
            if !h.is_null() {
                CFRelease(h);
            }
            CFRelease(t);
        }
        result
    }

    pub fn set_default_handler(content_type: &str, bundle_id: &str) -> Result<(), String> {
        let (t, h) = (cf(content_type), cf(bundle_id));
        let status = unsafe { LSSetDefaultRoleHandlerForContentType(t, ROLES_ALL, h) };
        unsafe {
            CFRelease(h);
            CFRelease(t);
        }
        if status != 0 {
            return Err(format!("Launch Services refused to set {bundle_id} as the default (error {status})."));
        }
        Ok(())
    }

    pub fn register_bundle(path: &std::path::Path) -> Result<(), String> {
        let bytes = path.to_string_lossy().into_owned().into_bytes();
        let url = unsafe { CFURLCreateFromFileSystemRepresentation(std::ptr::null(), bytes.as_ptr(), bytes.len() as isize, true) };
        let status = unsafe { LSRegisterURL(url, true) };
        unsafe { CFRelease(url) };
        if status != 0 {
            return Err(format!("Launch Services refused to register {} (error {status}).", path.display()));
        }
        Ok(())
    }
}
