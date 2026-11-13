//! Desktop notifications without native bindings: osascript on macOS, notify-send/gdbus on Linux. Best-effort; the
//! export panel always shows the result.

use crate::executables;
use crate::persistence::app_paths::APP_NAME;
use std::process::{Command, Stdio};

/// Shows a notification in the background.
pub fn notify(title: &str, body: &str) {
    let (title, body) = (title.to_string(), body.to_string());
    std::thread::spawn(move || {
        if cfg!(target_os = "macos") {
            mac(&title, &body);
        } else {
            linux(&title, &body);
        }
    });
}

fn run(program: &str, args: &[&str]) -> bool {
    Command::new(program)
        .args(args)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .status()
        .map(|s| s.success())
        .unwrap_or(false)
}

// Title and body go in as argv, so no AppleScript escaping is needed.
fn mac(title: &str, body: &str) {
    run(
        "/usr/bin/osascript",
        &["-e", "on run argv", "-e", "display notification (item 2 of argv) with title (item 1 of argv)", "-e", "end run", title, body],
    );
}

fn linux(title: &str, body: &str) {
    if let Some(notify_send) = executables::find("notify-send", None)
        && run(&notify_send, &["--app-name", APP_NAME, "--icon", "video-x-generic", title, body])
    {
        return;
    }
    if let Some(gdbus) = executables::find("gdbus", None) {
        run(
            &gdbus,
            &[
                "call",
                "--session",
                "--dest",
                "org.freedesktop.Notifications",
                "--object-path",
                "/org/freedesktop/Notifications",
                "--method",
                "org.freedesktop.Notifications.Notify",
                APP_NAME,
                "0",
                "video-x-generic",
                title,
                body,
                "[]",
                "{}",
                "8000",
            ],
        );
    }
}
