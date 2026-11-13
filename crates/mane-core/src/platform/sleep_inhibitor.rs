//! Keeps the computer from going to sleep on its own while held: an IOKit power assertion on macOS, a
//! systemd-inhibit lock on Linux. The display can still sleep. Both are released if the app dies.

use crate::executables;
use crate::persistence::{app_log, app_paths::APP_NAME};
use std::process::{Child, Command, Stdio};

#[derive(Default)]
pub struct SleepInhibitor {
    #[allow(dead_code)]
    assertion: Option<u32>,
    inhibit: Option<Child>,
}

impl SleepInhibitor {
    /// Best-effort: when the platform refuses, the failure goes to the log and the returned inhibitor does nothing.
    /// `reason` shows in `pmset -g assertions` and `systemd-inhibit --list`.
    pub fn acquire(reason: &str) -> SleepInhibitor {
        let mut s = SleepInhibitor::default();
        #[cfg(target_os = "macos")]
        {
            s.assertion = mac::assertion(reason);
        }
        #[cfg(not(target_os = "macos"))]
        {
            s.inhibit = linux_inhibit(reason);
        }
        s
    }

    /// Whether a lock is held (tests).
    pub fn is_held(&self) -> bool {
        self.assertion.is_some() || self.inhibit.is_some()
    }
}

impl Drop for SleepInhibitor {
    fn drop(&mut self) {
        #[cfg(target_os = "macos")]
        if let Some(id) = self.assertion.take() {
            mac::release(id);
        }
        // systemd-inhibit holds its lock until cat exits, and cat exits when its stdin closes.
        if let Some(mut child) = self.inhibit.take() {
            drop(child.stdin.take());
            let _ = child.wait();
        }
    }
}

#[cfg_attr(target_os = "macos", allow(dead_code))]
fn linux_inhibit(reason: &str) -> Option<Child> {
    let Some(systemd_inhibit) = executables::find("systemd-inhibit", None) else {
        app_log::write("Can't keep the computer awake: systemd-inhibit not found");
        return None;
    };
    // cat reads the pipe below, so the lock also goes away when this process dies.
    match Command::new(systemd_inhibit)
        .args(["--what=idle:sleep", "--mode=block", &format!("--who={APP_NAME}"), &format!("--why={reason}"), "cat"])
        .stdin(Stdio::piped())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
    {
        Ok(c) => Some(c),
        Err(e) => {
            app_log::write(&format!("Can't keep the computer awake: {e}"));
            None
        }
    }
}

#[cfg(target_os = "macos")]
mod mac {
    use crate::persistence::app_log;
    use std::ffi::{CString, c_char, c_void};

    const UTF8: u32 = 0x0800_0100;
    const LEVEL_ON: u32 = 255;

    #[link(name = "CoreFoundation", kind = "framework")]
    unsafe extern "C" {
        fn CFStringCreateWithCString(alloc: *const c_void, text: *const c_char, encoding: u32) -> *const c_void;
        fn CFRelease(value: *const c_void);
    }

    #[link(name = "IOKit", kind = "framework")]
    unsafe extern "C" {
        fn IOPMAssertionCreateWithName(kind: *const c_void, level: u32, name: *const c_void, id: *mut u32) -> i32;
        fn IOPMAssertionRelease(id: u32) -> i32;
    }

    pub fn assertion(reason: &str) -> Option<u32> {
        let kind = CString::new("PreventUserIdleSystemSleep").unwrap();
        let name = CString::new(reason.replace('\0', "")).unwrap();
        unsafe {
            let k = CFStringCreateWithCString(std::ptr::null(), kind.as_ptr(), UTF8);
            let n = CFStringCreateWithCString(std::ptr::null(), name.as_ptr(), UTF8);
            let mut id = 0u32;
            let result = IOPMAssertionCreateWithName(k, LEVEL_ON, n, &mut id);
            CFRelease(n);
            CFRelease(k);
            if result == 0 {
                Some(id)
            } else {
                app_log::write(&format!("Can't keep the computer awake: IOPMAssertionCreateWithName returned 0x{result:08x}"));
                None
            }
        }
    }

    pub fn release(id: u32) {
        unsafe { IOPMAssertionRelease(id) };
    }
}
