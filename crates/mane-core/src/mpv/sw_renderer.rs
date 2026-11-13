//! mpv's software render API: mpv draws the current frame, scaled and letterboxed, into a BGRA buffer.
//! The update callback fires on an mpv thread; render from your own thread, never from the callback.

use super::ffi::{self, Api};
use super::player::{MpvError, MpvPlayer};
use std::ffi::c_void;

pub struct SoftwareRenderer {
    api: &'static Api,
    context: ffi::RenderContext,
    callback: *mut Box<dyn Fn() + Send + Sync>,
}

// SAFETY: render calls are made from one thread at a time (the owner's render thread).
unsafe impl Send for SoftwareRenderer {}

unsafe extern "C" fn on_update(data: *mut c_void) {
    let callback = unsafe { &*(data as *const Box<dyn Fn() + Send + Sync>) };
    callback();
}

impl SoftwareRenderer {
    /// `invalidated` runs on an mpv thread whenever mpv has something new to show.
    pub fn new(player: &MpvPlayer, invalidated: Box<dyn Fn() + Send + Sync>) -> Result<SoftwareRenderer, MpvError> {
        let api = player.api();
        let mut context: ffi::RenderContext = std::ptr::null_mut();
        let mut params = [
            ffi::RenderParam { kind: ffi::RENDER_PARAM_API_TYPE, data: c"sw".as_ptr() as *mut c_void },
            ffi::RenderParam { kind: ffi::RENDER_PARAM_INVALID, data: std::ptr::null_mut() },
        ];
        let result = unsafe { (api.render_context_create)(&mut context, player.handle(), params.as_mut_ptr()) };
        if result < 0 {
            return Err(MpvError(format!("Software rendering isn't available: {}", api.error(result))));
        }
        let callback = Box::into_raw(Box::new(invalidated));
        unsafe { (api.render_context_set_update_callback)(context, Some(on_update), callback as *mut c_void) };
        Ok(SoftwareRenderer { api, context, callback })
    }

    /// True when mpv wants a new frame drawn (also processes mpv's internal render updates).
    pub fn update(&self) -> bool {
        unsafe { (self.api.render_context_update)(self.context) & ffi::RENDER_UPDATE_FRAME != 0 }
    }

    /// Draws the current frame into `buffer` (BGRA, `stride` bytes per row).
    pub fn render(&self, width: i32, height: i32, stride: usize, buffer: &mut [u8]) -> bool {
        if width <= 0 || height <= 0 || buffer.len() < stride * height as usize {
            return false;
        }
        // mpv fills in opaque alpha, so frames go straight into a BGRA texture. With "bgr0" the 4th byte is 0
        // and needs a pass over every pixel.
        let mut size = [width, height];
        let mut row_bytes: usize = stride;
        let mut params = [
            ffi::RenderParam { kind: ffi::RENDER_PARAM_SW_SIZE, data: size.as_mut_ptr() as *mut c_void },
            ffi::RenderParam { kind: ffi::RENDER_PARAM_SW_FORMAT, data: c"bgra".as_ptr() as *mut c_void },
            ffi::RenderParam { kind: ffi::RENDER_PARAM_SW_STRIDE, data: &mut row_bytes as *mut usize as *mut c_void },
            ffi::RenderParam { kind: ffi::RENDER_PARAM_SW_POINTER, data: buffer.as_mut_ptr() as *mut c_void },
            ffi::RenderParam { kind: ffi::RENDER_PARAM_INVALID, data: std::ptr::null_mut() },
        ];
        unsafe { (self.api.render_context_render)(self.context, params.as_mut_ptr()) >= 0 }
    }
}

impl Drop for SoftwareRenderer {
    fn drop(&mut self) {
        unsafe {
            (self.api.render_context_set_update_callback)(self.context, None, std::ptr::null_mut());
            (self.api.render_context_free)(self.context);
            drop(Box::from_raw(self.callback));
        }
    }
}
