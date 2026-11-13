use super::audio::HeardAudio;
use super::crop::CropRect;
use super::datetime::UtcTime;
use super::file_kind::FileKind;
use super::ids;
use super::media_info::MediaInfo;
use super::time::Time;
use std::path::Path;

/// A loaded file. Probe results live in a separate catalog so edits stay cheap to snapshot.
#[derive(Clone, PartialEq, Debug, Default)]
pub struct SourceMedia {
    pub id: String,
    pub path: String,
    pub file_size: i64,
    pub last_write_utc: UtcTime,
    /// Which audio stream to use, as an index among the file's audio streams ("a:N").
    pub audio_stream_index: i32,
}

impl SourceMedia {
    pub fn display_name(&self) -> String {
        Path::new(&self.path).file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_else(|| self.path.clone())
    }
}

/// An audio file that replaces a clip's sound. `anchor` is the clip's video source time where the file starts (its
/// in point when dropped), so splits continue the sound and trims don't move it against the picture; `offset` slips
/// it like a clip's own audio offset.
#[derive(Clone, PartialEq, Debug, Default)]
pub struct AudioReplacement {
    /// An id in [`ProjectState::audio_files`].
    pub file_id: String,
    pub anchor: Time,
    pub offset: Time,
    pub looped: bool,
}

/// One piece of a source on the timeline. `audio_offset` slips the clip's audio: positive means the audio is heard
/// later, so it is read from `[in − offset, out − offset]`.
#[derive(Clone, PartialEq, Debug, Default)]
pub struct Clip {
    pub id: String,
    pub source_id: String,
    pub r#in: Time,
    pub out: Time,
    pub audio_offset: Time,
    /// An audio file played instead of the clip's own audio; None plays its own.
    pub replaced_audio: Option<AudioReplacement>,
}

impl Clip {
    /// A new clip of `source_id` from `in` to `out`, with a fresh id.
    pub fn create(source_id: &str, r#in: Time, out: Time) -> Clip {
        Clip { id: ids::new(), source_id: source_id.to_string(), r#in, out, ..Default::default() }
    }

    pub fn duration(&self) -> Time {
        self.out - self.r#in
    }

    /// How far the heard file is slipped against the clip's video source time: file time = video time − slip.
    /// For replaced audio that is where it was dropped plus its offset.
    pub fn audio_slip(&self) -> Time {
        match &self.replaced_audio {
            Some(r) => r.anchor + r.offset,
            None => self.audio_offset,
        }
    }

    /// The offset the user edits: the replacement's when the audio is replaced, else the clip's own.
    pub fn active_audio_offset(&self) -> Time {
        self.replaced_audio.as_ref().map(|r| r.offset).unwrap_or(self.audio_offset)
    }

    /// The replaced audio repeats so the clip has no silence.
    pub fn loops_audio(&self) -> bool {
        self.replaced_audio.as_ref().is_some_and(|r| r.looped)
    }

    pub fn audio_in(&self) -> Time {
        self.r#in - self.audio_slip()
    }

    pub fn audio_out(&self) -> Time {
        self.out - self.audio_slip()
    }
}

/// Clips played back to back, without gaps.
#[derive(Clone, Debug)]
pub struct Timeline {
    clips: Vec<Clip>,
    /// Where each clip starts, plus the end of the last one (len + 1 entries).
    bounds: Vec<Time>,
}

impl PartialEq for Timeline {
    fn eq(&self, other: &Self) -> bool {
        self.clips == other.clips
    }
}

impl Default for Timeline {
    fn default() -> Self {
        Timeline::new(Vec::new())
    }
}

impl Timeline {
    pub fn new(clips: Vec<Clip>) -> Timeline {
        let mut bounds = Vec::with_capacity(clips.len() + 1);
        let mut start = Time::ZERO;
        bounds.push(start);
        for c in &clips {
            start += c.duration();
            bounds.push(start);
        }
        Timeline { clips, bounds }
    }

    pub fn empty() -> Timeline {
        Timeline::new(Vec::new())
    }

    pub fn clips(&self) -> &[Clip] {
        &self.clips
    }

    pub fn into_clips(self) -> Vec<Clip> {
        self.clips
    }

    pub fn bounds(&self) -> &[Time] {
        &self.bounds
    }

    pub fn duration(&self) -> Time {
        *self.bounds.last().unwrap()
    }

    pub fn index_of(&self, clip_id: &str) -> Option<usize> {
        self.clips.iter().position(|c| c.id == clip_id)
    }

    pub fn start_of(&self, index: usize) -> Time {
        self.bounds[index.min(self.clips.len())]
    }

    /// Finds the clip playing at `time`; the end of the timeline maps to the last clip.
    pub fn locate(&self, time: Time) -> Option<(usize, Time)> {
        if self.clips.is_empty() {
            return None;
        }
        let n = self.clips.len();
        let b = &self.bounds;
        // The last clip starting at or before the time; clips of zero length are passed over.
        let mut index: isize = match b[..n].binary_search(&time) {
            Ok(i) => i as isize,
            Err(i) => i as isize - 1,
        };
        while index < n as isize - 1 && (index < 0 || time >= b[(index + 1) as usize]) {
            if index < 0 {
                break;
            }
            index += 1;
        }
        let index = index.clamp(0, n as isize - 1) as usize;
        Some((index, b[index]))
    }
}

/// The undoable part of a project.
#[derive(Clone, Debug, Default)]
pub struct ProjectState {
    /// The videos clips are cut from.
    pub sources: Vec<SourceMedia>,
    pub timeline: Timeline,
    /// Imported audio files that can replace clips' sound.
    pub audio_files: Vec<SourceMedia>,
    crop: Option<CropRect>,
}

impl PartialEq for ProjectState {
    fn eq(&self, o: &Self) -> bool {
        self.sources == o.sources && self.audio_files == o.audio_files && self.timeline == o.timeline && self.crop == o.crop
    }
}

impl ProjectState {
    pub fn new(sources: Vec<SourceMedia>, timeline: Timeline, audio_files: Vec<SourceMedia>) -> ProjectState {
        ProjectState { sources, timeline, audio_files, crop: None }
    }

    pub fn empty() -> ProjectState {
        ProjectState::default()
    }

    /// The part of the output frame that is exported; None keeps the whole frame. Always inside the frame.
    pub fn crop(&self) -> Option<CropRect> {
        self.crop
    }

    /// The state with the output cropped to `crop` (clamped; one that keeps the whole frame, or can't be used,
    /// becomes None).
    pub fn with_crop(&self, crop: Option<CropRect>) -> ProjectState {
        let mut s = self.clone();
        s.crop = crop.and_then(|c| c.clamped()).filter(|c| !c.is_full());
        s
    }

    pub fn with_timeline(&self, timeline: Timeline) -> ProjectState {
        ProjectState { timeline, ..self.clone() }
    }

    pub fn source(&self, id: &str) -> Option<&SourceMedia> {
        self.sources.iter().find(|s| s.id == id)
    }

    pub fn audio_file(&self, id: &str) -> Option<&SourceMedia> {
        self.audio_files.iter().find(|s| s.id == id)
    }

    /// The videos or the audio files.
    pub fn files(&self, kind: FileKind) -> &[SourceMedia] {
        if kind == FileKind::Audio { &self.audio_files } else { &self.sources }
    }

    /// The state with the videos or the audio files changed.
    pub fn with_files(&self, kind: FileKind, files: Vec<SourceMedia>) -> ProjectState {
        let mut s = self.clone();
        if kind == FileKind::Audio {
            s.audio_files = files;
        } else {
            s.sources = files;
        }
        s
    }

    /// The state with one video or audio file changed (relinked, another audio stream).
    pub fn replace_file(&self, kind: FileKind, old: &SourceMedia, updated: SourceMedia) -> ProjectState {
        let mut files = self.files(kind).to_vec();
        if let Some(i) = files.iter().position(|f| f == old) {
            files[i] = updated;
        }
        self.with_files(kind, files)
    }

    /// The file whose audio the clip plays: its replacement audio file, or its own video.
    pub fn audio_source_of(&self, clip: &Clip) -> Option<&SourceMedia> {
        match &clip.replaced_audio {
            Some(r) => self.audio_file(&r.file_id),
            None => self.source(&clip.source_id),
        }
    }

    /// The file the clip plays with its probe results and chosen stream; None when the clip is silent: the file is
    /// gone, isn't analyzed yet (`info` returns None) or has no such audio stream.
    pub fn heard_audio<'a>(&self, clip: &Clip, info: impl Fn(&str) -> Option<&'a MediaInfo>) -> Option<HeardAudio> {
        let file = self.audio_source_of(clip)?;
        let media = info(&file.id)?;
        let stream = media.audio(file.audio_stream_index)?;
        Some(HeardAudio { file: file.clone(), stream: stream.clone(), info: media.clone() })
    }
}
