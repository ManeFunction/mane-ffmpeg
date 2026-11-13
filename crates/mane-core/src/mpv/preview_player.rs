//! The editor's preview: plays the timeline (an EDL from `timeline_edl`) or a single source file, and keeps mpv's
//! protocol to itself: which tracks to play (the EDL's titled tracks, or a source's chosen audio stream), keeping the
//! position across reloads, audio-only compact mode, pacing rapid seeks so each one finishes (and shows its frame)
//! before the next (`SeekPacer`), ignoring position reports that still echo the old position while a seek is under
//! way or come from the file being replaced, forgetting a file that failed so the next load tries again, and live
//! audio-delay feedback. Events are delivered by `poll` on the thread that drives it.

use super::player::{MpvError, MpvEvent, MpvPlayer, Value};
use super::seek_pacer::{CHECK_INTERVAL, SeekPacer};
use super::timeline_edl::{AUDIO_TITLE, VIDEO_TITLE};
use crate::model::Time;
use crate::model::time::seconds;
use crate::persistence::app_log;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{Receiver, channel};
use std::sync::{Arc, Condvar, Mutex, OnceLock};
use std::time::Instant;

#[derive(Clone, Debug, PartialEq)]
pub enum PreviewEvent {
    /// The playback position, except reports that echo an old position during a seek or a load.
    Position(Time),
    Duration(Time),
    Paused(bool),
    EndReached,
    /// What was loaded can't be played (mpv's error); it counts as unloaded, so loading it again retries.
    LoadFailed(String),
    /// A seek (or a load) finished and its frame is shown (diagnostics; the pacer already acted on it).
    Restarted,
}

/// A timer that runs the pacer's check once, `CHECK_INTERVAL` after it was last armed.
struct CheckTimer {
    deadline: Mutex<(Option<Instant>, bool)>,
    cv: Condvar,
}

pub type Waker = Arc<dyn Fn() + Send + Sync>;

pub struct PreviewPlayer {
    mpv: Arc<MpvPlayer>,
    rx: Receiver<MpvEvent>,
    seeks: Arc<SeekPacer>,
    timer: Arc<CheckTimer>,
    /// True from a load until its file has loaded: position reports come from the file being replaced.
    loading: Arc<AtomicBool>,
    loaded: Option<String>,
    entry: Option<i64>,
    timeline: bool,
    source_audio_stream: Option<i32>,
    compact: bool,
}

impl PreviewPlayer {
    /// `headless`: no video or audio output (tests).
    pub fn create(hardware_decoding: bool, headless: bool, waker: Waker) -> Result<PreviewPlayer, MpvError> {
        let (tx, rx) = channel();
        // Restarts go straight from mpv's event thread to the pacer: the next seek shouldn't wait for a busy UI.
        let pacer_slot: Arc<OnceLock<Arc<SeekPacer>>> = Arc::new(OnceLock::new());
        let slot = pacer_slot.clone();
        let sink_tx = Mutex::new(tx);
        let mpv = Arc::new(MpvPlayer::create(
            hardware_decoding,
            headless,
            Box::new(move |ev| {
                if let MpvEvent::PlaybackRestarted = ev {
                    if let Some(p) = slot.get() {
                        p.restarted();
                    }
                    let _ = sink_tx.lock().unwrap().send(MpvEvent::PlaybackRestarted);
                    return;
                }
                if let MpvEvent::Log(level, text) = &ev {
                    // mpv's warnings and errors (unplayable segments, failed commands) explain a blank or silent preview.
                    app_log::write(&format!("mpv {level}: {text}"));
                    return;
                }
                let _ = sink_tx.lock().unwrap().send(ev);
                waker();
            }),
        )?);
        let timer = Arc::new(CheckTimer { deadline: Mutex::new((None, false)), cv: Condvar::new() });
        // Weak: the pacer lives in mpv's event sink, so strong references would keep mpv alive forever.
        let (seek_mpv, check_mpv, arm_timer) = (Arc::downgrade(&mpv), Arc::downgrade(&mpv), timer.clone());
        let seeks = Arc::new(SeekPacer::new(
            Box::new(move |t| {
                if let Some(m) = seek_mpv.upgrade() {
                    m.command_async(&["seek", &seconds(t), "absolute+exact"]);
                }
            }),
            Box::new(move || check_mpv.upgrade().is_some_and(|m| m.get_string("seeking").as_deref() == Some("yes"))),
            Box::new(move |armed| {
                let mut d = arm_timer.deadline.lock().unwrap();
                d.0 = armed.then(|| Instant::now() + CHECK_INTERVAL);
                arm_timer.cv.notify_all();
            }),
            None,
        ));
        let _ = pacer_slot.set(seeks.clone());
        let (t, p) = (timer.clone(), seeks.clone());
        std::thread::Builder::new()
            .name("seek check".into())
            .spawn(move || {
                let mut d = t.deadline.lock().unwrap();
                loop {
                    if d.1 {
                        break;
                    }
                    match d.0 {
                        None => d = t.cv.wait(d).unwrap(),
                        Some(at) => {
                            let now = Instant::now();
                            if now >= at {
                                d.0 = None;
                                drop(d);
                                p.check();
                                d = t.deadline.lock().unwrap();
                            } else {
                                d = t.cv.wait_timeout(d, at - now).unwrap().0;
                            }
                        }
                    }
                }
            })
            .map_err(|e| MpvError(e.to_string()))?;
        Ok(PreviewPlayer {
            mpv,
            rx,
            seeks,
            timer,
            loading: Arc::new(AtomicBool::new(false)),
            loaded: None,
            entry: None,
            timeline: false,
            source_audio_stream: None,
            compact: false,
        })
    }

    /// The mpv instance, for the video view's renderer (which must be dropped before this player).
    pub fn mpv(&self) -> &MpvPlayer {
        &self.mpv
    }

    /// True when the timeline is what's loaded (rather than a source or nothing).
    pub fn is_timeline_loaded(&self) -> bool {
        self.timeline && self.loaded.is_some()
    }

    pub fn compact(&self) -> bool {
        self.compact
    }

    /// Audio only: no video is decoded (compact mode).
    pub fn set_compact(&mut self, value: bool) {
        self.compact = value;
        self.apply_tracks();
    }

    pub fn set_volume(&self, v: f64) {
        self.mpv.set_double("volume", v);
    }

    pub fn set_paused(&self, paused: bool) {
        self.mpv.set_flag("pause", paused);
    }

    /// Plays the timeline EDL starting at `position`; nothing happens when it's already loaded.
    pub fn load_timeline(&mut self, url: &str, position: Time) {
        if self.is_timeline_loaded() && self.loaded.as_deref() == Some(url) {
            return;
        }
        self.timeline = true;
        self.source_audio_stream = None;
        self.load(url, position);
    }

    /// Plays one file from its start with the given audio stream (an absolute stream index).
    pub fn load_source(&mut self, path: &str, audio_stream_index: Option<i32>) {
        self.timeline = false;
        self.source_audio_stream = audio_stream_index;
        self.load(path, Time::ZERO);
    }

    pub fn unload(&mut self) {
        self.seeks.reset();
        if self.loaded.is_none() {
            return;
        }
        self.loaded = None;
        self.entry = None;
        self.mpv.command(&["stop"]);
    }

    fn load(&mut self, target: &str, start: Time) {
        // A seek under way is dropped with the file it was in: the next one goes at once.
        self.seeks.reset();
        self.loading.store(true, Ordering::Release);
        self.loaded = Some(target.to_string());
        // mpv carries track choices over to the next file, where "no" for both would leave it nothing to play;
        // every file starts from mpv's own choice, and the tracks are picked once it has loaded.
        self.mpv.set_string("vid", "auto");
        self.mpv.set_string("aid", "auto");
        self.mpv.set_string("start", &seconds(start));
        self.entry = self.mpv.load_file(target);
    }

    /// Seeks exactly, paced so each seek finishes and shows its frame before the next.
    pub fn seek(&self, t: Time) {
        if self.loaded.is_some() {
            self.seeks.seek(t);
        }
    }

    pub fn step_frame(&self, back: bool) {
        self.seeks.forget_last_seek();
        self.mpv.command_async(&[if back { "frame-back-step" } else { "frame-step" }]);
    }

    /// Shifts the audio by `delta` without reloading (while an offset is dragged).
    pub fn preview_audio_delay(&self, delta: Time) {
        self.mpv.set_double("audio-delay", delta.total_seconds());
    }

    /// Applies mpv's events; returns what the owner should hear about.
    pub fn poll(&mut self) -> Vec<PreviewEvent> {
        let mut out = Vec::new();
        while let Ok(ev) = self.rx.try_recv() {
            match ev {
                MpvEvent::Property(name, value) => match (name.as_str(), value) {
                    ("time-pos", Value::Double(s)) => {
                        let echo = self.loading.load(Ordering::Acquire) || self.seeks.may_echo();
                        if !echo {
                            out.push(PreviewEvent::Position(Time::from_seconds(s)));
                        }
                    }
                    ("duration", Value::Double(s)) => out.push(PreviewEvent::Duration(Time::from_seconds(s))),
                    ("pause", Value::Flag(p)) => out.push(PreviewEvent::Paused(p)),
                    ("eof-reached", Value::Flag(true)) => out.push(PreviewEvent::EndReached),
                    _ => {}
                },
                MpvEvent::PlaybackRestarted => out.push(PreviewEvent::Restarted),
                MpvEvent::FileLoaded(entry) => self.on_file_loaded(entry),
                MpvEvent::FileFailed(entry, error) => {
                    if let Some(e) = self.on_file_failed(entry, error) {
                        out.push(e);
                    }
                }
                _ => {}
            }
        }
        out
    }

    fn on_file_loaded(&mut self, entry: i64) {
        // A file replaced before it finished loading: the current one's events are still to come.
        if Some(entry) != self.entry {
            return;
        }
        // Events come in mpv's order, so every report from the replaced file arrived before this one.
        self.loading.store(false, Ordering::Release);
        self.mpv.set_double("audio-delay", 0.0);
        self.mpv.set_string("start", "none");
        self.apply_tracks();
    }

    fn on_file_failed(&mut self, entry: i64, error: String) -> Option<PreviewEvent> {
        if Some(entry) != self.entry {
            return None;
        }
        app_log::write(&format!("Preview couldn't play the {}: {error}", if self.timeline { "timeline" } else { "source" }));
        self.seeks.reset();
        self.loading.store(false, Ordering::Release);
        self.loaded = None;
        self.entry = None;
        self.set_paused(true);
        Some(PreviewEvent::LoadFailed(error))
    }

    /// The timeline's titled tracks, or a source's video and chosen audio stream; no video when compact.
    fn apply_tracks(&self) {
        // Until the file has loaded there are no tracks to pick from, and picking none would carry over to it;
        // on_file_loaded picks them.
        if self.loading.load(Ordering::Acquire) || self.loaded.is_none() {
            return;
        }
        let (video_title, audio_title) = if self.timeline { (Some(VIDEO_TITLE), Some(AUDIO_TITLE)) } else { (None, None) };
        let count = self.mpv.get_int("track-list/count").unwrap_or(0);
        let (mut vid, mut aid): (Option<String>, Option<String>) = (None, None);
        for i in 0..count {
            let kind = self.mpv.get_string(&format!("track-list/{i}/type"));
            let title = self.mpv.get_string(&format!("track-list/{i}/title"));
            let id = self.mpv.get_string(&format!("track-list/{i}/id"));
            if kind.as_deref() == Some("video") && vid.is_none() && (video_title.is_none() || title.as_deref() == video_title) {
                vid = id.clone();
            }
            if kind.as_deref() == Some("audio")
                && (audio_title.is_none() || title.as_deref() == audio_title)
                && (aid.is_none()
                    || self.source_audio_stream.is_some_and(|w| self.mpv.get_int(&format!("track-list/{i}/ff-index")) == Some(w as i64)))
            {
                aid = id;
            }
        }
        if self.compact {
            self.mpv.set_string("vid", "no");
        } else if let Some(v) = vid {
            self.mpv.set_string("vid", &v);
        }
        self.mpv.set_string("aid", aid.as_deref().unwrap_or("no"));
    }
}

impl Drop for PreviewPlayer {
    fn drop(&mut self) {
        // Seeks also go from mpv's event thread and the timer's; neither may touch mpv once it's gone.
        self.seeks.dispose();
        {
            let mut d = self.timer.deadline.lock().unwrap();
            d.1 = true;
            self.timer.cv.notify_all();
        }
        // The MpvPlayer is destroyed when `mpv` drops right after this: the pacer only holds weak references.
    }
}
