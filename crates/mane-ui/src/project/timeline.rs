//! What the timeline control draws (zoom, scroll, playhead, selection) and the gestures it performs on the window.

use super::ProjectWindow;
use crate::logic::audio_drag::AudioDropTarget;
use crate::logic::timeline_gestures::{GestureHost, TimelineGestures};
use mane_core::editing::CutGrid;
use mane_core::editing::timeline_editor as te;
use mane_core::model::{Clip, ProjectState, Time, Timeline};
use std::collections::HashSet;

pub const MIN_ZOOM: f64 = 0.02;
pub const MAX_ZOOM: f64 = 1200.0;

pub struct TimelineState {
    /// Pixels per second. 0 until the first fit.
    pub zoom: f64,
    /// Timeline time (seconds) at the left edge of the tracks.
    pub scroll: f64,
    pub playhead: Time,
    pub selection: HashSet<String>,
    /// Width of the track area in pixels, reported by the view.
    pub viewport_width: f64,
    pub gestures: TimelineGestures,
    /// Where an audio file being dragged would land; None when nothing is dragged.
    pub audio_drop: Option<AudioDropTarget>,
}

impl Default for TimelineState {
    fn default() -> Self {
        TimelineState {
            zoom: 0.0,
            scroll: 0.0,
            playhead: Time::ZERO,
            selection: HashSet::new(),
            viewport_width: 800.0,
            gestures: TimelineGestures::default(),
            audio_drop: None,
        }
    }
}

impl TimelineState {
    pub fn select(&mut self, timeline: &Timeline, clip_id: &str, toggle: bool, range: bool) {
        if range && !self.selection.is_empty() {
            let clips = timeline.clips();
            let anchor = clips.iter().position(|c| self.selection.contains(&c.id));
            let target = timeline.index_of(clip_id);
            if let (Some(a), Some(t)) = (anchor, target) {
                self.selection = clips[a.min(t)..=a.max(t)].iter().map(|c| c.id.clone()).collect();
            }
        } else if toggle {
            if !self.selection.remove(clip_id) {
                self.selection.insert(clip_id.to_string());
            }
        } else if !self.selection.contains(clip_id) || self.selection.len() > 1 {
            self.selection = [clip_id.to_string()].into();
        }
    }

    pub fn select_all(&mut self, timeline: &Timeline) {
        self.selection = timeline.clips().iter().map(|c| c.id.clone()).collect();
    }

    pub fn select_only(&mut self, clip_id: &str) {
        self.selection = [clip_id.to_string()].into();
    }

    pub fn clear_selection(&mut self) {
        self.selection.clear();
    }

    /// Drops ids that no longer exist (after undo or delete).
    pub fn prune_selection(&mut self, state: &ProjectState) {
        let ids: HashSet<&str> = state.timeline.clips().iter().map(|c| c.id.as_str()).collect();
        self.selection.retain(|id| ids.contains(id.as_str()));
    }

    pub fn clamp(&self, timeline: &Timeline, t: Time) -> Time {
        t.clamp(Time::ZERO, timeline.duration())
    }

    pub fn fit(&mut self, duration: Time) {
        let seconds = duration.total_seconds().max(1.0);
        self.zoom = ((self.viewport_width - 24.0) / seconds).clamp(MIN_ZOOM, MAX_ZOOM);
        self.scroll = 0.0;
    }

    /// Zooms keeping the time under `anchor_x` (pixels from the track start) in place.
    pub fn zoom_by(&mut self, factor: f64, anchor_x: f64, duration: Time) {
        if self.zoom <= 0.0 {
            self.fit(duration);
        }
        let anchor_time = self.scroll + anchor_x / self.zoom;
        self.zoom = (self.zoom * factor).clamp(MIN_ZOOM, MAX_ZOOM);
        self.scroll = (anchor_time - anchor_x / self.zoom).max(0.0);
    }

    pub fn zoom_around_playhead(&mut self, factor: f64, duration: Time) {
        if self.zoom <= 0.0 {
            self.fit(duration);
        }
        let x = (self.playhead.total_seconds() - self.scroll) * self.zoom;
        let anchor = if x >= 0.0 && x <= self.viewport_width { x } else { self.viewport_width / 2.0 };
        self.zoom_by(factor, anchor, duration);
    }

    /// Keeps the playhead visible during playback.
    pub fn follow_playhead(&mut self) {
        if self.zoom <= 0.0 {
            return;
        }
        let visible = self.viewport_width / self.zoom;
        let t = self.playhead.total_seconds();
        if t < self.scroll || t > self.scroll + visible * 0.95 {
            self.scroll = (t - visible * 0.1).max(0.0);
        }
    }
}

/// The window as the gestures' host.
pub struct Host<'a>(pub &'a mut ProjectWindow);

impl GestureHost for Host<'_> {
    fn timeline(&self) -> Timeline {
        self.0.state().timeline.clone()
    }
    fn selection(&self) -> HashSet<String> {
        self.0.timeline.selection.clone()
    }
    fn zoom(&self) -> f64 {
        self.0.timeline.zoom
    }
    fn select(&mut self, clip_id: &str, toggle: bool, range: bool) {
        let t = self.0.state().timeline.clone();
        self.0.timeline.select(&t, clip_id, toggle, range);
        self.0.reset_fields();
    }
    fn clear_selection(&mut self) {
        self.0.timeline.clear_selection();
        self.0.reset_fields();
    }
    fn seek(&mut self, t: Time, scrubbing: bool) {
        let t = self.0.timeline.clamp(&self.0.state().timeline, t);
        self.0.seek_to(t, scrubbing);
    }
    fn end_scrub(&mut self) {
        self.0.end_scrub();
    }
    fn begin_gesture(&mut self) {
        self.0.history.begin_gesture();
    }
    fn preview_timeline(&mut self, timeline: Timeline) {
        let next = self.0.state().with_timeline(timeline);
        self.0.history.replace_current(next);
    }
    fn end_gesture(&mut self) {
        self.0.history.end_gesture();
    }
    fn move_selection(&mut self, insert_index: usize) {
        let sel = self.0.timeline.selection.clone();
        self.0.commit_timeline(|t| te::move_clips(t, &sel, insert_index));
    }
    fn grid_of(&self, source_id: &str) -> Option<CutGrid> {
        self.0.grid_of(source_id)
    }
    fn preview_audio_drag(&mut self, delta: Time) {
        self.0.preview_audio_delay(delta);
    }
    fn audio_end(&self, clip: &Clip) -> Option<Time> {
        self.0.state().heard_audio(clip, |id| self.0.catalog.info(id)).map(|h| h.end())
    }
}

impl ProjectWindow {
    /// Runs a gesture step with the window as its host.
    pub fn with_gestures(&mut self, f: impl FnOnce(&mut TimelineGestures, &mut Host)) {
        let mut g = std::mem::take(&mut self.timeline.gestures);
        f(&mut g, &mut Host(self));
        self.timeline.gestures = g;
    }
}
