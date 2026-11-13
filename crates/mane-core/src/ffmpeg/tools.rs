use crate::executables;
use crate::version::Version;
use std::path::Path;
use std::process::{Command, Stdio};

/// Paths of the ffmpeg tools this app drives, plus the detected version.
#[derive(Clone, PartialEq, Debug)]
pub struct FfmpegTools {
    pub ffmpeg: String,
    pub ffprobe: String,
    pub version_text: String,
    pub version: Option<Version>,
}

/// Oldest ffmpeg the generated commands are written for.
pub const MINIMUM: Version = Version::new(5, 0, 0);

/// Leading ffmpeg options for every run: no stdin, no banner, errors only.
pub const QUIET: [&str; 4] = ["-nostdin", "-hide_banner", "-loglevel", "error"];

impl FfmpegTools {
    pub fn is_too_old(&self) -> bool {
        self.version.is_some_and(|v| v < MINIMUM)
    }

    /// Finds ffmpeg and ffprobe (ffprobe defaults to ffmpeg's directory) and reads the version.
    pub fn locate(ffmpeg_override: Option<&str>, ffprobe_override: Option<&str>) -> Option<FfmpegTools> {
        let ffmpeg = executables::find("ffmpeg", ffmpeg_override)?;
        let sibling = Path::new(&ffmpeg).parent()?.join("ffprobe");
        let ffprobe = if ffprobe_override.is_some_and(|p| !p.trim().is_empty()) {
            executables::find("ffprobe", ffprobe_override)
        } else if sibling.is_file() {
            Some(sibling.to_string_lossy().into_owned())
        } else {
            executables::find("ffprobe", None)
        }?;
        let out = Command::new(&ffmpeg).args(["-hide_banner", "-version"]).stdin(Stdio::null()).output().ok()?;
        let text = String::from_utf8_lossy(&out.stdout);
        let first = text.split('\n').next().unwrap_or("").trim().to_string();
        let version = parse_version(&first);
        Some(FfmpegTools { ffmpeg, ffprobe, version_text: first, version })
    }
}

/// "ffmpeg version 8.1.2 Copyright…" → 8.1.2; git builds ("N-1234-g…") → None.
pub fn parse_version(line: &str) -> Option<Version> {
    let re = regex::Regex::new(r"version\s+n?(\d+)\.(\d+)(?:\.(\d+))?").unwrap();
    let c = re.captures(line)?;
    let n = |i: usize| c.get(i).map(|m| m.as_str().parse::<u32>().unwrap_or(0)).unwrap_or(0);
    Some(Version::new(n(1), n(2), n(3)))
}
