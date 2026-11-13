//! The analyzers shared by every project: probing (cached per file version), waveforms and keyframes.

use super::keyframes::KeyframeService;
use super::waveform::WaveformService;
use crate::ffmpeg::{FfmpegTools, ffprobe};
use crate::model::{MediaInfo, UtcTime};
use std::collections::HashMap;
use std::sync::{Arc, Mutex, RwLock};

pub trait MediaAnalyzers: Send + Sync {
    /// Probes a file; implementations cache per file version. Blocks.
    fn probe(&self, path: &str) -> Result<MediaInfo, String>;
    /// None while ffmpeg isn't available.
    fn waveforms(&self) -> Option<Arc<WaveformService>>;
    /// None while ffmpeg isn't available.
    fn keyframes(&self) -> Option<Arc<KeyframeService>>;
}

#[derive(Default)]
struct Services {
    tools: Option<FfmpegTools>,
    tools_error: Option<String>,
    waveforms: Option<Arc<WaveformService>>,
    keyframes: Option<Arc<KeyframeService>>,
}

/// The app's analyzers, over the located ffmpeg tools.
/// A probe of one file version: (size, modification time, the result once known).
type ProbeEntry = (i64, UtcTime, Arc<std::sync::OnceLock<Result<MediaInfo, String>>>);

#[derive(Default)]
pub struct Analyzers {
    services: RwLock<Services>,
    probes: Mutex<HashMap<String, ProbeEntry>>,
}

impl Analyzers {
    pub fn tools(&self) -> Option<FfmpegTools> {
        self.services.read().unwrap().tools.clone()
    }

    pub fn tools_error(&self) -> Option<String> {
        self.services.read().unwrap().tools_error.clone()
    }

    /// Takes newly located tools; the same tools keep what they've already analyzed. True when they changed.
    pub fn set_tools(&self, tools: Option<FfmpegTools>) -> bool {
        let mut s = self.services.write().unwrap();
        s.tools_error = match &tools {
            None if cfg!(target_os = "macos") => {
                Some("ffmpeg wasn't found. Install it (brew install ffmpeg) or set its path in Settings.".into())
            }
            None => Some("ffmpeg wasn't found. Install it with your package manager or set its path in Settings.".into()),
            Some(t) if t.is_too_old() => Some(format!("{} is older than 5.0; some features may fail.", t.version_text)),
            Some(_) => None,
        };
        if s.tools == tools {
            return false;
        }
        s.waveforms = tools.clone().map(|t| Arc::new(WaveformService::new(t)));
        s.keyframes = tools.clone().map(|t| Arc::new(KeyframeService::new(t)));
        s.tools = tools;
        self.probes.lock().unwrap().clear();
        true
    }
}

impl Analyzers {
    /// The keyframe scanner of the current tools.
    pub fn keyframes_service(&self) -> Option<Arc<KeyframeService>> {
        self.services.read().unwrap().keyframes.clone()
    }
}

impl MediaAnalyzers for Analyzers {
    /// Probes a file once per (size, modification time).
    fn probe(&self, path: &str) -> Result<MediaInfo, String> {
        let Some(tools) = self.tools() else {
            return Err(self.tools_error().unwrap_or_else(|| "ffprobe isn't available.".into()));
        };
        let meta = std::fs::metadata(path).map_err(|_| "File not found.".to_string())?;
        let size = meta.len() as i64;
        let write = meta.modified().map(UtcTime::from_system).unwrap_or_default();
        let cell = {
            let mut probes = self.probes.lock().unwrap();
            match probes.get(path) {
                Some((s, w, cell)) if *s == size && *w == write && !matches!(cell.get(), Some(Err(_))) => cell.clone(),
                _ => {
                    let cell = Arc::new(std::sync::OnceLock::new());
                    probes.insert(path.to_string(), (size, write, cell.clone()));
                    cell
                }
            }
        };
        cell.get_or_init(|| ffprobe::probe(&tools, path).map_err(|e| e.0)).clone()
    }

    fn waveforms(&self) -> Option<Arc<WaveformService>> {
        self.services.read().unwrap().waveforms.clone()
    }

    fn keyframes(&self) -> Option<Arc<KeyframeService>> {
        self.services.read().unwrap().keyframes.clone()
    }
}
