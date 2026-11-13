//! What a project knows about each of its sources, by source id: whether it's ready, its probe result, the chosen
//! audio stream, its waveform and (on demand) its keyframes. It analyzes again by itself when a source's path or
//! audio stream changes and when the tools change, and hands the preview and the export a snapshot of the probe
//! results. Work runs on background threads; their results are applied by [`SourceCatalog::poll`] on the thread
//! that drives the catalog (the UI), which also learns which sources changed.

use super::analyzers::MediaAnalyzers;
use super::keyframes::KeyframeIndex;
use super::waveform::Waveform;
use crate::model::{AudioStreamInfo, FileKind, MediaInfo, SourceMedia};
use std::collections::{HashMap, HashSet};
use std::path::Path;
use std::sync::Arc;
use std::sync::mpsc::{Receiver, Sender, channel};

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum SourceStatus {
    Analyzing,
    Ready,
    Missing,
    Failed,
}

struct Entry {
    source: SourceMedia,
    kind: FileKind,
    status: SourceStatus,
    info: Option<MediaInfo>,
    error: Option<String>,
    /// Bumped whenever the entry is analyzed again, so late results of an earlier run are dropped.
    generation: u64,
}

enum Msg {
    Probed { id: String, generation: u64, result: Result<MediaInfo, String> },
    Analyzed { id: String, generation: u64 },
}

pub type Waker = Arc<dyn Fn() + Send + Sync>;

pub struct SourceCatalog {
    analyzers: Arc<dyn MediaAnalyzers>,
    entries: HashMap<String, Entry>,
    want_keyframes: bool,
    tx: Sender<Msg>,
    rx: Receiver<Msg>,
    waker: Waker,
    /// Ids changed since the last poll by work done on this thread.
    pending_changes: Vec<String>,
}

impl SourceCatalog {
    pub fn new(analyzers: Arc<dyn MediaAnalyzers>, waker: Waker) -> SourceCatalog {
        let (tx, rx) = channel();
        SourceCatalog { analyzers, entries: HashMap::new(), want_keyframes: false, tx, rx, waker, pending_changes: Vec::new() }
    }

    pub fn analyzers(&self) -> &Arc<dyn MediaAnalyzers> {
        &self.analyzers
    }

    /// Applies finished work; returns the ids of sources whose status, probe result, waveform or keyframes changed.
    pub fn poll(&mut self) -> Vec<String> {
        let mut changed = std::mem::take(&mut self.pending_changes);
        while let Ok(msg) = self.rx.try_recv() {
            match msg {
                Msg::Probed { id, generation, result } => {
                    if !self.is_current(&id, generation) {
                        continue;
                    }
                    match result {
                        Ok(info) => {
                            let entry = self.entries.get(&id).unwrap();
                            if let Some(missing) = entry.kind.missing_stream(&info) {
                                self.update(&id, SourceStatus::Failed, Some(info), Some(missing.into()));
                            } else {
                                self.update(&id, SourceStatus::Ready, Some(info), None);
                                self.load_analysis(&id, generation);
                            }
                        }
                        Err(e) => self.update(&id, SourceStatus::Failed, None, Some(e)),
                    }
                    changed.append(&mut self.pending_changes);
                }
                Msg::Analyzed { id, generation } => {
                    if self.is_current(&id, generation) {
                        changed.push(id);
                    }
                }
            }
        }
        changed
    }

    /// Scan keyframes of every ready source (snapping to them, or stream copy).
    pub fn want_keyframes(&self) -> bool {
        self.want_keyframes
    }

    pub fn set_want_keyframes(&mut self, value: bool) {
        if self.want_keyframes == value {
            return;
        }
        self.want_keyframes = value;
        if value {
            let ready: Vec<(String, u64)> = self
                .entries
                .iter()
                .filter(|(_, e)| e.status == SourceStatus::Ready && e.kind.has_keyframes())
                .map(|(id, e)| (id.clone(), e.generation))
                .collect();
            for (id, g) in ready {
                self.load_analysis(&id, g);
            }
        }
    }

    /// Probe results of the ready sources and audio files, by id.
    pub fn media(&self) -> HashMap<String, MediaInfo> {
        self.entries.iter().filter_map(|(id, e)| e.info.clone().map(|i| (id.clone(), i))).collect()
    }

    pub fn status(&self, id: &str) -> SourceStatus {
        self.entries.get(id).map(|e| e.status).unwrap_or(SourceStatus::Missing)
    }

    pub fn error(&self, id: &str) -> Option<&str> {
        self.entries.get(id)?.error.as_deref()
    }

    pub fn info(&self, id: &str) -> Option<&MediaInfo> {
        self.entries.get(id)?.info.as_ref()
    }

    /// The source's chosen audio stream, or None when it has none (or isn't analyzed yet).
    pub fn audio(&self, source: &SourceMedia) -> Option<&AudioStreamInfo> {
        self.info(&source.id)?.audio(source.audio_stream_index)
    }

    pub fn waveform(&self, source: &SourceMedia) -> Option<Arc<Waveform>> {
        let info = self.info(&source.id)?;
        let audio = info.audio(source.audio_stream_index)?;
        self.analyzers.waveforms()?.try_get(&source.path, info, audio.index)
    }

    pub fn keyframes(&self, source: &SourceMedia) -> Option<Arc<KeyframeIndex>> {
        let info = self.info(&source.id)?;
        self.analyzers.keyframes()?.try_get(&source.path, info)
    }

    /// Brings the catalog in line with the project's sources (after edits, undo or loading): new sources are
    /// analyzed, removed ones forgotten, a moved file is probed again and a new audio stream gets its waveform.
    pub fn sync(&mut self, sources: &[SourceMedia], audio_files: &[SourceMedia]) {
        let current: Vec<(SourceMedia, FileKind)> =
            sources.iter().map(|s| (s.clone(), FileKind::Video)).chain(audio_files.iter().map(|s| (s.clone(), FileKind::Audio))).collect();
        let ids: HashSet<&str> = current.iter().map(|(s, _)| s.id.as_str()).collect();
        self.entries.retain(|id, _| ids.contains(id.as_str()));
        for (source, kind) in current {
            match self.entries.get_mut(&source.id) {
                None => {
                    let id = source.id.clone();
                    self.entries.insert(
                        id.clone(),
                        Entry { source, kind, status: SourceStatus::Analyzing, info: None, error: None, generation: 0 },
                    );
                    self.probe(&id);
                }
                Some(entry) if entry.source != source => {
                    let moved = entry.source.path != source.path;
                    let other_audio = entry.source.audio_stream_index != source.audio_stream_index;
                    let id = source.id.clone();
                    entry.source = source;
                    if moved {
                        self.probe(&id);
                    } else if other_audio && entry.status == SourceStatus::Ready {
                        let g = entry.generation;
                        self.load_analysis(&id, g);
                    }
                }
                _ => {}
            }
        }
    }

    /// The tools changed: sources that failed get another try, ready ones reload their analyses.
    pub fn reanalyze(&mut self) {
        let all: Vec<(String, SourceStatus, u64)> = self.entries.iter().map(|(id, e)| (id.clone(), e.status, e.generation)).collect();
        for (id, status, g) in all {
            if status != SourceStatus::Ready {
                self.probe(&id);
            } else {
                self.load_analysis(&id, g);
            }
        }
    }

    fn probe(&mut self, id: &str) {
        let entry = self.entries.get_mut(id).unwrap();
        entry.generation += 1;
        let generation = entry.generation;
        let path = entry.source.path.clone();
        if !Path::new(&path).is_file() {
            self.update(id, SourceStatus::Missing, None, None);
            return;
        }
        self.update(id, SourceStatus::Analyzing, None, None);
        let (analyzers, tx, waker, id) = (self.analyzers.clone(), self.tx.clone(), self.waker.clone(), id.to_string());
        std::thread::spawn(move || {
            let result = analyzers.probe(&path);
            let _ = tx.send(Msg::Probed { id, generation, result });
            waker();
        });
    }

    /// The waveform of the chosen audio stream (always) and the keyframes (when wanted), in the background.
    fn load_analysis(&mut self, id: &str, generation: u64) {
        let Some(entry) = self.entries.get(id) else { return };
        let Some(info) = entry.info.clone() else { return };
        let path = entry.source.path.clone();
        if let (Some(audio), Some(waveforms)) = (info.audio(entry.source.audio_stream_index), self.analyzers.waveforms()) {
            let (stream, info, path, tx, waker, id) =
                (audio.index, info.clone(), path.clone(), self.tx.clone(), self.waker.clone(), id.to_string());
            std::thread::spawn(move || {
                // Waveforms are an optional helper.
                if waveforms.get(&path, &info, stream).is_some() {
                    let _ = tx.send(Msg::Analyzed { id, generation });
                    waker();
                }
            });
        }
        if self.want_keyframes
            && entry.kind.has_keyframes()
            && let Some(keyframes) = self.analyzers.keyframes()
        {
            let (tx, waker, id) = (self.tx.clone(), self.waker.clone(), id.to_string());
            std::thread::spawn(move || {
                if keyframes.get(&path, &info).is_ok() {
                    let _ = tx.send(Msg::Analyzed { id, generation });
                    waker();
                }
            });
        }
    }

    fn update(&mut self, id: &str, status: SourceStatus, info: Option<MediaInfo>, error: Option<String>) {
        let Some(entry) = self.entries.get_mut(id) else { return };
        entry.status = status;
        entry.info = info;
        entry.error = error;
        self.pending_changes.push(id.to_string());
    }

    fn is_current(&self, id: &str, generation: u64) -> bool {
        self.entries.get(id).is_some_and(|e| e.generation == generation)
    }
}
