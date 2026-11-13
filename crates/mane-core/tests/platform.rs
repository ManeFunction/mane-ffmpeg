use mane_core::platform::sleep_inhibitor::SleepInhibitor;

/// Holds a lock while alive; on Linux it shows in `systemd-inhibit --list` when systemd is there.
#[test]
fn holds_a_lock_until_dropped() {
    let reason = format!("Exporting test-{}.mp4", mane_core::model::ids::new());
    let list = || {
        std::process::Command::new("systemd-inhibit")
            .args(["--list", "--no-pager"])
            .output()
            .map(|o| String::from_utf8_lossy(&o.stdout).into_owned())
            .unwrap_or_default()
    };
    if mane_core::executables::find("systemd-inhibit", None).is_none() {
        return;
    }
    let held = SleepInhibitor::acquire(&reason);
    let mut seen = false;
    for _ in 0..50 {
        if list().contains(&reason) {
            seen = true;
            break;
        }
        std::thread::sleep(std::time::Duration::from_millis(50));
    }
    if !held.is_held() {
        return;
    }
    assert!(seen, "the lock wasn't listed");
    drop(held);
    std::thread::sleep(std::time::Duration::from_millis(300));
    assert!(!list().contains(&reason));
}
