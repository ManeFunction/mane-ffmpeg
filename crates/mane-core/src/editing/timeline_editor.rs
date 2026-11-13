//! Pure edit operations; each returns a new timeline (or an equal one when nothing changes).

use super::cut_grid::CutGrid;
use crate::model::{AudioReplacement, Clip, Time, Timeline, ids};
use std::collections::HashSet;

/// Shortest clip an edit may leave behind.
pub const MIN_CLIP_DURATION: Time = Time::from_ms(40);
/// Largest audio slip in either direction.
pub const MAX_AUDIO_OFFSET: Time = Time::from_secs(60);
/// Largest slip of replaced audio, enough to reach any point in a long track.
pub const MAX_REPLACED_AUDIO_OFFSET: Time = Time::from_secs(24 * 3600);

pub type Grids<'a> = &'a dyn Fn(&str) -> Option<CutGrid>;

/// The slip limit of the clip's active audio.
pub fn max_audio_offset_for(clip: &Clip) -> Time {
    if clip.replaced_audio.is_none() { MAX_AUDIO_OFFSET } else { MAX_REPLACED_AUDIO_OFFSET }
}

pub fn append(timeline: &Timeline, clip: Clip) -> Timeline {
    let mut clips = timeline.clips().to_vec();
    clips.push(clip);
    Timeline::new(clips)
}

fn grid(clip: &Clip, grids: Grids) -> CutGrid {
    grids(&clip.source_id).unwrap_or_else(|| CutGrid::unknown(clip))
}

/// Splits the clip playing at timeline time `at`, at the nearest in point of its source's grid (the cut starts the
/// right half). Returns the id of the right half, or None when the cut is too close to an edge.
pub fn split(timeline: &Timeline, at: Time, grids: Grids) -> (Timeline, Option<String>) {
    let Some((index, clip_start)) = timeline.locate(at) else {
        return (timeline.clone(), None);
    };
    let clip = &timeline.clips()[index];
    let cut = grid(clip, grids).snap_in_point(clip.r#in + (at - clip_start));
    if cut - clip.r#in < MIN_CLIP_DURATION || clip.out - cut < MIN_CLIP_DURATION {
        return (timeline.clone(), None);
    }
    let left = Clip { out: cut, ..clip.clone() };
    let right = Clip { id: ids::new(), r#in: cut, ..clip.clone() };
    let right_id = right.id.clone();
    let mut clips = timeline.clips().to_vec();
    clips[index] = left;
    clips.insert(index + 1, right);
    (Timeline::new(clips), Some(right_id))
}

pub fn delete(timeline: &Timeline, ids: &HashSet<String>) -> Timeline {
    Timeline::new(timeline.clips().iter().filter(|c| !ids.contains(&c.id)).cloned().collect())
}

/// Where timeline time `at` lands once [`delete`] removes the clips: on the same moment, earlier by whatever is
/// deleted before it; inside a deleted clip, where that clip was.
pub fn time_after_delete(timeline: &Timeline, ids: &HashSet<String>, at: Time) -> Time {
    let b = timeline.bounds();
    let mut removed = Time::ZERO;
    for (i, c) in timeline.clips().iter().enumerate() {
        if b[i] >= at {
            break;
        }
        if ids.contains(&c.id) {
            removed += (if b[i + 1] < at { b[i + 1] } else { at }) - b[i];
        }
    }
    at - removed
}

/// Moves the given clips (keeping their order) so they start at `insert_index`, an index into the current list (as
/// if the moved clips were still in place).
pub fn move_clips(timeline: &Timeline, ids: &HashSet<String>, insert_index: usize) -> Timeline {
    let clips = timeline.clips();
    let moving: Vec<Clip> = clips.iter().filter(|c| ids.contains(&c.id)).cloned().collect();
    if moving.is_empty() {
        return timeline.clone();
    }
    let before = clips[..insert_index.min(clips.len())].iter().filter(|c| ids.contains(&c.id)).count();
    let mut rest: Vec<Clip> = clips.iter().filter(|c| !ids.contains(&c.id)).cloned().collect();
    let target = insert_index.saturating_sub(before).min(rest.len());
    rest.splice(target..target, moving);
    if rest == clips { timeline.clone() } else { Timeline::new(rest) }
}

fn set_clip(timeline: &Timeline, index: usize, clip: Clip) -> Timeline {
    let mut clips = timeline.clips().to_vec();
    clips[index] = clip;
    Timeline::new(clips)
}

/// Moves a clip's in point (ripple), snapped to its source's grid and clamped to the source and min duration.
pub fn trim_in(timeline: &Timeline, clip_id: &str, new_in: Time, grids: Grids) -> Timeline {
    let Some(index) = timeline.index_of(clip_id) else { return timeline.clone() };
    let clip = &timeline.clips()[index];
    let new_in = clamp(grid(clip, grids).snap_in_point(new_in), Time::ZERO, clip.out - MIN_CLIP_DURATION);
    if new_in == clip.r#in { timeline.clone() } else { set_clip(timeline, index, Clip { r#in: new_in, ..clip.clone() }) }
}

/// Moves a clip's out point (ripple), snapped to its source's grid and clamped to the source's end.
pub fn trim_out(timeline: &Timeline, clip_id: &str, new_out: Time, grids: Grids) -> Timeline {
    let Some(index) = timeline.index_of(clip_id) else { return timeline.clone() };
    let clip = &timeline.clips()[index];
    let g = grid(clip, grids);
    let new_out = clamp(g.snap_out_point(new_out), clip.r#in + MIN_CLIP_DURATION, g.duration);
    if new_out == clip.out { timeline.clone() } else { set_clip(timeline, index, Clip { out: new_out, ..clip.clone() }) }
}

/// Sets the offset of each clip's active audio: the replacement's when replaced, else its own.
pub fn set_audio_offset(timeline: &Timeline, ids: &HashSet<String>, offset: Time) -> Timeline {
    map(timeline, ids, |c| with_active_offset(c, offset))
}

pub fn nudge_audio_offset(timeline: &Timeline, ids: &HashSet<String>, delta: Time) -> Timeline {
    map(timeline, ids, |c| with_active_offset(c, c.active_audio_offset() + delta))
}

fn with_active_offset(clip: &Clip, offset: Time) -> Clip {
    let max = max_audio_offset_for(clip);
    let offset = clamp(offset, -max, max);
    let mut c = clip.clone();
    match &mut c.replaced_audio {
        Some(r) => r.offset = offset,
        None => c.audio_offset = offset,
    }
    c
}

/// Gives every clip of `source_id` the same own audio offset (replaced clips keep it for later).
pub fn set_audio_offset_for_source(timeline: &Timeline, source_id: &str, offset: Time) -> Timeline {
    let ids: HashSet<String> = timeline.clips().iter().filter(|c| c.source_id == source_id).map(|c| c.id.clone()).collect();
    map(timeline, &ids, |c| Clip { audio_offset: clamp(offset, -MAX_AUDIO_OFFSET, MAX_AUDIO_OFFSET), ..c.clone() })
}

/// Gives every clip playing `file_id` the same replacement offset.
pub fn set_replaced_audio_offset_for_file(timeline: &Timeline, file_id: &str, offset: Time) -> Timeline {
    let ids = clips_playing(timeline, file_id);
    set_audio_offset(timeline, &ids, offset)
}

fn clips_playing(timeline: &Timeline, file_id: &str) -> HashSet<String> {
    timeline.clips().iter().filter(|c| c.replaced_audio.as_ref().is_some_and(|r| r.file_id == file_id)).map(|c| c.id.clone()).collect()
}

/// Plays audio file `file_id` instead of the clip's sound, starting at its in point. A clip that already had
/// replaced audio keeps its loop choice.
pub fn replace_audio(timeline: &Timeline, clip_id: &str, file_id: &str) -> Timeline {
    let ids: HashSet<String> = [clip_id.to_string()].into();
    map(timeline, &ids, |c| Clip {
        replaced_audio: Some(AudioReplacement {
            file_id: file_id.to_string(),
            anchor: c.r#in,
            offset: Time::ZERO,
            looped: c.replaced_audio.as_ref().is_some_and(|r| r.looped),
        }),
        ..c.clone()
    })
}

/// The clips play their own audio again, with their own offsets.
pub fn restore_original_audio(timeline: &Timeline, ids: &HashSet<String>) -> Timeline {
    map(timeline, ids, |c| Clip { replaced_audio: None, ..c.clone() })
}

/// Turns looping of replaced audio on or off (clips with their own audio are left alone).
pub fn set_audio_loop(timeline: &Timeline, ids: &HashSet<String>, looped: bool) -> Timeline {
    map(timeline, ids, |c| {
        let mut c = c.clone();
        if let Some(r) = &mut c.replaced_audio {
            r.looped = looped;
        }
        c
    })
}

/// Clips playing an audio file that is removed go back to their own audio.
pub fn remove_audio_file(timeline: &Timeline, file_id: &str) -> Timeline {
    let ids = clips_playing(timeline, file_id);
    restore_original_audio(timeline, &ids)
}

pub fn remove_source(timeline: &Timeline, source_id: &str) -> Timeline {
    Timeline::new(timeline.clips().iter().filter(|c| c.source_id != source_id).cloned().collect())
}

/// Replaces a clip's in/out in one step (inspector edits), snapped and clamped like trims.
pub fn set_range(timeline: &Timeline, clip_id: &str, new_in: Time, new_out: Time, grids: Grids) -> Timeline {
    let Some(index) = timeline.index_of(clip_id) else { return timeline.clone() };
    let clip = &timeline.clips()[index];
    let g = grid(clip, grids);
    let new_in = clamp(g.snap_in_point(new_in), Time::ZERO, g.duration - MIN_CLIP_DURATION);
    let new_out = clamp(g.snap_out_point(new_out), new_in + MIN_CLIP_DURATION, g.duration);
    if new_in == clip.r#in && new_out == clip.out {
        timeline.clone()
    } else {
        set_clip(timeline, index, Clip { r#in: new_in, out: new_out, ..clip.clone() })
    }
}

fn map(timeline: &Timeline, ids: &HashSet<String>, change: impl Fn(&Clip) -> Clip) -> Timeline {
    let mut changed = false;
    let clips: Vec<Clip> = timeline
        .clips()
        .iter()
        .map(|c| {
            if !ids.contains(&c.id) {
                return c.clone();
            }
            let u = change(c);
            if u != *c {
                changed = true;
            }
            u
        })
        .collect();
    if changed { Timeline::new(clips) } else { timeline.clone() }
}

/// Clamps without panicking when min > max (the C# ternary order: min wins).
pub fn clamp(value: Time, min: Time, max: Time) -> Time {
    if value < min {
        min
    } else if value > max {
        max
    } else {
        value
    }
}
