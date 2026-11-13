//! Installs missing tools with Homebrew (macOS). Homebrew's own installer asks for the user's password and to press
//! Return, so it runs in Terminal and reports back through a status file; `brew install` runs here.

use crate::executables;
use std::io::{BufRead, BufReader};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};

/// The installer behind the one-liner on brew.sh.
pub const INSTALLER_URL: &str = "https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh";
const SCRIPT_NAME: &str = "install-homebrew.command";
const STATUS_NAME: &str = "install-homebrew.status";
/// How long a stopped `brew install` gets to clean up after Ctrl-C before it's killed.
const STOP_GRACE: Duration = Duration::from_secs(5);

/// The command brew.sh shows, for display.
pub fn installer_command() -> String {
    format!("/bin/bash -c \"$(curl -fsSL {INSTALLER_URL})\"")
}

pub fn find() -> Option<String> {
    executables::find("brew", None)
}

fn quote(v: &str) -> String {
    format!("'{}'", v.replace('\'', "'\\''"))
}

/// Writes a .command script into `directory` that runs Homebrew's installer and then writes its exit code to the
/// status file read by [`read_installer_status`]. Closing the window or pressing Ctrl-C records 129 or 130: a plain
/// EXIT trap would record 0 for a closed window.
pub fn write_installer_script(directory: &Path, installer_url: &str) -> std::io::Result<PathBuf> {
    std::fs::create_dir_all(directory)?;
    let status = directory.join(STATUS_NAME);
    let _ = std::fs::remove_file(&status);
    let script = directory.join(SCRIPT_NAME);
    let text = format!(
        r#"#!/bin/bash
# Written by Mane FFmpeg: runs Homebrew's installer and tells the app how it ended.
status_file={status}
trap 'echo $? > "$status_file"' EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM
clear
echo "Installing Homebrew for Mane FFmpeg."
echo "The installer asks for your Mac password and may install Apple's Command Line Tools."
echo
installer="$(curl -fsSL {url})" || {{ echo "Couldn't download Homebrew's installer. Check your internet connection."; exit 1; }}
/bin/bash -c "$installer"
code=$?
echo
if [ $code -eq 0 ]; then
  echo "Homebrew is installed. Go back to Mane FFmpeg to continue; you can close this window."
  echo "(The \"Next steps\" above are only needed to use brew in Terminal yourself.)"
else
  echo "Homebrew's installer stopped (exit code $code)."
fi
exit $code
"#,
        status = quote(&status.to_string_lossy()),
        url = quote(installer_url)
    );
    std::fs::write(&script, text)?;
    use std::os::unix::fs::PermissionsExt;
    std::fs::set_permissions(&script, std::fs::Permissions::from_mode(0o755))?;
    Ok(script)
}

/// The installer's exit code once its script has ended, or None while it runs (or before it started).
pub fn read_installer_status(directory: &Path) -> Option<i32> {
    std::fs::read_to_string(directory.join(STATUS_NAME)).ok()?.trim().parse().ok()
}

/// Runs a .command script in the user's terminal app (Terminal, unless they picked another one).
pub fn open_in_terminal(script: &Path) -> std::io::Result<()> {
    let status = Command::new("/usr/bin/open").arg(script).status()?;
    if !status.success() {
        return Err(std::io::Error::other(format!("open exited with code {}.", status.code().unwrap_or(-1))));
    }
    Ok(())
}

/// Runs `brew install` for `formulae`, passing each line it prints (stdout and stderr, in order) to `output`;
/// returns brew's exit code. Setting `cancel` interrupts brew like Ctrl-C, then kills it. Blocks.
pub fn install(brew: &str, formulae: &[String], output: &mut dyn FnMut(String), cancel: Arc<AtomicBool>) -> std::io::Result<i32> {
    // stderr goes into the stdout pipe, so errors stay next to the step that printed them. exec keeps brew the
    // process that gets the interrupt.
    let mut child = Command::new("/bin/sh")
        .arg("-c")
        .arg("exec \"$0\" \"$@\" 2>&1")
        .arg(brew)
        .arg("install")
        .args(formulae)
        .env("HOMEBREW_NO_ENV_HINTS", "1")
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .spawn()?;
    let pid = child.id() as i32;
    let done = Arc::new(AtomicBool::new(false));
    let (c, d) = (cancel.clone(), done.clone());
    std::thread::spawn(move || {
        while !d.load(Ordering::Acquire) {
            if c.load(Ordering::Acquire) {
                unsafe { libc::kill(pid, libc::SIGINT) };
                let until = Instant::now() + STOP_GRACE;
                while !d.load(Ordering::Acquire) && Instant::now() < until {
                    std::thread::sleep(Duration::from_millis(100));
                }
                if !d.load(Ordering::Acquire) {
                    unsafe { libc::kill(pid, libc::SIGKILL) };
                }
                return;
            }
            std::thread::sleep(Duration::from_millis(100));
        }
    });
    for line in BufReader::new(child.stdout.take().unwrap()).lines().map_while(Result::ok) {
        output(line);
    }
    let status = child.wait();
    done.store(true, Ordering::Release);
    Ok(status?.code().unwrap_or(-1))
}
