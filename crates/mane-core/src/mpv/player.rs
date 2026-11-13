//! One libmpv instance with its own event thread. Events are sent from that thread; callers marshal
//! to the UI themselves. libmpv's client API is thread-safe, so commands may come from any thread.

use super::ffi::{self, Api};
use std::ffi::{CString, c_int, c_void};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicPtr, Ordering};
use std::thread::JoinHandle;

#[derive(Debug, thiserror::Error)]
#[error("{0}")]
pub struct MpvError(pub String);

/// An observed property's value.
#[derive(Debug, Clone, PartialEq)]
pub enum Value {
    Double(f64),
    Flag(bool),
    Str(String),
    None,
}

#[derive(Debug, Clone)]
pub enum MpvEvent {
    /// Observed properties: (name, value).
    Property(String, Value),
    /// A file finished loading: its playlist entry id.
    FileLoaded(i64),
    /// A file failed to load or stopped on an error: its playlist entry id and mpv's error.
    FileFailed(i64, String),
    Log(String, String),
    /// A seek (or a load) finished.
    PlaybackRestarted,
}

pub type EventSink = Box<dyn Fn(MpvEvent) + Send + Sync>;

struct Inner {
    api: &'static Api,
    handle: AtomicPtr<c_void>,
    stopping: AtomicBool,
}

// SAFETY: libmpv's client API is thread-safe.
unsafe impl Send for Inner {}
unsafe impl Sync for Inner {}

pub struct MpvPlayer {
    inner: Arc<Inner>,
    thread: Option<JoinHandle<()>>,
}

fn c(s: &str) -> CString {
    CString::new(s.replace('\0', "")).unwrap()
}

impl MpvPlayer {
    /// `headless`: no video or audio output, for tests; otherwise video goes to a render context.
    pub fn create(hardware_decoding: bool, headless: bool, sink: EventSink) -> Result<MpvPlayer, MpvError> {
        let api = Api::get().ok_or_else(|| MpvError("libmpv isn't loaded.".into()))?;
        // SAFETY: FFI calls on a fresh handle.
        let handle = unsafe { (api.create)() };
        if handle.is_null() {
            return Err(MpvError("mpv_create failed.".into()));
        }
        let options: &[(&str, &str)] = &[
            ("config", "no"),
            ("terminal", "no"),
            ("vo", if headless { "null" } else { "libmpv" }),
            ("idle", "yes"),
            ("keep-open", "always"),
            ("pause", "yes"),
            ("hr-seek", "yes"),
            ("hwdec", if hardware_decoding { "auto-copy" } else { "no" }),
            ("input-default-bindings", "no"),
            ("input-vo-keyboard", "no"),
            ("load-scripts", "no"),
            ("ytdl", "no"),
            ("osc", "no"),
            ("sub-auto", "no"),
            ("audio-file-auto", "no"),
            ("audio-client-name", "Mane FFmpeg"),
            ("load-unsafe-playlists", "yes"),
            ("audio-display", "no"),
            // No built-in Lua scripts (stats, console, menus…): the preview needs none of them.
            ("load-stats-overlay", "no"),
            ("load-console", "no"),
            ("load-auto-profiles", "no"),
            ("load-select", "no"),
            ("load-positioning", "no"),
            ("load-commands", "no"),
            ("load-context-menu", "no"),
        ];
        unsafe {
            for (name, value) in options {
                (api.set_option_string)(handle, c(name).as_ptr(), c(value).as_ptr());
            }
            if headless {
                (api.set_option_string)(handle, c("ao").as_ptr(), c("null").as_ptr());
            }
            let init = (api.initialize)(handle);
            if init < 0 {
                (api.terminate_destroy)(handle);
                return Err(MpvError(format!("mpv_initialize failed: {}", api.error(init))));
            }
            (api.request_log_messages)(handle, c("warn").as_ptr());
            for (id, (name, format)) in OBSERVED.iter().enumerate() {
                (api.observe_property)(handle, id as u64 + 1, c(name).as_ptr(), *format);
            }
        }
        let inner = Arc::new(Inner { api, handle: AtomicPtr::new(handle), stopping: AtomicBool::new(false) });
        let loop_inner = inner.clone();
        let thread = std::thread::Builder::new()
            .name("mpv events".into())
            .spawn(move || event_loop(loop_inner, sink))
            .map_err(|e| MpvError(e.to_string()))?;
        Ok(MpvPlayer { inner, thread: Some(thread) })
    }

    pub fn handle(&self) -> ffi::Handle {
        self.inner.handle.load(Ordering::Acquire)
    }

    pub(crate) fn api(&self) -> &'static Api {
        self.inner.api
    }

    pub fn command(&self, args: &[&str]) -> i32 {
        self.run(args, false)
    }

    pub fn command_async(&self, args: &[&str]) {
        self.run(args, true);
    }

    /// Replaces whatever is playing with `target`. Returns its playlist entry id, which tells
    /// its FileLoaded and FileFailed apart from those of the file it replaces.
    pub fn load_file(&self, target: &str) -> Option<i64> {
        // "replace" leaves the new file alone in the playlist.
        if self.run(&["loadfile", target, "replace"], false) >= 0 { self.get_int("playlist/0/id") } else { None }
    }

    fn run(&self, args: &[&str], is_async: bool) -> i32 {
        let handle = self.handle();
        if handle.is_null() {
            return -1;
        }
        let owned: Vec<CString> = args.iter().map(|a| c(a)).collect();
        let mut ptrs: Vec<*const std::ffi::c_char> = owned.iter().map(|s| s.as_ptr()).collect();
        ptrs.push(std::ptr::null());
        let api = self.inner.api;
        // SAFETY: NUL-terminated argv that outlives the call.
        let result =
            unsafe { if is_async { (api.command_async)(handle, 0, ptrs.as_mut_ptr()) } else { (api.command)(handle, ptrs.as_mut_ptr()) } };
        if result < 0 {
            crate::persistence::app_log::write(&format!("mpv error: {}: {}", args[0], api.error(result)));
        }
        result
    }

    pub fn set_string(&self, name: &str, value: &str) {
        let handle = self.handle();
        if !handle.is_null() {
            unsafe { (self.inner.api.set_property_string)(handle, c(name).as_ptr(), c(value).as_ptr()) };
        }
    }

    pub fn set_flag(&self, name: &str, value: bool) {
        let handle = self.handle();
        if !handle.is_null() {
            let mut v: c_int = value as c_int;
            unsafe { (self.inner.api.set_property)(handle, c(name).as_ptr(), ffi::FORMAT_FLAG, &mut v as *mut _ as *mut c_void) };
        }
    }

    pub fn set_double(&self, name: &str, value: f64) {
        let handle = self.handle();
        if !handle.is_null() {
            let mut v = value;
            unsafe { (self.inner.api.set_property)(handle, c(name).as_ptr(), ffi::FORMAT_DOUBLE, &mut v as *mut _ as *mut c_void) };
        }
    }

    pub fn get_double(&self, name: &str) -> Option<f64> {
        let handle = self.handle();
        if handle.is_null() {
            return None;
        }
        let mut v = 0f64;
        let r = unsafe { (self.inner.api.get_property)(handle, c(name).as_ptr(), ffi::FORMAT_DOUBLE, &mut v as *mut _ as *mut c_void) };
        (r >= 0).then_some(v)
    }

    pub fn get_string(&self, name: &str) -> Option<String> {
        let handle = self.handle();
        if handle.is_null() {
            return None;
        }
        let ptr = unsafe { (self.inner.api.get_property_string)(handle, c(name).as_ptr()) };
        if ptr.is_null() {
            return None;
        }
        // SAFETY: mpv returned a NUL-terminated string, freed below.
        let s = unsafe { ffi::string(ptr) };
        unsafe { (self.inner.api.free)(ptr as *mut c_void) };
        Some(s)
    }

    pub fn get_int(&self, name: &str) -> Option<i64> {
        self.get_string(name)?.trim().parse().ok()
    }
}

const OBSERVED: [(&str, c_int); 4] =
    [("time-pos", ffi::FORMAT_DOUBLE), ("duration", ffi::FORMAT_DOUBLE), ("pause", ffi::FORMAT_FLAG), ("eof-reached", ffi::FORMAT_FLAG)];

fn event_loop(inner: Arc<Inner>, sink: EventSink) {
    let api = inner.api;
    let mut started_entry: i64 = 0;
    loop {
        if inner.stopping.load(Ordering::Acquire) {
            break;
        }
        let handle = inner.handle.load(Ordering::Acquire);
        if handle.is_null() {
            break;
        }
        // SAFETY: the handle lives until Drop joins this thread.
        let ev = unsafe { &*(api.wait_event)(handle, -1.0) };
        if inner.stopping.load(Ordering::Acquire) || ev.event_id == ffi::EVENT_SHUTDOWN {
            break;
        }
        unsafe {
            match ev.event_id {
                ffi::EVENT_PROPERTY_CHANGE if !ev.data.is_null() => {
                    let prop = &*(ev.data as *const ffi::EventProperty);
                    let name = ffi::string(prop.name);
                    let value = match prop.format {
                        ffi::FORMAT_DOUBLE if !prop.data.is_null() => Value::Double(*(prop.data as *const f64)),
                        ffi::FORMAT_FLAG if !prop.data.is_null() => Value::Flag(*(prop.data as *const c_int) != 0),
                        ffi::FORMAT_STRING if !prop.data.is_null() => {
                            Value::Str(ffi::string(*(prop.data as *const *const std::ffi::c_char)))
                        }
                        _ => Value::None,
                    };
                    sink(MpvEvent::Property(name, value));
                }
                ffi::EVENT_START_FILE if !ev.data.is_null() => {
                    started_entry = (*(ev.data as *const ffi::EventStartFile)).playlist_entry_id;
                }
                // Always follows the start of the same file.
                ffi::EVENT_FILE_LOADED => sink(MpvEvent::FileLoaded(started_entry)),
                ffi::EVENT_PLAYBACK_RESTART => sink(MpvEvent::PlaybackRestarted),
                ffi::EVENT_END_FILE if !ev.data.is_null() => {
                    let end = &*(ev.data as *const ffi::EventEndFile);
                    if end.reason == ffi::END_FILE_REASON_ERROR {
                        sink(MpvEvent::FileFailed(end.playlist_entry_id, api.error(end.error)));
                    }
                }
                ffi::EVENT_LOG_MESSAGE if !ev.data.is_null() => {
                    let msg = &*(ev.data as *const ffi::EventLogMessage);
                    sink(MpvEvent::Log(ffi::string(msg.level), ffi::string(msg.text).trim_end().to_string()));
                }
                _ => {}
            }
        }
    }
}

impl Drop for MpvPlayer {
    /// Stops the event thread and destroys the instance. Free any render context first.
    fn drop(&mut self) {
        let handle = self.handle();
        if handle.is_null() {
            return;
        }
        self.inner.stopping.store(true, Ordering::Release);
        unsafe { (self.inner.api.wakeup)(handle) };
        if let Some(t) = self.thread.take()
            && t.thread().id() != std::thread::current().id()
        {
            let _ = t.join();
        }
        self.inner.handle.store(std::ptr::null_mut(), Ordering::Release);
        unsafe { (self.inner.api.terminate_destroy)(handle) };
    }
}
