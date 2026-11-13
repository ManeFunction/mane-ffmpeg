//! The slice of libmpv's client and render API this app uses. The library is located at runtime by
//! [`super::library`], which loads the file it found and fills in this function table.

#![allow(non_camel_case_types, dead_code)]

use std::ffi::{CStr, c_char, c_double, c_int, c_void};
use std::sync::OnceLock;

pub type Handle = *mut c_void;
pub type RenderContext = *mut c_void;

pub const FORMAT_NONE: c_int = 0;
pub const FORMAT_STRING: c_int = 1;
pub const FORMAT_FLAG: c_int = 3;
pub const FORMAT_INT64: c_int = 4;
pub const FORMAT_DOUBLE: c_int = 5;

pub const EVENT_NONE: c_int = 0;
pub const EVENT_SHUTDOWN: c_int = 1;
pub const EVENT_LOG_MESSAGE: c_int = 2;
pub const EVENT_START_FILE: c_int = 6;
pub const EVENT_END_FILE: c_int = 7;
pub const EVENT_FILE_LOADED: c_int = 8;
pub const EVENT_PLAYBACK_RESTART: c_int = 21;
pub const EVENT_PROPERTY_CHANGE: c_int = 22;

pub const END_FILE_REASON_ERROR: c_int = 4;

pub const RENDER_PARAM_INVALID: c_int = 0;
pub const RENDER_PARAM_API_TYPE: c_int = 1;
pub const RENDER_PARAM_SW_SIZE: c_int = 17;
pub const RENDER_PARAM_SW_FORMAT: c_int = 18;
pub const RENDER_PARAM_SW_STRIDE: c_int = 19;
pub const RENDER_PARAM_SW_POINTER: c_int = 20;
pub const RENDER_UPDATE_FRAME: u64 = 1;

#[repr(C)]
pub struct Event {
    pub event_id: c_int,
    pub error: c_int,
    pub reply_userdata: u64,
    pub data: *mut c_void,
}

#[repr(C)]
pub struct EventProperty {
    pub name: *const c_char,
    pub format: c_int,
    pub data: *mut c_void,
}

#[repr(C)]
pub struct EventStartFile {
    pub playlist_entry_id: i64,
}

#[repr(C)]
pub struct EventEndFile {
    pub reason: c_int,
    pub error: c_int,
    pub playlist_entry_id: i64,
}

#[repr(C)]
pub struct EventLogMessage {
    pub prefix: *const c_char,
    pub level: *const c_char,
    pub text: *const c_char,
    pub log_level: c_int,
}

#[repr(C)]
pub struct RenderParam {
    pub kind: c_int,
    pub data: *mut c_void,
}

pub type UpdateCallback = unsafe extern "C" fn(*mut c_void);

/// Function pointers resolved from the loaded libmpv.
pub struct Api {
    _lib: libloading::Library,
    pub client_api_version: unsafe extern "C" fn() -> u32,
    pub create: unsafe extern "C" fn() -> Handle,
    pub initialize: unsafe extern "C" fn(Handle) -> c_int,
    pub terminate_destroy: unsafe extern "C" fn(Handle),
    pub error_string: unsafe extern "C" fn(c_int) -> *const c_char,
    pub free: unsafe extern "C" fn(*mut c_void),
    pub set_option_string: unsafe extern "C" fn(Handle, *const c_char, *const c_char) -> c_int,
    pub set_property_string: unsafe extern "C" fn(Handle, *const c_char, *const c_char) -> c_int,
    pub set_property: unsafe extern "C" fn(Handle, *const c_char, c_int, *mut c_void) -> c_int,
    pub get_property: unsafe extern "C" fn(Handle, *const c_char, c_int, *mut c_void) -> c_int,
    pub get_property_string: unsafe extern "C" fn(Handle, *const c_char) -> *mut c_char,
    pub observe_property: unsafe extern "C" fn(Handle, u64, *const c_char, c_int) -> c_int,
    pub command: unsafe extern "C" fn(Handle, *mut *const c_char) -> c_int,
    pub command_async: unsafe extern "C" fn(Handle, u64, *mut *const c_char) -> c_int,
    pub request_log_messages: unsafe extern "C" fn(Handle, *const c_char) -> c_int,
    pub wait_event: unsafe extern "C" fn(Handle, c_double) -> *mut Event,
    pub wakeup: unsafe extern "C" fn(Handle),
    pub render_context_create: unsafe extern "C" fn(*mut RenderContext, Handle, *mut RenderParam) -> c_int,
    pub render_context_set_update_callback: unsafe extern "C" fn(RenderContext, Option<UpdateCallback>, *mut c_void),
    pub render_context_update: unsafe extern "C" fn(RenderContext) -> u64,
    pub render_context_render: unsafe extern "C" fn(RenderContext, *mut RenderParam) -> c_int,
    pub render_context_free: unsafe extern "C" fn(RenderContext),
}

static API: OnceLock<Api> = OnceLock::new();

impl Api {
    /// Resolves every function from `lib`.
    ///
    /// # Safety
    /// `lib` must be a libmpv.
    pub unsafe fn from_library(lib: libloading::Library) -> Result<Api, libloading::Error> {
        macro_rules! sym {
            ($name:literal) => {
                // SAFETY: the signature matches libmpv's client.h / render.h.
                unsafe { *lib.get(concat!($name, "\0").as_bytes())? }
            };
        }
        Ok(Api {
            client_api_version: sym!("mpv_client_api_version"),
            create: sym!("mpv_create"),
            initialize: sym!("mpv_initialize"),
            terminate_destroy: sym!("mpv_terminate_destroy"),
            error_string: sym!("mpv_error_string"),
            free: sym!("mpv_free"),
            set_option_string: sym!("mpv_set_option_string"),
            set_property_string: sym!("mpv_set_property_string"),
            set_property: sym!("mpv_set_property"),
            get_property: sym!("mpv_get_property"),
            get_property_string: sym!("mpv_get_property_string"),
            observe_property: sym!("mpv_observe_property"),
            command: sym!("mpv_command"),
            command_async: sym!("mpv_command_async"),
            request_log_messages: sym!("mpv_request_log_messages"),
            wait_event: sym!("mpv_wait_event"),
            wakeup: sym!("mpv_wakeup"),
            render_context_create: sym!("mpv_render_context_create"),
            render_context_set_update_callback: sym!("mpv_render_context_set_update_callback"),
            render_context_update: sym!("mpv_render_context_update"),
            render_context_render: sym!("mpv_render_context_render"),
            render_context_free: sym!("mpv_render_context_free"),
            _lib: lib,
        })
    }

    /// Installs the table once; later calls keep the first.
    pub fn install(api: Api) -> &'static Api {
        let _ = API.set(api);
        API.get().expect("installed")
    }

    pub fn get() -> Option<&'static Api> {
        API.get()
    }

    pub fn error(&self, code: c_int) -> String {
        // SAFETY: mpv_error_string returns a static string for any code.
        let ptr = unsafe { (self.error_string)(code) };
        if ptr.is_null() {
            return format!("error {code}");
        }
        unsafe { CStr::from_ptr(ptr) }.to_string_lossy().into_owned()
    }
}

/// A Rust string from a C string mpv owns (empty for null).
///
/// # Safety
/// `ptr` is null or a NUL-terminated string that lives for the call.
pub unsafe fn string(ptr: *const c_char) -> String {
    if ptr.is_null() {
        String::new()
    } else {
        // SAFETY: mpv hands out NUL-terminated UTF-8.
        unsafe { CStr::from_ptr(ptr) }.to_string_lossy().into_owned()
    }
}
