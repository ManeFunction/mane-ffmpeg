use crate::model::time::{self, Time};
use crate::model::{Clip, MediaInfo, Rational};

/// Where cuts may land in one source, and how long it is. Every cut lands on a whole frame. With `keyframes`
/// (snapping for stream copy, which can only start a clip on a keyframe), in points land on the nearest keyframe.
#[derive(Clone, PartialEq, Debug, Default)]
pub struct CutGrid {
    pub duration: Time,
    pub frame_rate: Rational,
    pub frame_phase: Time,
    pub keyframes: Option<Vec<Time>>,
}

impl CutGrid {
    pub fn new(duration: Time) -> CutGrid {
        CutGrid { duration, frame_rate: Rational::ZERO, frame_phase: Time::ZERO, keyframes: None }
    }

    pub fn for_media(media: &MediaInfo, keyframes: Option<Vec<Time>>) -> CutGrid {
        CutGrid {
            duration: media.duration,
            frame_rate: media.video.as_ref().map(|v| v.frame_rate).unwrap_or_default(),
            frame_phase: media.video.as_ref().map(|v| v.start_offset).unwrap_or_default(),
            keyframes,
        }
    }

    /// For a source that hasn't been analyzed: nothing to snap to, and a clip can't grow past its out point.
    pub fn unknown(clip: &Clip) -> CutGrid {
        CutGrid::new(clip.out)
    }

    pub fn snap_in_point(&self, t: Time) -> Time {
        match &self.keyframes {
            Some(k) if !k.is_empty() => *k.iter().min_by_key(|k| (**k - t).0.unsigned_abs()).unwrap(),
            _ => self.snap_out_point(t),
        }
    }

    pub fn snap_out_point(&self, t: Time) -> Time {
        time::snap_to_frame(t, self.frame_rate, self.frame_phase)
    }
}
