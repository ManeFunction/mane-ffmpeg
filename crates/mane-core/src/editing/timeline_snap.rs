//! Where the playhead snaps while it is dragged with Shift held: clip edges, and the edges of each clip's audio
//! inside it (where shifted or short audio starts or runs out, and where looped audio starts again).

use crate::model::{AudioLayout, Clip, Time, Timeline};

/// The snap point nearest to `time`, or None when none is within `radius`. `audio_end` is where the clip's heard
/// audio stream ends; None when it has none or isn't analyzed yet.
pub fn nearest(timeline: &Timeline, time: Time, radius: Time, audio_end: &dyn Fn(&Clip) -> Option<Time>) -> Option<Time> {
    let mut best: Option<Time> = None;
    let mut consider = |point: Time| {
        let distance = (point - time).abs();
        if distance <= radius && best.is_none_or(|b| distance < (b - time).abs()) {
            best = Some(point);
        }
    };
    let b = timeline.bounds();
    for (i, clip) in timeline.clips().iter().enumerate() {
        let (start, end) = (b[i], b[i + 1]);
        if time < start - radius || time > end + radius {
            continue;
        }
        consider(start);
        consider(end);
        let Some(heard_end) = audio_end(clip) else { continue };
        let local = time - start;
        for point in AudioLayout::for_clip(clip, clip.r#in, clip.duration(), heard_end).edges(local - radius, local + radius) {
            consider(start + point);
        }
    }
    best
}
