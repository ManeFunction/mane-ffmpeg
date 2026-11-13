use mane_core::updates::installer::{ApplyPlan, write_apply_script};
use mane_core::updates::release_package::ReleasePackage;
use mane_core::updates::{changelog, parse_version};
use mane_core::version::Version;
use std::os::unix::fs::PermissionsExt;
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};

#[test]
fn parses_tags() {
    for (tag, expected) in [("v0.9.1", "0.9.1"), ("0.10.0", "0.10.0"), ("v1.2", "1.2.0"), ("v1.0.0-rc.1", "1.0.0"), ("1.4.2+abc", "1.4.2")]
    {
        assert_eq!(parse_version(tag), Version::parse(expected), "{tag}");
    }
    for tag in ["Unreleased", "", "vnext"] {
        assert!(parse_version(tag).is_none());
    }
    assert_eq!(Version::parse("0.9.0.0"), parse_version("v0.9.0"));
}

const MARKDOWN: &str = "# Changelog

All notable changes to this project will be documented in this file.

## [Unreleased]
### Added
- Not out yet.

## [0.10.0] - 2026-11-20
### Added
- Ten.

## [0.9.2] - 2026-11-10
### Fixed
- Nine two.

## [0.9.1] - 2026-11-01
### Fixed
- Nine one.

## [0.9.0] - 2026-10-26
### Added
- First release.

[0.9.1]: https://github.com/ManeFunction/mane-ffmpeg/compare/v0.9.0...v0.9.1
";

#[test]
fn takes_the_versions_after_the_current_one_up_to_the_latest() {
    let text = changelog::between(MARKDOWN, Version::new(0, 9, 0), Version::new(0, 9, 2)).unwrap();
    assert_eq!(text, "## [0.9.2] - 2026-11-10\n### Fixed\n- Nine two.\n\n## [0.9.1] - 2026-11-01\n### Fixed\n- Nine one.");
}

#[test]
fn leaves_out_unreleased_older_versions_and_link_references() {
    let text = changelog::between(MARKDOWN, Version::new(0, 8, 0), Version::new(0, 10, 0)).unwrap();
    assert!(text.contains("- Ten.") && text.contains("- First release."));
    assert!(!text.contains("Not out yet") && !text.contains("compare/"));
}

#[test]
fn is_none_without_newer_sections() {
    assert!(changelog::between(MARKDOWN, Version::new(0, 10, 0), Version::new(0, 10, 0)).is_none());
}

struct Dir(tempfile::TempDir);

impl Dir {
    fn new() -> Dir {
        Dir(tempfile::tempdir().unwrap())
    }
    fn p(&self) -> &Path {
        self.0.path()
    }
    fn installed(&self) -> PathBuf {
        self.p().join("opt").join("app")
    }
    fn staged(&self) -> PathBuf {
        self.p().join("work").join("new").join("mane-ffmpeg")
    }
    fn log(&self) -> PathBuf {
        self.p().join("logs").join("update.log")
    }
    fn write(&self, path: &Path, text: &str) {
        std::fs::create_dir_all(path.parent().unwrap()).unwrap();
        std::fs::write(path, text).unwrap();
    }
    fn run(&self, run_installer: bool, wait_for: Vec<u32>, staged: Option<PathBuf>) -> String {
        let plan = ApplyPlan {
            package: ReleasePackage::LinuxTarball,
            installed: self.installed(),
            staged: staged.unwrap_or_else(|| self.staged()),
            version: Version::new(1, 2, 3),
            wait_for,
            relaunch: false,
            reopen: vec![],
            run_installer,
            log: self.log(),
            clean_up: Some(self.p().join("work")),
        };
        let script = write_apply_script(&self.p().join("work"), &plan).unwrap();
        let _ = std::process::Command::new("/bin/sh").arg(script).output();
        std::fs::read_to_string(self.log()).unwrap()
    }
    fn entries(&self, sub: &str) -> Vec<String> {
        std::fs::read_dir(self.p().join(sub)).unwrap().map(|e| e.unwrap().file_name().to_string_lossy().into_owned()).collect()
    }
}

#[test]
fn replaces_the_app_folder() {
    let d = Dir::new();
    d.write(&d.installed().join("ManeFfmpeg"), "old");
    d.write(&d.installed().join("removed.dll"), "old");
    d.write(&d.staged().join("app").join("ManeFfmpeg"), "new");
    let log = d.run(false, vec![], None);
    assert!(log.contains("Installed."), "{log}");
    assert_eq!(std::fs::read_to_string(d.installed().join("ManeFfmpeg")).unwrap(), "new");
    assert!(!d.installed().join("removed.dll").exists());
    assert_eq!(d.entries("opt"), ["app"]);
    assert!(!d.p().join("work").exists());
}

#[test]
fn keeps_the_old_version_when_the_new_one_is_missing() {
    let d = Dir::new();
    d.write(&d.installed().join("ManeFfmpeg"), "old");
    let log = d.run(false, vec![], Some(d.p().join("nothing-here")));
    assert!(log.contains("The update failed"));
    assert_eq!(std::fs::read_to_string(d.installed().join("ManeFfmpeg")).unwrap(), "old");
    assert_eq!(d.entries("opt"), ["app"]);
}

#[test]
fn runs_the_new_install_script_for_the_standard_install() {
    let d = Dir::new();
    d.write(&d.installed().join("ManeFfmpeg"), "old");
    d.write(&d.staged().join("app").join("ManeFfmpeg"), "new");
    let install = d.staged().join("install.sh");
    d.write(&install, &format!("#!/bin/sh\ncp -R \"$(dirname \"$0\")/app\" '{}/'\n", d.p().join("opt").display()));
    std::fs::set_permissions(&install, std::fs::Permissions::from_mode(0o700)).unwrap();
    let log = d.run(true, vec![], None);
    assert!(log.contains("Installed."), "{log}");
    assert_eq!(std::fs::read_to_string(d.installed().join("ManeFfmpeg")).unwrap(), "new");
    assert_eq!(d.entries("opt"), ["app"]);
}

#[test]
fn restores_the_old_version_when_the_install_script_fails() {
    let d = Dir::new();
    d.write(&d.installed().join("ManeFfmpeg"), "old");
    let install = d.staged().join("install.sh");
    d.write(&install, "#!/bin/sh\nexit 3\n");
    std::fs::set_permissions(&install, std::fs::Permissions::from_mode(0o700)).unwrap();
    let log = d.run(true, vec![], None);
    assert!(log.contains("restoring the old version"));
    assert_eq!(std::fs::read_to_string(d.installed().join("ManeFfmpeg")).unwrap(), "old");
}

#[test]
fn waits_for_the_app_to_quit() {
    let d = Dir::new();
    d.write(&d.installed().join("ManeFfmpeg"), "old");
    d.write(&d.staged().join("app").join("ManeFfmpeg"), "new");
    let mut app = std::process::Command::new("sleep").arg("1").spawn().unwrap();
    let started = Instant::now();
    let pid = app.id();
    std::thread::spawn(move || {
        let _ = app.wait();
    });
    d.run(false, vec![pid], None);
    assert!(started.elapsed() >= Duration::from_millis(800), "didn't wait: {:?}", started.elapsed());
    assert_eq!(std::fs::read_to_string(d.installed().join("ManeFfmpeg")).unwrap(), "new");
}

#[test]
fn assets_are_named_like_the_release_workflow_names_them() {
    assert_eq!(ReleasePackage::MacApp.asset_name(Version::new(1, 2, 3), "osx-arm64"), "mane-ffmpeg-1.2.3-osx-arm64.zip");
    assert_eq!(ReleasePackage::LinuxTarball.asset_name(Version::new(1, 2, 3), "linux-x64"), "mane-ffmpeg-1.2.3-linux-x64.tar.gz");
    assert_eq!((ReleasePackage::MacApp.platform(), ReleasePackage::LinuxTarball.platform()), ("osx", "linux"));
}

#[test]
fn the_staged_app_is_where_each_package_keeps_it() {
    let w = Path::new("/w/new");
    assert_eq!(
        ReleasePackage::MacApp.executable_in(&w.join(ReleasePackage::MacApp.staged_name())),
        PathBuf::from("/w/new/Mane FFmpeg.app/Contents/MacOS/ManeFfmpeg")
    );
    assert_eq!(
        ReleasePackage::LinuxTarball.executable_in(&w.join(ReleasePackage::LinuxTarball.staged_name())),
        PathBuf::from("/w/new/mane-ffmpeg/app/ManeFfmpeg")
    );
}

#[test]
fn a_mac_copy_is_the_bundle_it_runs_from() {
    let d = tempfile::tempdir().unwrap();
    let bundle = d.path().join("Mane FFmpeg.app");
    let exe = bundle.join("Contents").join("MacOS").join("ManeFfmpeg");
    assert!(ReleasePackage::MacApp.installed_path(&exe).is_none());
    std::fs::create_dir_all(exe.parent().unwrap()).unwrap();
    std::fs::write(bundle.join("Contents").join("Info.plist"), "").unwrap();
    assert_eq!(ReleasePackage::MacApp.installed_path(&exe), Some(bundle));
    assert!(ReleasePackage::MacApp.installed_path(&d.path().join("bin").join("ManeFfmpeg")).is_none());
    assert!(
        ReleasePackage::MacApp.cannot_replace_reason(Path::new("/private/var/folders/x/AppTranslocation/1/d/Mane FFmpeg.app")).is_some()
    );
    assert!(ReleasePackage::MacApp.cannot_replace_reason(Path::new("/Applications/Mane FFmpeg.app")).is_none());
}

#[test]
fn a_linux_copy_is_the_bundles_app_folder() {
    assert_eq!(ReleasePackage::LinuxTarball.installed_path(Path::new("/opt/x/app/ManeFfmpeg")), Some(PathBuf::from("/opt/x/app")));
    assert!(ReleasePackage::LinuxTarball.installed_path(Path::new("/usr/bin/ManeFfmpeg")).is_none());
    let standard = mane_core::persistence::app_paths::home().join(".local/opt/mane-ffmpeg/app");
    assert!(ReleasePackage::LinuxTarball.runs_installer(&standard));
    assert!(!ReleasePackage::LinuxTarball.runs_installer(Path::new("/opt/x/app")));
    assert!(!ReleasePackage::MacApp.runs_installer(&standard));
}
