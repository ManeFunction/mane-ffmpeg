//! How the release workflow packs the app for one platform, and how that package is installed: the asset's name, how
//! it unpacks, where the app sits in it and in an installed copy, and the shell steps that put a new copy in place.

use crate::executables;
use crate::version::Version;
use std::path::{Path, PathBuf};
use std::process::Command;

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum ReleasePackage {
    MacApp,
    LinuxTarball,
}

const LINUX_FOLDER: &str = "mane-ffmpeg";
const LINUX_APP_FOLDER: &str = "app";

impl ReleasePackage {
    /// This machine's kind of package.
    pub fn current() -> Option<ReleasePackage> {
        if cfg!(target_os = "macos") {
            Some(ReleasePackage::MacApp)
        } else if cfg!(target_os = "linux") {
            Some(ReleasePackage::LinuxTarball)
        } else {
            None
        }
    }

    /// The platform part of the release workflow's runtime identifiers ("osx" in "osx-arm64").
    pub fn platform(self) -> &'static str {
        match self {
            ReleasePackage::MacApp => "osx",
            ReleasePackage::LinuxTarball => "linux",
        }
    }

    fn archive_extension(self) -> &'static str {
        match self {
            ReleasePackage::MacApp => ".zip",
            ReleasePackage::LinuxTarball => ".tar.gz",
        }
    }

    /// The release asset, named like the release workflow names it.
    pub fn asset_name(self, version: Version, runtime_id: &str) -> String {
        format!("mane-ffmpeg-{version}-{runtime_id}{}", self.archive_extension())
    }

    /// The command that unpacks `archive` into `folder`.
    pub fn unpack(self, archive: &Path, folder: &Path) -> Command {
        match self {
            // ditto keeps the bundle's symlinks, permissions and signature.
            ReleasePackage::MacApp => {
                let mut c = Command::new("/usr/bin/ditto");
                c.args(["-x", "-k"]).arg(archive).arg(folder);
                c
            }
            ReleasePackage::LinuxTarball => {
                let mut c = Command::new(executables::find("tar", None).unwrap_or_else(|| "tar".into()));
                c.arg("-xzf").arg(archive).arg("-C").arg(folder);
                c
            }
        }
    }

    /// The package's top entry once unpacked: what an update stages.
    pub fn staged_name(self) -> &'static str {
        match self {
            ReleasePackage::MacApp => "Mane FFmpeg.app",
            ReleasePackage::LinuxTarball => LINUX_FOLDER,
        }
    }

    /// Where the app's executable is in a staged package.
    pub fn executable_in(self, staged: &Path) -> PathBuf {
        match self {
            ReleasePackage::MacApp => staged.join("Contents").join("MacOS").join("ManeFfmpeg"),
            ReleasePackage::LinuxTarball => staged.join(LINUX_APP_FOLDER).join("ManeFfmpeg"),
        }
    }

    /// What an update replaces when the app runs from `process_path`; None when it isn't a package.
    pub fn installed_path(self, process_path: &Path) -> Option<PathBuf> {
        match self {
            // The .app bundle holding Contents/MacOS/the executable.
            ReleasePackage::MacApp => {
                let bundle = process_path.parent()?.parent()?.parent()?;
                (bundle.extension().is_some_and(|e| e.eq_ignore_ascii_case("app")) && bundle.join("Contents").join("Info.plist").is_file())
                    .then(|| bundle.to_path_buf())
            }
            // The bundle's app/ folder.
            ReleasePackage::LinuxTarball => {
                let folder = process_path.parent()?;
                (folder.file_name()? == LINUX_APP_FOLDER).then(|| folder.to_path_buf())
            }
        }
    }

    /// Why the platform won't let the copy at `installed` be replaced, or None.
    pub fn cannot_replace_reason(self, installed: &Path) -> Option<&'static str> {
        (self == ReleasePackage::MacApp && installed.to_string_lossy().contains("/AppTranslocation/")).then_some(
            "macOS runs this copy from a read-only location. Move Mane FFmpeg to Applications and open it from there to update it here.",
        )
    }

    /// Whether updating the copy at `installed` runs the new package's own installer: install.sh installs into
    /// ~/.local/opt/mane-ffmpeg/app, so that copy is updated by running the new install.sh (it also refreshes the menu
    /// entry).
    pub fn runs_installer(self, installed: &Path) -> bool {
        self == ReleasePackage::LinuxTarball
            && std::path::absolute(installed).ok()
                == Some(crate::persistence::app_paths::home().join(".local").join("opt").join(LINUX_FOLDER).join(LINUX_APP_FOLDER))
    }

    /// Shell: copies the staged package's app from "$staged" to "$incoming".
    pub fn copy_in_script(self) -> &'static str {
        match self {
            ReleasePackage::MacApp => "/usr/bin/ditto \"$staged\" \"$incoming\"",
            ReleasePackage::LinuxTarball => "cp -R \"$staged/app\" \"$incoming\"",
        }
    }

    /// Shell: run on "$installed" once the new copy is in place.
    pub fn after_install_script(self) -> &'static str {
        match self {
            ReleasePackage::MacApp => "xattr -dr com.apple.quarantine \"$installed\" 2>/dev/null || true",
            ReleasePackage::LinuxTarball => ":",
        }
    }

    /// Shell: starts the installed app again, opening `reopen` (already quoted).
    pub fn relaunch_script(self, reopen: &str) -> String {
        match self {
            ReleasePackage::MacApp if !reopen.is_empty() => format!("open -a \"$installed\" {reopen}"),
            ReleasePackage::MacApp => "open \"$installed\"".into(),
            ReleasePackage::LinuxTarball => format!("nohup \"$installed/ManeFfmpeg\" {reopen} >/dev/null 2>&1 &"),
        }
    }
}
