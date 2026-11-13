//! Pure edits of a project's files together with the clips that use them. Each keeps the state whole: every clip's
//! video is in the sources, and every replaced sound's file is in the audio files.

use super::timeline_editor as te;
use crate::model::{Clip, FileKind, MediaInfo, ProjectState, SourceMedia, Time, Timeline};
use std::collections::HashSet;
use std::path::Path;

/// A file to add with its probe result, or why probing failed.
#[derive(Clone, Debug)]
pub struct ProbedFile {
    pub path: String,
    pub info: Option<MediaInfo>,
    pub error: Option<String>,
}

/// Where added files go.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum AddRouting {
    /// Videos are listed and appended as whole clips; files with only sound go to the audio files.
    ByContent,
    /// Every file with sound goes to the audio files, videos too (their sound can replace a clip's).
    AllAsAudio,
}

/// The state after adding files, with the ids of the videos and audio files added and what was skipped.
#[derive(Clone, Debug)]
pub struct AddedFiles {
    pub state: ProjectState,
    pub videos: Vec<String>,
    pub audio_files: Vec<String>,
    pub skipped: Vec<String>,
}

/// The state with `file` listed, and its id; a path already listed keeps its entry (and id).
pub fn add_file(state: &ProjectState, kind: FileKind, file: SourceMedia) -> (ProjectState, String) {
    let files = state.files(kind);
    if let Some(existing) = files.iter().find(|f| f.path == file.path) {
        return (state.clone(), existing.id.clone());
    }
    let id = file.id.clone();
    let mut list = files.to_vec();
    list.push(file);
    (state.with_files(kind, list), id)
}

/// Appends a clip of video `source_id`; unchanged when that video isn't listed.
pub fn append_clip(state: &ProjectState, source_id: &str, r#in: Time, out: Time) -> ProjectState {
    if state.source(source_id).is_none() {
        return state.clone();
    }
    state.with_timeline(te::append(&state.timeline, Clip::create(source_id, r#in, out)))
}

/// How many clips use the file: clips of a video, or clips whose sound an audio file replaces.
pub fn clips_using(state: &ProjectState, kind: FileKind, id: &str) -> usize {
    state
        .timeline
        .clips()
        .iter()
        .filter(|c| if kind == FileKind::Audio { c.replaced_audio.as_ref().is_some_and(|r| r.file_id == id) } else { c.source_id == id })
        .count()
}

/// Removes a file with what depends on it: a video's clips go too, and clips playing an audio file go back to their
/// own sound.
pub fn remove_file(state: &ProjectState, kind: FileKind, id: &str) -> ProjectState {
    let timeline =
        if kind == FileKind::Audio { te::remove_audio_file(&state.timeline, id) } else { te::remove_source(&state.timeline, id) };
    let files = state.files(kind).iter().filter(|f| f.id != id).cloned().collect();
    state.with_files(kind, files).with_timeline(timeline)
}

/// Plays audio file `file_id` instead of the clip's sound. Returns an equal state when the clip or the audio file
/// isn't there.
pub fn replace_clip_audio(state: &ProjectState, clip_id: &str, file_id: &str) -> ProjectState {
    if state.timeline.index_of(clip_id).is_none() || state.audio_file(file_id).is_none() {
        return state.clone();
    }
    state.with_timeline(te::replace_audio(&state.timeline, clip_id, file_id))
}

/// Drops what refers to files that aren't listed (a damaged or hand-edited project): clips of unknown videos go, and
/// clips whose replacement audio file is unknown play their own sound.
pub fn drop_orphans(state: &ProjectState) -> ProjectState {
    let videos: HashSet<&str> = state.sources.iter().map(|s| s.id.as_str()).collect();
    let audio: HashSet<&str> = state.audio_files.iter().map(|s| s.id.as_str()).collect();
    let clips = state
        .timeline
        .clips()
        .iter()
        .filter(|c| videos.contains(c.source_id.as_str()))
        .map(|c| match &c.replaced_audio {
            Some(r) if !audio.contains(r.file_id.as_str()) => Clip { replaced_audio: None, ..c.clone() },
            _ => c.clone(),
        })
        .collect();
    state.with_timeline(Timeline::new(clips))
}

/// Adds probed files as `routing` says, each path once (a listed path keeps its entry). `new_file` makes the entry
/// for a path that isn't listed yet.
pub fn add_probed(state: &ProjectState, files: &[ProbedFile], routing: AddRouting, new_file: &dyn Fn(&str) -> SourceMedia) -> AddedFiles {
    let mut state = state.clone();
    let (mut videos, mut audio, mut skipped) = (Vec::new(), Vec::new(), Vec::new());
    for f in files {
        let has_sound = f.info.as_ref().is_some_and(|i| !i.audio_streams.is_empty());
        let to_audio =
            if routing == AddRouting::AllAsAudio { has_sound } else { f.info.as_ref().is_some_and(|i| i.video.is_none()) && has_sound };
        if to_audio {
            let (s, id) = add_file(&state, FileKind::Audio, new_file(&f.path));
            state = s;
            audio.push(id);
        } else if routing == AddRouting::ByContent && f.info.as_ref().is_some_and(|i| i.video.is_some()) {
            let (s, id) = add_file(&state, FileKind::Video, new_file(&f.path));
            state = append_clip(&s, &id, Time::ZERO, f.info.as_ref().unwrap().duration);
            videos.push(id);
        } else {
            let missing = if routing == AddRouting::AllAsAudio { "no audio stream" } else { "no video or audio stream" };
            let name = Path::new(&f.path).file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
            skipped.push(format!("{name}: {}", f.error.as_deref().unwrap_or(missing)));
        }
    }
    AddedFiles { state, videos, audio_files: audio, skipped }
}
