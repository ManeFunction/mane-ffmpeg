use mane_core::platform::homebrew;
use std::os::unix::fs::PermissionsExt;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Instant;

fn exe(dir: &Path, name: &str, body: &str) -> PathBuf {
    let path = dir.join(name);
    std::fs::write(&path, format!("#!/bin/sh\n{body}\n")).unwrap();
    std::fs::set_permissions(&path, std::fs::Permissions::from_mode(0o700)).unwrap();
    path
}

fn url(path: &Path) -> String {
    format!("file://{}", path.display())
}

fn run_installer(body: &str) -> Option<i32> {
    let dir = tempfile::tempdir().unwrap();
    let installer = exe(dir.path(), "install.sh", body);
    let work = dir.path().join("work");
    let script = homebrew::write_installer_script(&work, &url(&installer)).unwrap();
    let _ = Command::new(script).output();
    homebrew::read_installer_status(&work)
}

#[test]
fn installer_script_reports_success() {
    assert_eq!(run_installer("echo installing; exit 0"), Some(0));
}

#[test]
fn installer_script_reports_the_installers_exit_code() {
    assert_eq!(run_installer("exit 7"), Some(7));
}

#[test]
fn installer_script_reports_a_failed_download() {
    let dir = tempfile::tempdir().unwrap();
    let work = dir.path().join("work");
    let script = homebrew::write_installer_script(&work, &url(&dir.path().join("missing.sh"))).unwrap();
    let _ = Command::new(script).output();
    assert_eq!(homebrew::read_installer_status(&work), Some(1));
}

#[test]
fn installer_script_reports_a_closed_window_as_a_failure() {
    let dir = tempfile::tempdir().unwrap();
    let installer = exe(dir.path(), "install.sh", "sleep 30");
    let work = dir.path().join("work");
    let script = homebrew::write_installer_script(&work, &url(&installer)).unwrap();
    let _ = Command::new("/bin/bash").args(["-c", "set -m; \"$0\" & pid=$!; sleep 1; kill -HUP -- -$pid; wait $pid"]).arg(&script).output();
    assert_eq!(homebrew::read_installer_status(&work), Some(129));
}

#[test]
fn installer_status_is_unknown_until_the_script_has_written_it() {
    let dir = tempfile::tempdir().unwrap();
    homebrew::write_installer_script(dir.path(), homebrew::INSTALLER_URL).unwrap();
    assert_eq!(homebrew::read_installer_status(dir.path()), None);
    std::fs::write(dir.path().join("install-homebrew.status"), "").unwrap();
    assert_eq!(homebrew::read_installer_status(dir.path()), None);
}

#[test]
fn writing_the_installer_script_forgets_the_last_run() {
    let dir = tempfile::tempdir().unwrap();
    homebrew::write_installer_script(dir.path(), homebrew::INSTALLER_URL).unwrap();
    std::fs::write(dir.path().join("install-homebrew.status"), "1\n").unwrap();
    homebrew::write_installer_script(dir.path(), homebrew::INSTALLER_URL).unwrap();
    assert_eq!(homebrew::read_installer_status(dir.path()), None);
}

#[test]
fn install_passes_the_formulae_and_streams_stdout_and_stderr_in_order() {
    let dir = tempfile::tempdir().unwrap();
    let brew = exe(dir.path(), "brew", "echo \"args: $*\"; echo 'Error: no bottle' >&2; echo \"hints: $HOMEBREW_NO_ENV_HINTS\"; exit 3");
    let mut lines = Vec::new();
    let code = homebrew::install(
        &brew.to_string_lossy(),
        &["ffmpeg".into(), "mpv".into()],
        &mut |l| lines.push(l),
        Arc::new(AtomicBool::new(false)),
    )
    .unwrap();
    assert_eq!(code, 3);
    assert_eq!(lines, ["args: install ffmpeg mpv", "Error: no bottle", "hints: 1"]);
}

#[test]
fn stopping_an_install_interrupts_brew_like_ctrl_c() {
    let dir = tempfile::tempdir().unwrap();
    let brew = exe(dir.path(), "brew", "trap 'echo interrupted; kill $!; exit 130' INT\necho ready\nsleep 30 &\nwait");
    let cancel = Arc::new(AtomicBool::new(false));
    let mut lines = Vec::new();
    let mut started: Option<Instant> = None;
    let c = cancel.clone();
    homebrew::install(
        &brew.to_string_lossy(),
        &["ffmpeg".into()],
        &mut |l| {
            if l == "ready" {
                started = Some(Instant::now());
                c.store(true, Ordering::Release);
            }
            lines.push(l);
        },
        cancel,
    )
    .unwrap();
    assert!(started.unwrap().elapsed().as_secs() < 4);
    assert!(lines.iter().any(|l| l == "interrupted"));
}
