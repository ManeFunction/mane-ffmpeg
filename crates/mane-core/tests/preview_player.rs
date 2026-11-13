//! The preview player against a headless libmpv: tracks, position across reloads, compact mode, seeks. Skipped
//! (passes) without ffmpeg or libmpv.
mod fixture;
use fixture::wait_until;
use mane_core::model::*;
use mane_core::mpv::preview_player::{PreviewEvent, PreviewPlayer};
use mane_core::mpv::{library, timeline_edl};
use std::sync::Arc;
use std::time::{Duration, Instant};

fn ready() -> Option<&'static fixture::Fixture> {
    let f = fixture::get()?;
    if library::relaunch_environment(None).is_some() || !library::try_load(None) {
        return None;
    }
    Some(f)
}

fn near(v: Option<f64>, expected: f64) -> bool {
    v.is_some_and(|s| (s - expected).abs() < 0.05)
}

fn player() -> PreviewPlayer {
    PreviewPlayer::create(false, true, Arc::new(|| {})).unwrap()
}

fn url(f: &fixture::Fixture, path: &str, clips: &[(f64, f64)]) -> String {
    let info = f.probe(&f.sync30);
    let state = ProjectState::new(
        vec![SourceMedia { id: "s".into(), path: path.into(), ..Default::default() }],
        Timeline::new(clips.iter().map(|(a, b)| Clip::create("s", Time::from_seconds(*a), Time::from_seconds(*b))).collect()),
        vec![],
    );
    timeline_edl::build(&state, &[("s".to_string(), info)].into(), "/nonexistent/silence.mka").unwrap()
}

/// Waits, delivering the player's events as the UI would.
fn wait(p: &mut PreviewPlayer, mut done: impl FnMut(&PreviewPlayer) -> bool, what: &str) {
    wait_until(
        || {
            p.poll();
            done(p)
        },
        what,
    );
}

#[test]
fn timeline_plays_its_titled_tracks_and_keeps_the_position_across_reloads() {
    let Some(f) = ready() else { return };
    let mut p = player();
    p.load_timeline(&url(f, &f.sync30, &[(1.0, 3.0), (5.0, 8.0)]), Time::from_seconds(2.5));
    wait(
        &mut p,
        |p| p.mpv().get_string("current-tracks/audio/title").as_deref() == Some(timeline_edl::AUDIO_TITLE),
        "the timeline audio track",
    );
    assert_eq!(p.mpv().get_string("current-tracks/video/title").as_deref(), Some(timeline_edl::VIDEO_TITLE));
    wait(&mut p, |p| near(p.mpv().get_double("time-pos"), 2.5), "the start position");
    assert!(p.is_timeline_loaded());
    p.load_timeline(&url(f, &f.sync30, &[(1.0, 3.0), (6.0, 8.0)]), Time::from_seconds(3.25));
    wait(&mut p, |p| near(p.mpv().get_double("time-pos"), 3.25), "the position after reloading");
    p.set_compact(true);
    wait(&mut p, |p| p.mpv().get_string("vid").as_deref() == Some("no"), "video off in compact mode");
    assert_eq!(p.mpv().get_string("current-tracks/audio/title").as_deref(), Some(timeline_edl::AUDIO_TITLE));
}

#[test]
fn compact_mode_switched_before_the_timeline_loads_still_lets_it_play() {
    let Some(f) = ready() else { return };
    let mut p = player();
    p.set_compact(true);
    p.load_timeline(&url(f, &f.sync30, &[(1.0, 3.0), (5.0, 8.0)]), Time::from_seconds(1.0));
    wait(
        &mut p,
        |p| p.mpv().get_string("current-tracks/audio/title").as_deref() == Some(timeline_edl::AUDIO_TITLE),
        "the timeline audio track",
    );
    assert_eq!(p.mpv().get_string("vid").as_deref(), Some("no"));
    p.set_compact(false);
    wait(
        &mut p,
        |p| p.mpv().get_string("current-tracks/video/title").as_deref() == Some(timeline_edl::VIDEO_TITLE),
        "the timeline video track",
    );
}

#[test]
fn a_timeline_that_fails_to_load_is_tried_again_next_time() {
    let Some(f) = ready() else { return };
    let mut p = player();
    let missing = url(f, &f.directory.join("missing.mp4").to_string_lossy(), &[(1.0, 3.0)]);
    let mut failures = 0;
    let count = |p: &mut PreviewPlayer| p.poll().iter().filter(|e| matches!(e, PreviewEvent::LoadFailed(_))).count();
    p.load_timeline(&missing, Time::ZERO);
    wait_until(
        || {
            failures += count(&mut p);
            failures == 1
        },
        "the load failing",
    );
    assert!(!p.is_timeline_loaded());
    p.load_timeline(&missing, Time::ZERO);
    wait_until(
        || {
            failures += count(&mut p);
            failures == 2
        },
        "the second try failing",
    );
    p.load_timeline(&url(f, &f.sync30, &[(1.0, 3.0)]), Time::ZERO);
    wait(
        &mut p,
        |p| p.mpv().get_string("current-tracks/audio/title").as_deref() == Some(timeline_edl::AUDIO_TITLE),
        "the playable timeline",
    );
    assert!(p.is_timeline_loaded());
}

#[test]
fn positions_still_queued_from_the_replaced_timeline_are_not_reported() {
    let Some(f) = ready() else { return };
    let mut p = player();
    p.load_timeline(&url(f, &f.sync30, &[(0.0, 10.0)]), Time::from_seconds(6.0));
    p.set_paused(false);
    // Events stay queued until poll, like on a busy UI thread.
    wait_until(|| p.mpv().get_double("time-pos").is_some_and(|t| t > 6.2), "the first timeline playing");
    let positions = |events: Vec<PreviewEvent>| -> Vec<f64> {
        events.into_iter().filter_map(|e| if let PreviewEvent::Position(t) = e { Some(t.total_seconds()) } else { None }).collect()
    };
    assert!(positions(p.poll()).iter().any(|t| *t > 6.0));
    let queued_from = p.mpv().get_double("time-pos").unwrap();
    wait_until(|| p.mpv().get_double("time-pos").is_some_and(|t| t > queued_from + 0.2), "positions queuing up");
    p.load_timeline(&url(f, &f.sync30, &[(0.0, 4.0)]), Time::from_seconds(1.0));
    let mut reported = Vec::new();
    wait_until(
        || {
            reported.extend(positions(p.poll()));
            !reported.is_empty()
        },
        "a position from the second timeline",
    );
    assert!(reported.iter().all(|t| (0.9..=3.0).contains(t)), "{reported:?}");
}

#[test]
fn source_preview_plays_the_chosen_audio_stream_and_rapid_seeks_land_on_the_last() {
    let Some(f) = ready() else { return };
    let path = f.directory.join("two-audio.mkv").to_string_lossy().into_owned();
    assert!(
        std::process::Command::new(&f.tools.ffmpeg)
            .args([
                "-nostdin",
                "-hide_banner",
                "-loglevel",
                "error",
                "-y",
                "-i",
                &f.sync30,
                "-map",
                "0:v",
                "-map",
                "0:a",
                "-map",
                "0:a",
                "-c",
                "copy",
                &path
            ])
            .status()
            .unwrap()
            .success()
    );
    let mut p = player();
    p.load_source(&path, Some(2));
    wait(&mut p, |p| p.mpv().get_int("current-tracks/audio/ff-index") == Some(2), "the second audio stream");
    assert!(!p.is_timeline_loaded());
    for s in [1.0, 2.0, 3.0, 4.5] {
        p.seek(Time::from_seconds(s));
    }
    wait(&mut p, |p| near(p.mpv().get_double("time-pos"), 4.5), "the last seek");
}

#[test]
fn scrubbing_a_long_gop_timeline_shows_frames_while_the_pointer_still_moves() {
    let Some(f) = ready() else { return };
    let path = f.directory.join("long-gop.mp4").to_string_lossy().into_owned();
    assert!(
        std::process::Command::new(&f.tools.ffmpeg)
            .args([
                "-nostdin",
                "-hide_banner",
                "-loglevel",
                "error",
                "-y",
                "-f",
                "lavfi",
                "-i",
                "testsrc2=size=1280x720:rate=30",
                "-f",
                "lavfi",
                "-i",
                "sine"
            ])
            .args(["-t", "10", "-c:v", "libx264", "-preset", "veryfast", "-g", "300", "-pix_fmt", "yuv420p", "-c:a", "aac", &path])
            .status()
            .unwrap()
            .success()
    );
    let state = ProjectState::new(
        vec![SourceMedia { id: "s".into(), path: path.clone(), ..Default::default() }],
        Timeline::new(vec![Clip::create("s", Time::from_seconds(0.5), Time::from_seconds(9.5))]),
        vec![],
    );
    let u = timeline_edl::build(&state, &[("s".to_string(), f.probe(&path))].into(), "/nonexistent/silence.mka").unwrap();
    let mut p = player();
    p.load_timeline(&u, Time::ZERO);
    wait(&mut p, |p| p.mpv().get_string("current-tracks/audio/title").as_deref() == Some(timeline_edl::AUDIO_TITLE), "the timeline");
    wait(&mut p, |p| near(p.mpv().get_double("time-pos"), 0.0), "the first frame");
    // Frames shown while dragging: distinct settled positions in the middle of the drag.
    let mut shown: Vec<f64> = Vec::new();
    let clock = Instant::now();
    while shown.iter().filter(|t| **t > 1.2 && **t < 8.0).count() < 3 && clock.elapsed() < Duration::from_secs(20) {
        let t = clock.elapsed().as_secs_f64() * 3.5 % 14.0;
        p.seek(Time::from_seconds(1.0 + if t < 7.0 { t } else { 14.0 - t }));
        let restarts = p.poll().into_iter().filter(|e| *e == PreviewEvent::Restarted).count();
        if restarts > 0
            && let Some(pos) = p.mpv().get_double("time-pos")
        {
            shown.push(pos);
        }
        std::thread::sleep(Duration::from_millis(8));
    }
    p.seek(Time::from_seconds(8.5));
    assert!(shown.iter().filter(|t| **t > 1.2 && **t < 8.0).count() >= 3, "frames shown during the drag: {shown:?}");
    wait(&mut p, |p| near(p.mpv().get_double("time-pos"), 8.5), "the frame under the released pointer");
}
