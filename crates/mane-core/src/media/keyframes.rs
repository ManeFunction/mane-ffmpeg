//! Keyframe (seek point) times of a file's video stream, from ffprobe packet flags. Cached per file version.

use super::once_cache::OnceCache;
use crate::ffmpeg::FfmpegTools;
use crate::model::time::parse_float;
use crate::model::{MediaInfo, Time};
use std::io::{BufRead, BufReader};
use std::process::{Command, Stdio};
use std::sync::Arc;

/// Keyframe times (0-based) of a video stream, plus its reorder delay: how much earlier than its display time a
/// frame is decoded (B-frames). Stream-copy cuts need both.
#[derive(Clone, PartialEq, Debug, Default)]
pub struct KeyframeIndex {
    pub times: Vec<Time>,
    pub reorder_delay: Time,
}

pub struct KeyframeService {
    tools: FfmpegTools,
    cache: OnceCache<String, Result<Arc<KeyframeIndex>, String>>,
}

impl KeyframeService {
    pub fn new(tools: FfmpegTools) -> KeyframeService {
        KeyframeService { tools, cache: OnceCache::default() }
    }

    // A file replaced in place gets a new stamp when probed again, so it is scanned again.
    fn key(path: &str, media: &MediaInfo) -> String {
        format!("{path}\n{}", media.stamp)
    }

    /// The keyframes of `path` as probed in `media`, if already scanned.
    pub fn try_get(&self, path: &str, media: &MediaInfo) -> Option<Arc<KeyframeIndex>> {
        self.cache.try_get(&Self::key(path, media)).and_then(Result::ok)
    }

    /// Scans (once) and returns the keyframes; blocks.
    pub fn get(&self, path: &str, media: &MediaInfo) -> Result<Arc<KeyframeIndex>, String> {
        let key = Self::key(path, media);
        let result = self.cache.get(&key, || self.scan(path, media).map(Arc::new));
        if result.is_err() {
            self.cache.remove(&key);
        }
        result
    }

    fn scan(&self, path: &str, media: &MediaInfo) -> Result<KeyframeIndex, String> {
        let video = media.video.as_ref().ok_or("No video stream.")?;
        let mut child = Command::new(&self.tools.ffprobe)
            .args([
                "-v",
                "error",
                "-select_streams",
                &video.index.to_string(),
                "-show_entries",
                "packet=pts_time,dts_time,flags",
                "-of",
                "csv=p=0",
                path,
            ])
            .stdin(Stdio::null())
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .spawn()
            .map_err(|e| e.to_string())?;
        let mut result = Vec::new();
        let mut delay: Option<Time> = None;
        for line in BufReader::new(child.stdout.take().unwrap()).lines().map_while(Result::ok) {
            // "pts_time,dts_time,flags"
            let parts: Vec<&str> = line.split(',').collect();
            if parts.len() < 3 || !parts[2].contains('K') {
                continue;
            }
            let Some(pts) = parse_float(parts[0]) else { continue };
            result.push(Time::from_seconds(pts) - media.start_time);
            if let Some(dts) = parse_float(parts[1]) {
                let d = Time::from_seconds(pts - dts);
                if d >= Time::ZERO && delay.is_none_or(|x| d < x) {
                    delay = Some(d);
                }
            }
        }
        let _ = child.wait();
        result.sort();
        if result.is_empty() {
            result.push(Time::ZERO);
        }
        Ok(KeyframeIndex { times: result, reorder_delay: delay.unwrap_or(Time::ZERO) })
    }
}
