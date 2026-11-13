use super::media_info::{AudioStreamInfo, MediaInfo};
use super::time::Time;
use super::timeline::{Clip, SourceMedia};

/// The file a clip plays (an audio file dropped on it, or its own video), with its probe results and chosen stream.
#[derive(Clone, Debug, PartialEq)]
pub struct HeardAudio {
    pub file: SourceMedia,
    pub info: MediaInfo,
    pub stream: AudioStreamInfo,
}

impl HeardAudio {
    /// Where the heard stream ends.
    pub fn end(&self) -> Time {
        self.info.audio_end(&self.stream)
    }

    /// The sound under the video `[video_start, video_start + length)` of `clip`.
    pub fn layout_range(&self, clip: &Clip, video_start: Time, length: Time) -> AudioLayout {
        AudioLayout::for_clip(clip, video_start, length, self.end())
    }

    /// The sound under the whole clip.
    pub fn layout(&self, clip: &Clip) -> AudioLayout {
        self.layout_range(clip, clip.r#in, clip.duration())
    }
}

/// `length` of sound starting `at` into a stretch: the heard file read from `read_from`, or silence when None.
#[derive(Clone, Copy, PartialEq, Debug)]
pub struct AudioSpan {
    pub at: Time,
    pub length: Time,
    pub read_from: Option<Time>,
}

impl AudioSpan {
    pub fn is_silence(&self) -> bool {
        self.read_from.is_none()
    }
}

/// Where the audio under a stretch of a clip's video comes from, once the clip's audio is slipped: `lead` of
/// silence, then `read_length` of the file read from `read_from`, then `tail` of silence, `length` in all.
#[derive(Clone, Copy, PartialEq, Debug)]
pub struct AudioPlacement {
    pub lead: Time,
    pub read_from: Time,
    pub read_length: Time,
    pub length: Time,
}

const TOLERANCE: Time = Time::from_ms(1);

impl AudioPlacement {
    pub fn tail(&self) -> Time {
        self.length - self.lead - self.read_length
    }

    /// True when (almost) none of the file's audio falls in the range: it is all silence.
    pub fn is_silent(&self) -> bool {
        self.read_length <= TOLERANCE
    }

    /// Audio under the video `[video_start, video_start + length)` of a clip whose audio is slipped by `offset`, from
    /// a file whose audio stream ends at `audio_end`.
    pub fn compute(video_start: Time, length: Time, offset: Time, audio_end: Time) -> AudioPlacement {
        let start = video_start - offset;
        let read_from = start.max(Time::ZERO);
        let lead = if read_from - start < length { read_from - start } else { length };
        let end = if start + length < audio_end { start + length } else { audio_end };
        let read_length = if end > read_from { end - read_from } else { Time::ZERO };
        AudioPlacement { lead, read_from, read_length, length }
    }
}

/// A stretch of a looped audio file: `length` of the file read from `phase`, starting again from its beginning
/// every `period`.
#[derive(Clone, Copy, PartialEq, Debug)]
pub struct AudioLoop {
    pub phase: Time,
    pub period: Time,
    pub length: Time,
}

impl AudioLoop {
    /// Files shorter than this play once instead of looping.
    pub const MIN_PERIOD: Time = Time::from_ms(100);

    /// The stretch runs past the file's end, so the file repeats in it.
    pub fn wraps(&self) -> bool {
        self.phase + self.length > self.period
    }

    pub fn can_loop(audio_end: Time) -> bool {
        audio_end >= Self::MIN_PERIOD
    }

    pub fn compute(video_start: Time, length: Time, slip: Time, audio_end: Time) -> AudioLoop {
        let start = (video_start - slip).0.rem_euclid(audio_end.0);
        AudioLoop { phase: Time(start), period: audio_end, length }
    }

    /// The stretch's first piece as a plain read from `phase`: all of it for a loop that doesn't wrap, followed by
    /// silence for one that does.
    pub fn as_placement(&self) -> AudioPlacement {
        AudioPlacement {
            lead: Time::ZERO,
            read_from: self.phase,
            read_length: if self.wraps() { self.period - self.phase } else { self.length },
            length: self.length,
        }
    }

    /// The pieces of the file to play in order: from the phase to the end, whole files, then the rest.
    pub fn pieces(&self) -> Vec<(Time, Time)> {
        let mut out = Vec::new();
        let mut left = self.length;
        let mut from = self.phase;
        while left > Time::ZERO {
            let piece = if self.period - from < left { self.period - from } else { left };
            out.push((from, piece));
            left -= piece;
            from = Time::ZERO;
        }
        out
    }
}

/// What a stretch of a clip sounds like: the heard file played once where the slipped audio falls (`Placed`,
/// silence around it), or repeated (`Looped`, for replaced audio that loops). This is the one place that chooses
/// between them; the preview, every export mode, the timeline drawing and snapping all render its answer.
#[derive(Clone, Copy, PartialEq, Debug)]
pub enum AudioLayout {
    /// The file played once, slipped. `file_start` is the file time at the stretch's start, before clamping.
    Placed { placement: AudioPlacement, file_start: Time },
    /// The file repeated with no silence, from the loop's phase.
    Looped(AudioLoop),
}

impl AudioLayout {
    pub fn for_clip(clip: &Clip, video_start: Time, length: Time, audio_end: Time) -> AudioLayout {
        if clip.loops_audio() && AudioLoop::can_loop(audio_end) {
            AudioLayout::Looped(AudioLoop::compute(video_start, length, clip.audio_slip(), audio_end))
        } else {
            AudioLayout::Placed {
                placement: AudioPlacement::compute(video_start, length, clip.audio_slip(), audio_end),
                file_start: video_start - clip.audio_slip(),
            }
        }
    }

    pub fn length(&self) -> Time {
        match self {
            AudioLayout::Placed { placement, .. } => placement.length,
            AudioLayout::Looped(l) => l.length,
        }
    }

    /// The stretch from start to end, as file reads and silences in order.
    pub fn spans(&self) -> Vec<AudioSpan> {
        match self {
            AudioLayout::Placed { placement: p, .. } => {
                if p.is_silent() {
                    return vec![AudioSpan { at: Time::ZERO, length: p.length, read_from: None }];
                }
                let mut out = Vec::new();
                if p.lead > Time::ZERO {
                    out.push(AudioSpan { at: Time::ZERO, length: p.lead, read_from: None });
                }
                out.push(AudioSpan { at: p.lead, length: p.read_length, read_from: Some(p.read_from) });
                if p.tail() > Time::ZERO {
                    out.push(AudioSpan { at: p.lead + p.read_length, length: p.tail(), read_from: None });
                }
                out
            }
            AudioLayout::Looped(l) => {
                let mut at = Time::ZERO;
                l.pieces()
                    .into_iter()
                    .map(|(read_from, length)| {
                        let span = AudioSpan { at, length, read_from: Some(read_from) };
                        at += length;
                        span
                    })
                    .collect()
            }
        }
    }

    /// Where the sound starts, runs out or starts again, strictly inside the stretch and within `[from, to]`, in
    /// order: the marks the timeline draws and the playhead snaps to.
    pub fn edges(&self, from: Time, to: Time) -> Vec<Time> {
        let mut out = Vec::new();
        match self {
            AudioLayout::Placed { placement: p, .. } => {
                let lead = p.lead;
                if lead > Time::ZERO && lead < p.length && lead >= from && lead <= to {
                    out.push(lead);
                }
                let end = lead + p.read_length;
                if p.read_length > Time::ZERO && p.tail() > Time::ZERO && end >= from && end <= to {
                    out.push(end);
                }
            }
            AudioLayout::Looped(l) => {
                // The seams: the file starts again at period − phase into the stretch, then every period.
                let first = l.period - l.phase;
                let skip = if from > first { ((from - first) / l.period).ceil() as i64 } else { 0 };
                let mut seam = first + l.period * skip;
                while seam <= to && seam < l.length {
                    if seam > Time::ZERO {
                        out.push(seam);
                    }
                    seam += l.period;
                }
            }
        }
        out
    }

    /// The time in the heard file playing at `local` into the stretch (outside the file where it is silent).
    pub fn file_time(&self, local: Time) -> Time {
        match self {
            AudioLayout::Placed { file_start, .. } => *file_start + local,
            AudioLayout::Looped(l) => Time((l.phase + local).0.rem_euclid(l.period.0)),
        }
    }
}
