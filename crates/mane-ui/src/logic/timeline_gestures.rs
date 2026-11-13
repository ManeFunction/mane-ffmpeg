//! The timeline's pointer gestures in timeline terms: a press on a zone, moves to a time, a release. Clicks select and
//! seek; drags scrub, reorder clips, trim edges or slip audio, each drag one undo step. Scrubbing with Shift held
//! snaps the playhead to clip edges and audio edges.

use mane_core::editing::timeline_editor as te;
use mane_core::editing::{CutGrid, timeline_snap};
use mane_core::model::{Clip, Time, Timeline};
use std::collections::HashSet;

/// What a point on the timeline is over.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum TimelineZone {
    /// The ruler (and the gap under it): clicks only seek.
    Ruler,
    /// Track rows, but no clip there: clicks seek and clear the selection.
    Empty,
    TrimIn,
    TrimOut,
    /// A clip's body in V1: select, or drag to reorder.
    Clip,
    /// A clip in A1: select, or drag to slip its audio.
    Audio,
}

#[derive(Clone, PartialEq, Debug)]
pub struct TimelineHit {
    pub zone: TimelineZone,
    pub time: Time,
    pub clip_id: Option<String>,
}

/// What timeline gestures act on: the window's timeline, or a fake in tests.
pub trait GestureHost {
    fn timeline(&self) -> Timeline;
    fn selection(&self) -> HashSet<String>;
    /// Pixels per second, to tell a click from a drag.
    fn zoom(&self) -> f64;
    fn select(&mut self, clip_id: &str, toggle: bool, range: bool);
    fn clear_selection(&mut self);
    fn seek(&mut self, time: Time, scrubbing: bool);
    fn end_scrub(&mut self);
    fn begin_gesture(&mut self);
    fn preview_timeline(&mut self, timeline: Timeline);
    fn end_gesture(&mut self);
    fn move_selection(&mut self, insert_index: usize);
    fn grid_of(&self, source_id: &str) -> Option<CutGrid>;
    /// Live audio feedback while an offset is being dragged.
    fn preview_audio_drag(&mut self, delta: Time);
    /// Where the clip's heard audio stream ends; None when it has none or isn't analyzed yet.
    fn audio_end(&self, clip: &Clip) -> Option<Time>;
}

/// How far (pixels) the pointer must move before a press on a clip becomes a drag.
pub const DRAG_THRESHOLD: f64 = 4.0;
/// How close (pixels) a Shift-scrub must come to a clip or audio edge to snap to it.
pub const SNAP_RADIUS: f64 = 8.0;

#[derive(Clone, Copy, PartialEq, Eq, Debug, Default)]
enum Drag {
    #[default]
    None,
    Scrub,
    PendingClip,
    Reorder,
    TrimIn,
    TrimOut,
    PendingSlip,
    Slip,
}

#[derive(Default, Debug)]
pub struct TimelineGestures {
    drag: Drag,
    press_time: Time,
    last_time: Time,
    clip: Option<Clip>,
    gesture_start: Option<Timeline>,
    command: bool,
    shift: bool,
    /// Where the dragged clips would land (an index into the clips), or None when not reordering.
    pub insert_index: Option<usize>,
}

impl TimelineGestures {
    pub fn is_active(&self) -> bool {
        self.drag != Drag::None
    }

    pub fn is_reordering(&self) -> bool {
        self.drag == Drag::Reorder
    }

    /// `command`: Cmd/Ctrl was held, a click toggles the clip in the selection. `shift`: a click extends the
    /// selection to the clip, a scrub snaps to edges.
    pub fn press(&mut self, host: &mut dyn GestureHost, hit: TimelineHit, command: bool, shift: bool) {
        self.reset();
        self.press_time = hit.time;
        self.last_time = hit.time;
        self.command = command;
        self.shift = shift;
        let timeline = host.timeline();
        let index = hit.clip_id.as_deref().and_then(|id| timeline.index_of(id));
        let Some(index) = index else {
            self.drag = Drag::Scrub;
            if hit.zone == TimelineZone::Empty {
                host.clear_selection();
            }
            let t = if shift { snap(host, hit.time) } else { hit.time };
            host.seek(t, true);
            return;
        };
        self.clip = Some(timeline.clips()[index].clone());
        self.gesture_start = Some(timeline);
        self.drag = match hit.zone {
            TimelineZone::TrimIn => Drag::TrimIn,
            TimelineZone::TrimOut => Drag::TrimOut,
            TimelineZone::Audio => Drag::PendingSlip,
            _ => Drag::PendingClip,
        };
        if matches!(self.drag, Drag::TrimIn | Drag::TrimOut) {
            host.begin_gesture();
        }
    }

    /// `time`: timeline time now under the pointer. `shift`: a scrub snaps, a slip goes at a tenth of the speed.
    pub fn move_to(&mut self, host: &mut dyn GestureHost, time: Time, shift: bool) {
        self.last_time = time;
        let delta = time - self.press_time;
        let dragged = (delta.total_seconds() * host.zoom()).abs() > DRAG_THRESHOLD;
        match self.drag {
            Drag::Scrub => {
                let t = if shift { snap(host, time) } else { time };
                host.seek(t, true);
            }
            Drag::PendingClip if dragged => {
                self.select_pressed_clip(host);
                self.drag = Drag::Reorder;
                self.insert_index = Some(nearest_cut(&host.timeline(), time));
            }
            Drag::Reorder => self.insert_index = Some(nearest_cut(&host.timeline(), time)),
            Drag::TrimIn => {
                let (start, clip) = (self.gesture_start.as_ref().unwrap(), self.clip.as_ref().unwrap());
                let grids = |id: &str| host.grid_of(id);
                let t = te::trim_in(start, &clip.id, clip.r#in + delta, &grids);
                host.preview_timeline(t);
            }
            Drag::TrimOut => {
                let (start, clip) = (self.gesture_start.as_ref().unwrap(), self.clip.as_ref().unwrap());
                let grids = |id: &str| host.grid_of(id);
                let t = te::trim_out(start, &clip.id, clip.out + delta, &grids);
                host.preview_timeline(t);
            }
            Drag::PendingSlip if dragged => {
                self.select_pressed_clip(host);
                host.begin_gesture();
                self.drag = Drag::Slip;
            }
            Drag::Slip => {
                let factor = if shift { 0.1 } else { 1.0 };
                let slip = Time::from_ms((delta.total_ms() * factor).round_ties_even() as i64);
                let t = te::nudge_audio_offset(self.gesture_start.as_ref().unwrap(), &host.selection(), slip);
                host.preview_timeline(t);
                host.preview_audio_drag(slip);
            }
            _ => {}
        }
    }

    /// Shift was pressed or released while the pointer stood still: a scrub snaps (or lets go) at once.
    pub fn shift_changed(&mut self, host: &mut dyn GestureHost, shift: bool) {
        if self.drag == Drag::Scrub {
            let t = if shift { snap(host, self.last_time) } else { self.last_time };
            host.seek(t, true);
        }
    }

    pub fn release(&mut self, host: &mut dyn GestureHost, time: Time) {
        match self.drag {
            Drag::Scrub => host.end_scrub(),
            Drag::PendingClip | Drag::PendingSlip => {
                let id = self.clip.as_ref().unwrap().id.clone();
                host.select(&id, self.command, self.shift);
                if !self.command && !self.shift {
                    host.seek(time, false);
                }
            }
            Drag::Reorder => host.move_selection(self.insert_index.unwrap_or(0)),
            Drag::TrimIn | Drag::TrimOut => host.end_gesture(),
            Drag::Slip => {
                host.end_gesture();
                host.preview_audio_drag(Time::ZERO);
            }
            Drag::None => {}
        }
        self.reset();
    }

    /// The pointer was taken away mid-gesture: keeps what was dragged so far.
    pub fn abort(&mut self, host: &mut dyn GestureHost) {
        if matches!(self.drag, Drag::TrimIn | Drag::TrimOut | Drag::Slip) {
            host.end_gesture();
        }
        if self.drag == Drag::Slip {
            host.preview_audio_drag(Time::ZERO);
        }
        if self.drag == Drag::Scrub {
            host.end_scrub();
        }
        self.reset();
    }

    fn select_pressed_clip(&self, host: &mut dyn GestureHost) {
        let id = &self.clip.as_ref().unwrap().id;
        if !host.selection().contains(id) {
            host.select(id, false, false);
        }
    }

    fn reset(&mut self) {
        self.drag = Drag::None;
        self.clip = None;
        self.gesture_start = None;
        self.insert_index = None;
    }
}

/// The clip or audio edge within `SNAP_RADIUS` pixels of `time`, else the time itself.
fn snap(host: &dyn GestureHost, time: Time) -> Time {
    let radius = Time::from_seconds(SNAP_RADIUS / host.zoom().max(1e-9));
    timeline_snap::nearest(&host.timeline(), time, radius, &|c| host.audio_end(c)).unwrap_or(time)
}

/// The clip boundary (index 0…count) nearest to `time`.
fn nearest_cut(timeline: &Timeline, time: Time) -> usize {
    let b = timeline.bounds();
    let mut best = 0;
    for k in 1..b.len() {
        if (b[k] - time).abs() < (b[best] - time).abs() {
            best = k;
        }
    }
    best
}
