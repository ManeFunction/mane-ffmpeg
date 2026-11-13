//! Waveforms built with ffmpeg (mono, 8 kHz) and cached in memory and on disk.

use super::once_cache::OnceCache;
use crate::ffmpeg::FfmpegTools;
use crate::ffmpeg::tools::QUIET;
use crate::model::{MediaInfo, Time, UtcTime};
use crate::persistence::app_paths;
use sha1::{Digest, Sha1};
use std::io::Read;
use std::path::PathBuf;
use std::process::{Command, Stdio};
use std::sync::Arc;

/// Peak levels (0–255) of an audio stream, one per 1/`BUCKETS_PER_SECOND` of a second.
#[derive(Clone, PartialEq, Debug, Default)]
pub struct Waveform {
    pub peaks: Vec<u8>,
}

pub const BUCKETS_PER_SECOND: i64 = 100;

impl Waveform {
    pub fn duration(&self) -> Time {
        Time::from_seconds(self.peaks.len() as f64 / BUCKETS_PER_SECOND as f64)
    }

    /// Highest peak in [from, to), or 0 outside the stream.
    pub fn max(&self, from: Time, to: Time) -> u8 {
        let a = ((from.total_seconds() * BUCKETS_PER_SECOND as f64).floor() as i64).max(0);
        let b = ((to.total_seconds() * BUCKETS_PER_SECOND as f64).ceil() as i64).max(a + 1).min(self.peaks.len() as i64);
        if a >= b {
            return 0;
        }
        *self.peaks[a as usize..b as usize].iter().max().unwrap_or(&0)
    }
}

const SAMPLE_RATE: usize = 8000;
const SAMPLES_PER_BUCKET: usize = SAMPLE_RATE / BUCKETS_PER_SECOND as usize;
const MAGIC: &[u8] = b"MFWF1";

pub struct WaveformService {
    tools: FfmpegTools,
    cache: OnceCache<String, Option<Arc<Waveform>>>,
}

impl WaveformService {
    pub fn new(tools: FfmpegTools) -> WaveformService {
        WaveformService { tools, cache: OnceCache::default() }
    }

    // A file replaced in place gets a new stamp when probed again, so its waveform is read again.
    fn key(path: &str, media: &MediaInfo, stream: i32) -> String {
        format!("{path}\n{}\n{stream}", media.stamp)
    }

    pub fn try_get(&self, path: &str, media: &MediaInfo, stream: i32) -> Option<Arc<Waveform>> {
        self.cache.try_get(&Self::key(path, media, stream)).flatten()
    }

    /// The waveform of stream `stream` (absolute index) of `path`, or None if it has none. Blocks.
    pub fn get(&self, path: &str, media: &MediaInfo, stream: i32) -> Option<Arc<Waveform>> {
        self.cache.get(&Self::key(path, media, stream), || self.load_or_build(path, stream).map(Arc::new))
    }

    fn load_or_build(&self, path: &str, stream: i32) -> Option<Waveform> {
        let cache_file = cache_file(path, stream);
        if let Some(w) = cache_file.as_ref().and_then(read_cache) {
            return Some(w);
        }
        let mut args: Vec<String> = QUIET.iter().map(|s| s.to_string()).collect();
        args.extend(
            [
                "-i",
                path,
                "-map",
                &format!("0:{stream}"),
                "-ac",
                "1",
                "-ar",
                &SAMPLE_RATE.to_string(),
                "-f",
                "s16le",
                "-acodec",
                "pcm_s16le",
                "pipe:1",
            ]
            .map(String::from),
        );
        let mut child =
            Command::new(&self.tools.ffmpeg).args(&args).stdin(Stdio::null()).stdout(Stdio::piped()).stderr(Stdio::null()).spawn().ok()?;
        let mut out = child.stdout.take().unwrap();
        let mut peaks = Vec::new();
        let bucket_bytes = SAMPLES_PER_BUCKET * 2;
        let mut buffer = vec![0u8; bucket_bytes * 256];
        let mut carry = 0;
        loop {
            let read = match out.read(&mut buffer[carry..]) {
                Ok(0) | Err(_) => break,
                Ok(n) => n,
            };
            let available = carry + read;
            let whole = available / bucket_bytes * bucket_bytes;
            for offset in (0..whole).step_by(bucket_bytes) {
                peaks.push(peak(&buffer[offset..offset + bucket_bytes]));
            }
            carry = available - whole;
            buffer.copy_within(whole..available, 0);
        }
        if carry >= 2 {
            peaks.push(peak(&buffer[..carry & !1]));
        }
        let ok = child.wait().map(|s| s.success()).unwrap_or(false);
        if !ok && peaks.is_empty() {
            return None;
        }
        let w = Waveform { peaks };
        if let Some(f) = cache_file {
            write_cache(&f, &w);
        }
        Some(w)
    }
}

fn peak(pcm: &[u8]) -> u8 {
    let mut max = 0i32;
    for s in pcm.as_chunks::<2>().0 {
        let sample = (i16::from_le_bytes([s[0], s[1]]) as i32).abs();
        max = max.max(sample);
    }
    // Square-root scale so quiet speech is still visible next to loud peaks.
    ((max as f64 / 32768.0).sqrt() * 255.0).clamp(0.0, 255.0) as u8
}

fn cache_file(path: &str, stream: i32) -> Option<PathBuf> {
    let meta = std::fs::metadata(path).ok()?;
    if !meta.is_file() {
        return None;
    }
    let full = std::path::absolute(path).ok()?;
    let write = meta.modified().map(UtcTime::from_system).unwrap_or_default();
    let mut hasher = Sha1::new();
    hasher.update(format!("{}|{}|{}|{stream}", full.to_string_lossy(), meta.len(), write.0).as_bytes());
    let hash: String = hasher.finalize().iter().map(|b| format!("{b:02X}")).collect();
    let dir = app_paths::cache_directory().join("waveforms");
    std::fs::create_dir_all(&dir).ok()?;
    Some(dir.join(format!("{hash}.bin")))
}

fn read_cache(file: &PathBuf) -> Option<Waveform> {
    let bytes = std::fs::read(file).ok()?;
    bytes.starts_with(MAGIC).then(|| Waveform { peaks: bytes[MAGIC.len()..].to_vec() })
}

fn write_cache(file: &PathBuf, w: &Waveform) {
    // A missing cache only costs time.
    let mut data = MAGIC.to_vec();
    data.extend_from_slice(&w.peaks);
    let _ = std::fs::write(file, data);
}
