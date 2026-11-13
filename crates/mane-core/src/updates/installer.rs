//! Downloads a release in the form the release workflow packs it (`ReleasePackage`) and swaps it in for the running
//! copy. The swap runs in a small shell script that waits for the app to quit first, so nothing is replaced while it
//! runs, and puts the old copy back if the new one can't be moved in.

use super::checker::ReleaseAsset;
use super::release_package::ReleasePackage;
use crate::persistence::{app_log, app_paths};
use crate::version::Version;
use sha2::{Digest, Sha256};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};

/// A downloaded and unpacked release, waiting to replace the installed copy. `staged` is the unpacked package: the
/// new "Mane FFmpeg.app" (macOS), or the "mane-ffmpeg" folder with app/ and install.sh (Linux).
#[derive(Clone, Debug, PartialEq)]
pub struct PreparedUpdate {
    pub version: Version,
    pub staged: PathBuf,
}

/// How long the script waits for the app to quit before it gives up (in 0.1 s steps).
const QUIT_WAIT_STEPS: u32 = 600;

pub fn work_directory() -> PathBuf {
    app_paths::cache_directory().join("update")
}

/// The script's output, next to app.log.
pub fn log_path() -> PathBuf {
    app_log::file_path().parent().unwrap().join("update.log")
}

/// The release workflow's runtime identifier for this machine, or None where there are no releases.
pub fn runtime_id() -> Option<String> {
    let arch = match std::env::consts::ARCH {
        "x86_64" => "x64",
        "aarch64" => "arm64",
        _ => return None,
    };
    Some(format!("{}-{arch}", ReleasePackage::current()?.platform()))
}

/// The release asset for this machine, named like the release workflow names it.
pub fn asset_name(version: Version) -> Option<String> {
    Some(ReleasePackage::current()?.asset_name(version, &runtime_id()?))
}

/// What an update replaces: the .app bundle this process runs from (macOS) or the app/ folder of the Linux bundle.
/// None for anything else, such as a development build.
pub fn installed_path(process_path: Option<&Path>) -> Option<PathBuf> {
    let p = match process_path {
        Some(p) => p.to_path_buf(),
        None => std::env::current_exe().ok()?,
    };
    ReleasePackage::current()?.installed_path(&p)
}

/// Why the copy at `installed` can't replace itself, or None when it can.
pub fn cannot_install_reason(installed: Option<&Path>) -> Option<String> {
    let Some(installed) = installed else {
        return Some("This copy doesn't run from a release package, so it can't update itself.".into());
    };
    if let Some(r) = ReleasePackage::current().and_then(|p| p.cannot_replace_reason(installed)) {
        return Some(r.into());
    }
    let parent = installed.parent()?;
    (!is_writable(parent)).then(|| format!("{} isn't writable, so the update can't be put in place.", parent.display()))
}

/// Removes leftovers of an update that was downloaded but never installed.
pub fn clean_up() {
    let dir = work_directory();
    if dir.exists()
        && let Err(e) = std::fs::remove_dir_all(&dir)
    {
        app_log::write(&format!("Update: couldn't remove {}: {e}", dir.display()));
    }
}

/// Downloads `asset`, checks it against GitHub's SHA-256 digest and unpacks it. `progress` gets the downloaded
/// fraction (0–1); returning false from `cancelled` stops it. Blocks.
pub fn prepare(
    agent: &ureq::Agent,
    asset: &ReleaseAsset,
    version: Version,
    progress: &mut dyn FnMut(f64),
    cancelled: &dyn Fn() -> bool,
) -> Result<PreparedUpdate, String> {
    let package = ReleasePackage::current().ok_or("There are no releases for this system.")?;
    clean_up();
    let work = work_directory();
    std::fs::create_dir_all(&work).map_err(|e| e.to_string())?;
    let file_name = Path::new(&asset.name).file_name().ok_or("Bad asset name.")?;
    let archive = work.join(file_name);
    download(agent, asset, &archive, progress, cancelled)?;
    let unpacked = work.join("new");
    std::fs::create_dir_all(&unpacked).map_err(|e| e.to_string())?;
    let out = package.unpack(&archive, &unpacked).stdin(Stdio::null()).output().map_err(|e| e.to_string())?;
    if !out.status.success() {
        return Err(format!("Couldn't unpack {}: {}", asset.name, String::from_utf8_lossy(&out.stderr).trim()));
    }
    let _ = std::fs::remove_file(&archive);
    let staged = unpacked.join(package.staged_name());
    if !package.executable_in(&staged).is_file() {
        return Err(format!("{} doesn't hold the app where it should.", asset.name));
    }
    app_log::write(&format!("Update: {version} is ready in {}", staged.display()));
    Ok(PreparedUpdate { version, staged })
}

fn download(
    agent: &ureq::Agent,
    asset: &ReleaseAsset,
    path: &Path,
    progress: &mut dyn FnMut(f64),
    cancelled: &dyn Fn() -> bool,
) -> Result<(), String> {
    let mut response = agent.get(&asset.url).config().timeout_global(None).build().call().map_err(|e| e.to_string())?;
    let total =
        response.headers().get("content-length").and_then(|v| v.to_str().ok()).and_then(|v| v.parse::<u64>().ok()).unwrap_or(asset.size);
    let mut reader = response.body_mut().with_config().limit(u64::MAX).reader();
    let mut file = std::fs::File::create(path).map_err(|e| e.to_string())?;
    let mut hash = Sha256::new();
    let mut buffer = vec![0u8; 128 * 1024];
    let mut done = 0u64;
    loop {
        if cancelled() {
            return Err("Cancelled".into());
        }
        let n = reader.read(&mut buffer).map_err(|e| e.to_string())?;
        if n == 0 {
            break;
        }
        file.write_all(&buffer[..n]).map_err(|e| e.to_string())?;
        hash.update(&buffer[..n]);
        done += n as u64;
        if total > 0 {
            progress((done as f64 / total as f64).min(1.0));
        }
    }
    let sha: String = hash.finalize().iter().map(|b| format!("{b:02x}")).collect();
    if let Some(expected) = &asset.sha256
        && !sha.eq_ignore_ascii_case(expected)
    {
        return Err(format!("The download of {} is damaged (its checksum doesn't match). Try again later.", asset.name));
    }
    Ok(())
}

/// What the apply script does.
#[derive(Clone, Debug)]
pub struct ApplyPlan {
    /// The kind of package `installed` and `staged` are.
    pub package: ReleasePackage,
    pub installed: PathBuf,
    pub staged: PathBuf,
    pub version: Version,
    pub wait_for: Vec<u32>,
    pub relaunch: bool,
    pub reopen: Vec<String>,
    /// Install with the new package's install.sh.
    pub run_installer: bool,
    pub log: PathBuf,
    /// A folder to delete at the end (the downloaded update).
    pub clean_up: Option<PathBuf>,
}

/// Starts the script that replaces `installed` with `update` once every process in `wait_for` has exited. With
/// `relaunch` it then starts the app again, opening `reopen` (project files). Returns right away: the caller quits
/// the app next.
pub fn apply(update: &PreparedUpdate, installed: &Path, wait_for: Vec<u32>, relaunch: bool, reopen: Vec<String>) -> Result<(), String> {
    let package = ReleasePackage::current().ok_or("There are no releases for this system.")?;
    let plan = ApplyPlan {
        package,
        installed: installed.to_path_buf(),
        staged: update.staged.clone(),
        version: update.version,
        wait_for,
        relaunch,
        reopen,
        run_installer: package.runs_installer(installed),
        log: log_path(),
        clean_up: Some(work_directory()),
    };
    let script = write_apply_script(&work_directory(), &plan).map_err(|e| e.to_string())?;
    app_log::write(&format!(
        "Update: installing {} after quitting (relaunch: {relaunch}); log in {}",
        update.version,
        log_path().display()
    ));
    Command::new("/bin/sh").arg(script).stdin(Stdio::null()).spawn().map_err(|e| format!("Couldn't start the update script: {e}"))?;
    Ok(())
}

fn quote(v: &str) -> String {
    format!("'{}'", v.replace('\'', "'\\''"))
}

pub fn write_apply_script(directory: &Path, plan: &ApplyPlan) -> std::io::Result<PathBuf> {
    std::fs::create_dir_all(directory)?;
    let installed_full = std::path::absolute(&plan.installed)?;
    let installed = installed_full.to_string_lossy().trim_end_matches('/').to_string();
    let installed_path = Path::new(&installed);
    let name = installed_path.file_name().unwrap().to_string_lossy().into_owned();
    let sibling = installed_path.parent().unwrap();
    // Next to the installed copy, so moving it in place is a rename on the same disk.
    let incoming = sibling.join(format!(".{name}.new"));
    let backup = sibling.join(format!(".{name}.old"));
    let reopen = plan.reopen.iter().map(|r| quote(r)).collect::<Vec<_>>().join(" ");
    let install = if plan.run_installer {
        r#"  rm -rf "$backup"
  mv "$installed" "$backup" || { echo "Couldn't move the old version aside."; return 1; }
  if ! "$staged/install.sh"; then
    echo "install.sh failed; restoring the old version."
    rm -rf "$installed"
    mv "$backup" "$installed"
    return 1
  fi
  rm -rf "$backup""#
            .to_string()
    } else {
        format!(
            r#"  rm -rf "$incoming" "$backup"
  if ! {}; then
    echo "Couldn't copy the new version next to the old one."
    rm -rf "$incoming"
    return 1
  fi
  mv "$installed" "$backup" || {{ echo "Couldn't move the old version aside."; rm -rf "$incoming"; return 1; }}
  if ! mv "$incoming" "$installed"; then
    echo "Couldn't put the new version in place; restoring the old one."
    mv "$backup" "$installed"
    return 1
  fi
  rm -rf "$backup""#,
            plan.package.copy_in_script()
        )
    };
    let relaunch = if plan.relaunch { plan.package.relaunch_script(&reopen) } else { String::new() };
    let clean = plan.clean_up.as_ref().map(|c| format!("rm -rf {}", quote(&c.to_string_lossy()))).unwrap_or_default();
    let waits = plan.wait_for.iter().map(|p| p.to_string()).collect::<Vec<_>>().join(" ");
    let v = plan.version;
    let text = format!(
        r#"#!/bin/sh
# Written by Mane FFmpeg: installs {v} once the app has quit.
mkdir -p {log_dir}
exec >>{log} 2>&1
installed={installed_q}
staged={staged}
incoming={incoming}
backup={backup}
echo "$(date '+%Y-%m-%d %H:%M:%S') Installing {v} into $installed"

for pid in {waits}; do
  steps=0
  while kill -0 "$pid" 2>/dev/null; do
    steps=$((steps + 1))
    if [ "$steps" -gt {QUIT_WAIT_STEPS} ]; then
      echo "The app (pid $pid) is still running; not updating."
      exit 1
    fi
    sleep 0.1
  done
done

install_update() {{
{install}
  {after}
}}

if install_update; then
  echo "Installed."
else
  echo "The update failed; the old version stays."
fi
{clean}
{relaunch}
"#,
        log_dir = quote(&plan.log.parent().unwrap().to_string_lossy()),
        log = quote(&plan.log.to_string_lossy()),
        installed_q = quote(&installed),
        staged = quote(&plan.staged.to_string_lossy()),
        incoming = quote(&incoming.to_string_lossy()),
        backup = quote(&backup.to_string_lossy()),
        after = plan.package.after_install_script(),
    );
    let script = directory.join("apply-update.sh");
    std::fs::write(&script, text)?;
    use std::os::unix::fs::PermissionsExt;
    std::fs::set_permissions(&script, std::fs::Permissions::from_mode(0o700))?;
    Ok(script)
}

/// The process that started this one (the macOS relaunch keeps it waiting until this one quits).
pub fn parent_process_id() -> u32 {
    // SAFETY: getppid can't fail.
    unsafe { libc::getppid() as u32 }
}

fn is_writable(dir: &Path) -> bool {
    let probe = dir.join(format!(".mane-ffmpeg-write-test-{}", crate::model::ids::new()));
    match std::fs::write(&probe, []) {
        Ok(()) => {
            let _ = std::fs::remove_file(probe);
            true
        }
        Err(_) => false,
    }
}
