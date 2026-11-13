//! One project's preview: the player, transport, source preview with marks, compact mode and cropping.

use super::ProjectWindow;
use crate::controls::video_view::{VideoRenderer, VideoTexture};
use crate::logic::crop_gesture::CropDragGesture;
use mane_core::editing::project_editor as pe;
use mane_core::editing::timeline_editor as te;
use mane_core::media::silence_asset;
use mane_core::media::source_catalog::SourceStatus;
use mane_core::model::time::{self, Time};
use mane_core::model::*;
use mane_core::mpv::preview_player::{PreviewEvent, PreviewPlayer};
use mane_core::mpv::{library, timeline_edl};
use std::sync::Arc;
use std::time::{Duration, Instant};

#[derive(Clone, Copy, PartialEq, Eq, Debug, Default)]
pub enum PreviewMode {
    #[default]
    Timeline,
    Source,
}

/// A shape the crop can be held to: width over height as shown; None for any shape, or the frame's own.
#[derive(Clone, Copy, PartialEq, Debug)]
pub struct CropAspect {
    pub name: &'static str,
    pub ratio: Option<f64>,
    pub is_frame: bool,
}

impl CropAspect {
    /// The width over height it keeps in a frame of `frame_aspect`, or None for any shape.
    pub fn r#in(&self, frame_aspect: f64) -> Option<f64> {
        if self.is_frame { (frame_aspect > 0.0).then_some(frame_aspect) } else { self.ratio }
    }
}

pub const CROP_ASPECTS: [CropAspect; 7] = [
    CropAspect { name: "Free", ratio: None, is_frame: false },
    CropAspect { name: "Original", ratio: None, is_frame: true },
    CropAspect { name: "16:9", ratio: Some(16.0 / 9.0), is_frame: false },
    CropAspect { name: "9:16", ratio: Some(9.0 / 16.0), is_frame: false },
    CropAspect { name: "4:3", ratio: Some(4.0 / 3.0), is_frame: false },
    CropAspect { name: "4:5", ratio: Some(4.0 / 5.0), is_frame: false },
    CropAspect { name: "1:1", ratio: Some(1.0), is_frame: false },
];

const RELOAD_DELAY: Duration = Duration::from_millis(250);

/// Field order matters: the renderer is dropped before the player it draws.
pub struct PreviewState {
    pub renderer: Option<VideoRenderer>,
    pub texture: VideoTexture,
    pub player: Option<PreviewPlayer>,
    pub is_playing: bool,
    pub message: Option<String>,
    pub mode: PreviewMode,
    /// The file previewed on its own (an id in the Files panel).
    pub previewed: Option<String>,
    pub source_position: Time,
    pub source_length: Time,
    pub mark_in: Option<Time>,
    pub mark_out: Option<Time>,
    pub volume: f64,
    /// Compact mode: the video preview is hidden and mpv plays audio only.
    pub is_compact: bool,
    scrubbing: bool,
    resume_after_scrub: bool,
    reload_at: Option<Instant>,
    silence_path: Option<String>,
    silence_pending: bool,
    // Cropping
    pub is_cropping: bool,
    crop_start: Option<Arc<ProjectState>>,
    crop_start_aspect: usize,
    pub aspect: usize,
    pub picture_aspect: f64,
    pub frame_aspect: f64,
    pub frame_pixels: (i32, i32),
    pub crop_drag: CropDragGesture,
}

impl Default for PreviewState {
    fn default() -> Self {
        PreviewState {
            renderer: None,
            texture: VideoTexture::default(),
            player: None,
            is_playing: false,
            message: None,
            mode: PreviewMode::Timeline,
            previewed: None,
            source_position: Time::ZERO,
            source_length: Time::ZERO,
            mark_in: None,
            mark_out: None,
            volume: 100.0,
            is_compact: false,
            scrubbing: false,
            resume_after_scrub: false,
            reload_at: None,
            silence_path: None,
            silence_pending: false,
            is_cropping: false,
            crop_start: None,
            crop_start_aspect: 0,
            aspect: 0,
            picture_aspect: 0.0,
            frame_aspect: 0.0,
            frame_pixels: (0, 0),
            crop_drag: CropDragGesture::default(),
        }
    }
}

const ADD_VIDEOS: &str = "Add videos to start: File → Add Videos, or drop files here.";

impl ProjectWindow {
    /// Creates the player once libmpv is loaded: when the window opens, or after libmpv was installed.
    pub fn start_player(&mut self) {
        if self.preview.player.is_some() || self.closing {
            return;
        }
        if !library::is_loaded() {
            self.preview.message =
                Some(format!("Preview needs libmpv. {}\n{}", library::load_error().unwrap_or_default(), library::install_hint()));
            return;
        }
        let hw = self.app.settings().current().hardware_decoding;
        match PreviewPlayer::create(hw, false, self.waker.as_fn()) {
            Ok(mut player) => {
                player.set_volume(self.preview.volume);
                // Started late, the window may already be compact and have clips.
                if self.preview.is_compact {
                    player.set_compact(true);
                }
                match VideoRenderer::new(player.mpv(), self.waker.as_fn()) {
                    Ok(r) => self.preview.renderer = Some(r),
                    Err(e) => self.preview.message = Some(e),
                }
                self.preview.player = Some(player);
                if self.preview.renderer.is_some() {
                    self.preview.message = Some(ADD_VIDEOS.into());
                }
                if self.has_clips() {
                    self.schedule_timeline_reload();
                }
            }
            Err(e) => self.preview.message = Some(format!("Preview couldn't start: {}", e.0)),
        }
    }

    pub fn set_volume(&mut self, v: f64) {
        self.preview.volume = v;
        if let Some(p) = &self.preview.player {
            p.set_volume(v);
        }
    }

    pub fn set_compact(&mut self, value: bool) {
        if self.preview.is_compact == value {
            return;
        }
        // Hiding the preview mid-crop discards the crop changes, as the close button does.
        if value {
            self.cancel_crop();
        }
        self.preview.is_compact = value;
        if let Some(p) = &mut self.preview.player {
            p.set_compact(value);
        }
        self.update_frame();
    }

    pub fn toggle_compact(&mut self) {
        self.set_compact(!self.preview.is_compact);
    }

    pub fn preview_toggle_text(&self) -> &'static str {
        if self.preview.is_compact { "Show preview" } else { "Hide preview" }
    }

    pub fn is_source_mode(&self) -> bool {
        self.preview.mode == PreviewMode::Source
    }

    fn previewed_item(&self) -> Option<&super::files::FileItem> {
        self.preview.previewed.as_deref().and_then(|id| self.file_item(id))
    }

    /// A video is previewed on its own: In/Out marks can pick a range of it (audio files have none).
    pub fn can_mark(&self) -> bool {
        self.is_source_mode() && self.previewed_item().is_some_and(|i| i.kind == FileKind::Video)
    }

    pub fn preview_title(&self) -> String {
        if let (PreviewMode::Source, Some(s)) = (self.preview.mode, self.previewed_item()) {
            return format!("{}: {}", if s.kind == FileKind::Audio { "Audio" } else { "Source" }, s.name());
        }
        if self.preview.is_cropping {
            return format!("Crop{}", self.crop_size_text().map(|t| format!(" · {t}")).unwrap_or_default());
        }
        match self.crop_size_text() {
            Some(size) => format!("Preview · cropped to {size}"),
            None => "Preview".into(),
        }
    }

    /// Where the preview is: in the previewed source, or the playhead on the timeline.
    pub fn position(&self) -> Time {
        if self.is_source_mode() { self.preview.source_position } else { self.timeline.playhead }
    }

    pub fn length(&self) -> Time {
        if self.is_source_mode() { self.preview.source_length } else { self.state().timeline.duration() }
    }

    pub fn position_text(&self) -> String {
        format!("{} / {}", time::display(self.position(), false), time::display(self.length(), false))
    }

    pub fn marks_text(&self) -> String {
        let f = |t: Option<Time>| t.map(|t| time::display(t, false)).unwrap_or_else(|| "—".into());
        format!("In {} · Out {}", f(self.preview.mark_in), f(self.preview.mark_out))
    }

    /// Player events, the delayed timeline reload: run every frame.
    pub(super) fn preview_logic(&mut self) {
        let events = self.preview.player.as_mut().map(|p| p.poll()).unwrap_or_default();
        for e in events {
            match e {
                PreviewEvent::Position(t) => self.on_preview_position(t),
                PreviewEvent::Duration(d) => {
                    if self.is_source_mode() {
                        self.preview.source_length = d;
                    }
                }
                PreviewEvent::Paused(p) => self.preview.is_playing = !p,
                PreviewEvent::EndReached => self.preview.is_playing = false,
                PreviewEvent::LoadFailed(e) => self.preview.message = Some(format!("Preview couldn't play this: {e}")),
                PreviewEvent::Restarted => {}
            }
        }
        if let Some(at) = self.preview.reload_at {
            let now = Instant::now();
            if now >= at {
                self.preview.reload_at = None;
                self.reload_timeline();
            } else {
                self.waker.wake_after(at - now);
            }
        }
        self.update_picture();
    }

    fn on_preview_position(&mut self, position: Time) {
        if self.is_source_mode() {
            self.preview.source_position = position;
            return;
        }
        if self.preview.scrubbing {
            return;
        }
        self.timeline.playhead = self.timeline.clamp(&self.state().timeline, position);
        if self.preview.is_playing {
            self.timeline.follow_playhead();
        }
    }

    fn schedule_timeline_reload(&mut self) {
        if self.preview.player.is_none() || self.is_source_mode() {
            return;
        }
        self.preview.reload_at = Some(Instant::now() + RELOAD_DELAY);
        self.waker.wake_after(RELOAD_DELAY);
    }

    pub fn reload_timeline(&mut self) {
        if self.preview.player.is_none() || self.is_source_mode() {
            return;
        }
        // A drag in progress is heard through audio-delay and seen on the timeline; the EDL is rebuilt on release.
        if self.history.in_gesture() {
            return;
        }
        if !self.has_clips() {
            self.preview.player.as_mut().unwrap().unload();
            self.preview.message = Some(ADD_VIDEOS.into());
            return;
        }
        let Some(silence) = self.preview.silence_path.clone() else {
            self.prepare_silence();
            return;
        };
        let url = timeline_edl::build(self.state(), &self.catalog.media(), &silence);
        let Some(url) = url else {
            self.preview.message = Some(if self.sources.iter().any(|s| s.is_problem()) {
                "Some videos are missing; relink them in the Files panel.".into()
            } else {
                "Analyzing videos…".into()
            });
            return;
        };
        self.preview.message = None;
        let playhead = self.timeline.playhead;
        self.preview.player.as_mut().unwrap().load_timeline(&url, playhead);
    }

    fn prepare_silence(&mut self) {
        if self.preview.silence_pending {
            return;
        }
        let Some(tools) = self.app.tools() else {
            self.preview.message = self.app.tools_error();
            return;
        };
        self.preview.silence_pending = true;
        self.spawn(
            move || silence_asset::ensure(&tools),
            |w, result| {
                w.preview.silence_pending = false;
                match result {
                    Ok(p) => {
                        w.preview.silence_path = Some(p.to_string_lossy().into_owned());
                        w.schedule_timeline_reload();
                    }
                    Err(e) => w.preview.message = Some(format!("Preview setup failed: {e}")),
                }
            },
        );
    }

    /// Moves the playhead (from timeline clicks, keys or the transport) and seeks the player.
    pub fn seek_to(&mut self, t: Time, scrubbing: bool) {
        if self.is_source_mode() {
            self.back_to_timeline();
        }
        if scrubbing && !self.preview.scrubbing {
            self.preview.scrubbing = true;
            self.preview.resume_after_scrub = self.preview.is_playing;
            if self.preview.is_playing
                && let Some(p) = &self.preview.player
            {
                p.set_paused(true);
            }
        }
        self.timeline.playhead = t;
        if let Some(p) = &self.preview.player {
            p.seek(t);
        }
        if !self.preview.is_playing {
            self.reset_fields();
        }
    }

    pub fn end_scrub(&mut self) {
        if !self.preview.scrubbing {
            return;
        }
        self.preview.scrubbing = false;
        if self.preview.resume_after_scrub
            && let Some(p) = &self.preview.player
        {
            p.set_paused(false);
        }
    }

    /// Puts the playhead at `t` after an edit moved what was under it, and plays the edited timeline from there at
    /// once: the old one has the wrong part at that time.
    pub fn move_playhead_after_edit(&mut self, t: Time) {
        self.timeline.playhead = t;
        self.preview.reload_at = None;
        self.reload_timeline();
    }

    pub fn preview_audio_delay(&self, delta: Time) {
        if let Some(p) = &self.preview.player {
            p.preview_audio_delay(delta);
        }
    }

    // ---- Transport --------------------------------------------------------------------------------

    pub fn play_pause(&mut self) {
        if self.preview.player.is_none() {
            return;
        }
        if !self.preview.is_playing && !self.is_source_mode() {
            if !self.has_clips() {
                return;
            }
            if !self.preview.player.as_ref().unwrap().is_timeline_loaded() {
                self.reload_timeline();
            }
            if self.timeline.playhead >= self.state().timeline.duration() - Time::from_ms(60) {
                self.seek_to(Time::ZERO, false);
            }
        }
        let playing = self.preview.is_playing;
        self.preview.player.as_ref().unwrap().set_paused(playing);
        // The flag follows mpv's report; show the change at once.
        self.preview.is_playing = !playing;
    }

    pub fn step_frame(&mut self, back: bool) {
        if self.preview.player.is_none() {
            return;
        }
        if self.preview.is_compact {
            // No video is decoded in compact mode, so step by one frame of what is being previewed.
            let video = if self.is_source_mode() {
                self.previewed_item().and_then(|i| i.info.as_ref()).and_then(|i| i.video.clone())
            } else {
                self.state()
                    .timeline
                    .locate(self.timeline.playhead)
                    .and_then(|(i, _)| self.catalog.info(&self.state().timeline.clips()[i].source_id))
                    .and_then(|i| i.video.clone())
            };
            let frame = time::frame_duration(video.map(|v| v.frame_rate).unwrap_or_default());
            let p = self.position();
            self.seek_preview(if back { p - frame } else { p + frame });
            return;
        }
        self.preview.player.as_ref().unwrap().step_frame(back);
    }

    /// Seeks whatever is previewed, within its length.
    pub fn seek_preview(&mut self, t: Time) {
        let t = t.clamp(Time::ZERO, self.length());
        if self.is_source_mode() {
            if let Some(p) = &self.preview.player {
                p.seek(t);
            }
        } else {
            self.seek_to(t, false);
        }
    }

    pub fn jump(&mut self, by: Time) {
        let p = self.position();
        self.seek_preview(p + by);
    }

    pub fn go_to_start(&mut self) {
        self.seek_preview(Time::ZERO);
    }

    pub fn go_to_end(&mut self) {
        let l = self.length();
        self.seek_preview(l);
    }

    /// Moves the playhead to the previous or next cut.
    pub fn jump_to_cut(&mut self, back: bool) {
        let b = self.state().timeline.bounds().to_vec();
        let now = self.timeline.playhead;
        let eps = Time::from_ms(5);
        let target = if back {
            b.iter().rev().find(|t| **t < now - eps).copied().unwrap_or(Time::ZERO)
        } else {
            b.iter().find(|t| **t > now + eps).copied().unwrap_or_else(|| self.state().timeline.duration())
        };
        self.seek_to(target, false);
    }

    // ---- Source preview ---------------------------------------------------------------------------

    pub fn preview_source(&mut self, id: &str) {
        let Some(item) = self.file_item(id).cloned() else { return };
        if self.preview.player.is_none() || item.status != SourceStatus::Ready {
            return;
        }
        self.set_compact(false);
        self.preview.mode = PreviewMode::Source;
        self.preview.previewed = Some(id.to_string());
        self.preview.mark_in = None;
        self.preview.mark_out = None;
        self.preview.source_position = Time::ZERO;
        self.preview.source_length = item.info.as_ref().map(|i| i.duration).unwrap_or_default();
        self.preview.message = None;
        let stream = self.catalog.audio(&item.source).map(|a| a.index);
        self.preview.player.as_mut().unwrap().load_source(&item.source.path, stream);
        self.update_frame();
    }

    pub fn back_to_timeline(&mut self) {
        if !self.is_source_mode() {
            return;
        }
        self.preview.mode = PreviewMode::Timeline;
        self.preview.previewed = None;
        if let Some(p) = &mut self.preview.player {
            p.unload();
        }
        self.reload_timeline();
        self.update_frame();
    }

    pub fn set_mark_in(&mut self) {
        let pos = self.preview.source_position;
        self.preview.mark_in = Some(match self.preview.previewed.as_deref().and_then(|id| self.grid_of(id)) {
            Some(g) => g.snap_in_point(pos),
            None => pos,
        });
    }

    pub fn set_mark_out(&mut self) {
        let pos = self.preview.source_position;
        self.preview.mark_out = Some(match self.preview.previewed.as_deref().and_then(|id| self.grid_of(id)) {
            Some(g) => g.snap_out_point(pos),
            None => pos,
        });
    }

    pub fn add_marked_range(&mut self) {
        if !self.can_mark() {
            return;
        }
        let Some(item) = self.previewed_item().cloned() else { return };
        let Some(info) = &item.info else { return };
        let mut start = self.preview.mark_in.unwrap_or(Time::ZERO);
        let mut end = self.preview.mark_out.unwrap_or(info.duration);
        if end <= start {
            std::mem::swap(&mut start, &mut end);
        }
        if end - start < te::MIN_CLIP_DURATION {
            self.status_message = Some("Set In (I) and Out (O) marks further apart.".into());
            return;
        }
        let next = pe::append_clip(self.state(), item.id(), start, end);
        self.commit_keeping_fit(next);
        self.status_message = Some(format!("Added {} from {} to the end of the timeline.", time::display(end - start, false), item.name()));
        self.preview.mark_in = None;
        self.preview.mark_out = None;
    }

    // ---- Cropping ---------------------------------------------------------------------------------

    /// What the preview shows of the frame: the crop on the timeline, except while it is being edited.
    pub fn shown_crop(&self) -> Option<CropRect> {
        if !self.is_source_mode() && !self.preview.is_cropping { self.state().crop() } else { None }
    }

    /// The shape a drag keeps (width over height), or 0 for any.
    pub fn aspect_lock(&self) -> f64 {
        CROP_ASPECTS[self.preview.aspect].r#in(self.preview.frame_aspect).unwrap_or(0.0)
    }

    /// There is a timeline picture to crop.
    pub fn can_crop(&self) -> bool {
        !self.is_source_mode() && self.has_clips() && !self.preview.is_compact && self.preview.frame_aspect > 0.0
    }

    pub fn has_crop(&self) -> bool {
        self.state().crop().is_some()
    }

    /// The crop in output pixels, as "1280 × 720"; None without one.
    pub fn crop_size_text(&self) -> Option<String> {
        let crop = self.state().crop()?;
        let (w, h) = self.preview.frame_pixels;
        (w > 0 && h > 0).then(|| crop.to_pixels(w, h).size_text())
    }

    /// Picking a shape reshapes the crop at once; "Free" leaves it as it is.
    pub fn select_crop_aspect(&mut self, index: usize) {
        if self.preview.aspect == index {
            return;
        }
        self.preview.aspect = index;
        if self.aspect_lock() > 0.0 && self.preview.frame_aspect > 0.0 {
            let crop = self.state().crop().unwrap_or(CropRect::FULL).with_aspect(self.aspect_lock(), self.preview.frame_aspect);
            self.commit_crop(Some(crop));
        }
    }

    pub fn toggle_crop(&mut self) {
        if self.preview.is_cropping {
            self.set_cropping(false);
        } else if self.can_crop() {
            self.set_cropping(true);
        }
    }

    fn set_cropping(&mut self, value: bool) {
        if self.preview.is_cropping == value {
            return;
        }
        self.preview.is_cropping = value;
        self.preview.crop_start = value.then(|| self.history.current().clone());
        self.preview.crop_start_aspect = self.preview.aspect;
        self.preview.crop_drag.cancel();
    }

    pub fn finish_crop(&mut self) {
        self.set_cropping(false);
    }

    /// Leaves crop mode, putting back the crop and shape it was entered with. The crop steps made meanwhile leave the
    /// undo history, the steps before stay; if other edits were made meanwhile, only the crop goes back.
    pub fn cancel_crop(&mut self) {
        if !self.preview.is_cropping {
            return;
        }
        let Some(start) = self.preview.crop_start.clone() else { return };
        let aspect = self.preview.crop_start_aspect;
        let changed = !Arc::ptr_eq(&start, self.history.current());
        if changed {
            let only_crop = self.state().with_crop(start.crop()) == *start;
            let rewound = only_crop && self.history.rewind_to(&start);
            self.after_edit();
            if !rewound {
                self.commit_crop(start.crop());
            }
        }
        self.preview.aspect = aspect;
        self.set_cropping(false);
        if changed {
            self.status_message = Some("Crop changes cancelled.".into());
        }
    }

    /// The preview's close button: cancels the crop while cropping, otherwise hides the preview.
    pub fn close_preview(&mut self) {
        if self.preview.is_cropping {
            self.cancel_crop();
        } else {
            self.toggle_compact();
        }
    }

    pub fn close_preview_tip(&self) -> &'static str {
        if self.preview.is_cropping { "Cancel the crop changes" } else { "Hide the video preview (⇧⌘C)" }
    }

    pub fn reset_crop(&mut self) {
        if !self.has_crop() {
            return;
        }
        self.preview.aspect = 0;
        self.commit_crop(None);
    }

    /// Crops the output to `crop` as one undo step (None or the whole frame removes the crop).
    pub fn commit_crop(&mut self, crop: Option<CropRect>) {
        let before = self.history.current().clone();
        let next = self.state().with_crop(crop);
        self.commit(next);
        if Arc::ptr_eq(&before, self.history.current()) {
            return;
        }
        self.status_message = match (self.state().crop(), self.crop_size_text()) {
            (None, _) => Some("Crop removed: the whole frame is exported.".into()),
            (Some(_), Some(size)) => Some(format!("Exports crop to {size}.")),
            _ => self.status_message.take(),
        };
    }

    /// The edit or the analysis of its files changed: the timeline to play, the frame and the crop.
    pub(super) fn preview_on_project_changed(&mut self) {
        self.schedule_timeline_reload();
        self.update_frame();
    }

    /// Works out the frame and the picture shown. Leaves crop mode when there's nothing to crop any more.
    pub fn update_frame(&mut self) {
        // The frame the export would make: the same one the export crops, so the sizes shown are the sizes exported.
        let videos: Vec<Option<VideoStreamInfo>> = if self.is_source_mode() {
            vec![self.previewed_item().and_then(|i| i.info.as_ref()).and_then(|i| i.video.clone())]
        } else {
            self.state().timeline.clips().iter().map(|c| self.catalog.info(&c.source_id).and_then(|i| i.video.clone())).collect()
        };
        let frame = match videos.first() {
            Some(Some(first)) => {
                let mut list = vec![first];
                list.extend(videos.iter().skip(1).flatten());
                Some(OutputFrame::of(&list))
            }
            _ => None,
        };
        self.preview.frame_aspect = frame.map(|f| f.aspect).unwrap_or(0.0);
        self.preview.frame_pixels = frame.map(|f| (f.width, f.height)).unwrap_or((0, 0));
        self.update_picture();
        if self.preview.is_cropping && !self.can_crop() {
            self.set_cropping(false);
        }
    }

    /// The shape of the picture shown now: the previewed video's, or that of the clip under the playhead.
    pub fn update_picture(&mut self) {
        let picture = if self.is_source_mode() {
            self.previewed_item().and_then(|i| i.info.as_ref()).and_then(|i| i.video.clone())
        } else {
            self.state()
                .timeline
                .locate(self.timeline.playhead)
                .and_then(|(i, _)| self.catalog.info(&self.state().timeline.clips()[i].source_id))
                .and_then(|i| i.video.clone())
        };
        self.preview.picture_aspect = picture.map(|v| v.display_aspect()).unwrap_or(self.preview.frame_aspect);
    }

    /// Frees the player and its renderer (the window is closing).
    pub fn shut_down_preview(&mut self) {
        self.preview.renderer = None;
        if let Some(state) = self.app.render_state.get() {
            self.preview.texture.free(state);
        }
        self.preview.player = None;
    }
}
