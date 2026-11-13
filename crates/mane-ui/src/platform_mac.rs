//! macOS-only helpers: the pointer position during a drop from Finder, moving and resizing a window in one step, and
//! files opened from Finder ("open document" events).

#![cfg(target_os = "macos")]

use std::sync::Mutex;

static OPENED: Mutex<Vec<String>> = Mutex::new(Vec::new());

/// Files macOS asked the app to open (double-clicked projects, files dropped on the Dock icon) since the last call.
pub fn take_opened_files() -> Vec<String> {
    std::mem::take(&mut *OPENED.lock().unwrap())
}

/// Adds `application:openURLs:` to the app delegate winit installs, so Finder's open requests reach the app.
pub fn install_open_handler() {
    use objc2::runtime::{AnyClass, AnyObject, Sel};
    use objc2::sel;
    use objc2_app_kit::NSApplication;
    use objc2_foundation::{MainThreadMarker, NSArray, NSURL};

    extern "C-unwind" fn open_urls(_this: &AnyObject, _sel: Sel, _app: &AnyObject, urls: &NSArray<NSURL>) {
        let mut opened = OPENED.lock().unwrap();
        for url in urls.iter() {
            if let Some(path) = url.path() {
                opened.push(path.to_string());
            }
        }
    }

    let Some(mtm) = MainThreadMarker::new() else { return };
    let app = NSApplication::sharedApplication(mtm);
    let Some(delegate) = app.delegate() else { return };
    let object: &AnyObject = (*delegate).as_ref();
    let class: &AnyClass = object.class();
    unsafe {
        let types = c"v@:@@";
        let imp: objc2::runtime::Imp = std::mem::transmute(open_urls as extern "C-unwind" fn(_, _, _, _));
        objc2::ffi::class_addMethod(class as *const AnyClass as *mut AnyClass, sel!(application:openURLs:), imp, types.as_ptr());
    }
}

/// The pointer, in the window's points: during a drag from Finder the window gets no pointer moves.
pub fn pointer_in_window(ctx: &egui::Context) -> Option<egui::Pos2> {
    use objc2_app_kit::NSEvent;
    let screen = NSEvent::mouseLocation();
    let inner = ctx.input(|i| i.viewport().inner_rect)?;
    let monitor = ctx.input(|i| i.viewport().monitor_size)?;
    // Cocoa's origin is the bottom-left of the main screen; egui's the top-left.
    let x = screen.x as f32 - inner.min.x;
    let y = monitor.y - screen.y as f32 - inner.min.y;
    Some(egui::pos2(x, y))
}

/// Moves the top edge down by `top_down` and grows the height by `grow`, in points, in one native call: setting the
/// position and the size separately shows a frame with the window moved but not yet resized.
pub fn change_frame(ctx: &egui::Context, top_down: f32, grow: f32) -> bool {
    use objc2_app_kit::NSApplication;
    use objc2_foundation::MainThreadMarker;
    let _ = ctx;
    let Some(mtm) = MainThreadMarker::new() else { return false };
    let app = NSApplication::sharedApplication(mtm);
    let Some(window) = app.keyWindow() else { return false };
    let mut frame = window.frame();
    // Cocoa puts the origin at the bottom-left corner, with y growing upwards.
    let top = frame.origin.y + frame.size.height - top_down as f64;
    frame.size.height += grow as f64;
    frame.origin.y = top - frame.size.height;
    window.setFrame_display(frame, true);
    true
}
