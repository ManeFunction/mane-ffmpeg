//! Where things are on the timeline control, in pixels relative to its top-left: the track header, the ruler, V1 and
//! A1, and the mapping between x and timeline time at a zoom (pixels per second) and scroll (seconds at the left edge).

use super::timeline_gestures::{TimelineHit, TimelineZone};
use mane_core::model::{Time, Timeline};

pub const HEADER: f64 = 44.0;
pub const RULER_HEIGHT: f64 = 26.0;
/// Gap between the ruler and V1, repeated below A1.
pub const TRACK_MARGIN: f64 = 8.0;
pub const VIDEO_TOP: f64 = RULER_HEIGHT + TRACK_MARGIN;
pub const VIDEO_HEIGHT: f64 = 62.0;
pub const AUDIO_TOP: f64 = VIDEO_TOP + VIDEO_HEIGHT + 6.0;
pub const AUDIO_HEIGHT: f64 = VIDEO_HEIGHT / 2.0;
pub const HEIGHT: f64 = AUDIO_TOP + AUDIO_HEIGHT + TRACK_MARGIN;
/// How close to a clip edge a press grabs it for trimming.
pub const EDGE_GRAB: f64 = 6.0;

pub fn x(zoom: f64, scroll: f64, t: Time) -> f64 {
    HEADER + (t.total_seconds() - scroll) * zoom
}

pub fn time(zoom: f64, scroll: f64, x: f64) -> Time {
    Time::from_seconds(scroll + (x - HEADER) / zoom.max(1e-9))
}

pub fn in_video_row(y: f64) -> bool {
    (VIDEO_TOP..VIDEO_TOP + VIDEO_HEIGHT).contains(&y)
}

pub fn in_audio_row(y: f64) -> bool {
    (AUDIO_TOP..AUDIO_TOP + AUDIO_HEIGHT).contains(&y)
}

pub fn hit_test(timeline: &Timeline, zoom: f64, scroll: f64, px: f64, py: f64) -> TimelineHit {
    let t = time(zoom, scroll, px);
    if py < VIDEO_TOP {
        return TimelineHit { zone: TimelineZone::Ruler, time: t, clip_id: None };
    }
    if !in_video_row(py) && !in_audio_row(py) {
        return TimelineHit { zone: TimelineZone::Empty, time: t, clip_id: None };
    }
    let mut start = Time::ZERO;
    for clip in timeline.clips() {
        let x0 = x(zoom, scroll, start);
        let x1 = x(zoom, scroll, start + clip.duration());
        start += clip.duration();
        if px < x0 || px > x1 {
            continue;
        }
        let zone = if in_audio_row(py) {
            TimelineZone::Audio
        } else if x1 - x0 <= EDGE_GRAB * 3.0 {
            TimelineZone::Clip
        } else if px - x0 <= EDGE_GRAB {
            TimelineZone::TrimIn
        } else if x1 - px <= EDGE_GRAB {
            TimelineZone::TrimOut
        } else {
            TimelineZone::Clip
        };
        return TimelineHit { zone, time: t, clip_id: Some(clip.id.clone()) };
    }
    TimelineHit { zone: TimelineZone::Empty, time: t, clip_id: None }
}
