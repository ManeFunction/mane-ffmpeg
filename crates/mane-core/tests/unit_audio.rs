mod common;
use common::*;
use mane_core::editing::project_editor::{self as pe, AddRouting, ProbedFile};
use mane_core::editing::timeline_editor as te;
use mane_core::model::*;
use mane_core::mpv::timeline_edl;
use mane_core::persistence::project_file;
use std::collections::HashMap;

fn own(in_s: f64, out_s: f64, offset_s: f64) -> Clip {
    Clip { audio_offset: s(offset_s), ..clip("c", "s", in_s, out_s) }
}

fn replaced(in_s: f64, out_s: f64, anchor: f64, offset: f64, looped: bool) -> Clip {
    Clip {
        replaced_audio: Some(AudioReplacement { file_id: "f".into(), anchor: s(anchor), offset: s(offset), looped }),
        ..own(in_s, out_s, 0.0)
    }
}

fn layout(c: &Clip, end: f64) -> AudioLayout {
    AudioLayout::for_clip(c, c.r#in, c.duration(), s(end))
}

fn aac(index: i32, rate: i32, duration: Option<f64>) -> AudioStreamInfo {
    AudioStreamInfo { index, codec: "aac".into(), sample_rate: rate, channels: 2, duration: duration.map(s), ..Default::default() }
}

fn h264() -> VideoStreamInfo {
    VideoStreamInfo { index: 0, codec: "h264".into(), width: 640, height: 360, frame_rate: Rational::new(30, 1), ..Default::default() }
}

#[test]
fn looped_audio_that_does_not_wrap_is_one_read_not_silence() {
    let l = layout(&replaced(4.5, 5.5, 1.0, 0.0, true), 3.0);
    assert_eq!(l.spans(), vec![AudioSpan { at: s(0.0), length: s(1.0), read_from: Some(s(0.5)) }]);
    assert!(l.edges(s(0.0), s(1.0)).is_empty());
}

#[test]
fn too_short_files_and_loop_off_play_once() {
    assert!(matches!(layout(&replaced(0.0, 2.0, 0.0, 0.0, true), 0.05), AudioLayout::Placed { .. }));
    assert!(matches!(layout(&replaced(0.0, 2.0, 0.0, 0.0, false), 3.0), AudioLayout::Placed { .. }));
}

#[test]
fn heard_audio_is_the_replacement_or_the_clips_own_once_analyzed() {
    let video = MediaInfo { duration: s(10.0), audio_streams: vec![aac(1, 48000, None)], ..Default::default() };
    let tone = MediaInfo { duration: s(3.0), audio_streams: vec![aac(0, 44100, Some(2.5))], ..Default::default() };
    let state = ProjectState::new(vec![source("s", "/m/a.mp4")], Timeline::empty(), vec![source("f", "/m/t.m4a")]);
    let media: HashMap<String, MediaInfo> = [("s".into(), video.clone()), ("f".into(), tone)].into();
    let get = |id: &str| media.get(id);
    assert_eq!(state.heard_audio(&own(0.0, 1.0, 0.0), get).unwrap().file.id, "s");
    let r = state.heard_audio(&replaced(0.0, 1.0, 0.0, 0.0, false), get).unwrap();
    assert_eq!((r.file.id.as_str(), r.end()), ("f", s(2.5)));
    let only_video: HashMap<String, MediaInfo> = [("s".into(), video.clone())].into();
    assert!(state.heard_audio(&replaced(0.0, 1.0, 0.0, 0.0, false), |id| only_video.get(id)).is_none());
    assert!(state.heard_audio(&Clip { source_id: "gone".into(), ..own(0.0, 1.0, 0.0) }, get).is_none());
    let silent: HashMap<String, MediaInfo> = [("s".into(), MediaInfo { audio_streams: vec![], ..video })].into();
    assert!(state.heard_audio(&own(0.0, 1.0, 0.0), |id| silent.get(id)).is_none());
}

#[test]
fn edl_builds_two_streams_with_slip_and_silence() {
    let info = MediaInfo { duration: s(10.0), video: Some(h264()), audio_streams: vec![aac(1, 48000, None)], ..Default::default() };
    let state = ProjectState::new(
        vec![source("s", "/m/a,b.mp4")],
        Timeline::new(vec![
            Clip { audio_offset: ms(120.0), ..clip("1", "s", 2.0, 5.0) },
            Clip { audio_offset: ms(120.0), ..clip("2", "s", 0.0, 2.0) },
        ]),
        vec![],
    );
    let edl = timeline_edl::build(&state, &[("s".to_string(), info)].into(), "/c/silence.mka").unwrap();
    assert_eq!(
        edl,
        "edl://!new_stream;!track_meta,title=mane-video;%10%/m/a,b.mp4,2.000000,3.000000;%10%/m/a,b.mp4,0.000000,2.000000;\
         !new_stream;!track_meta,title=mane-audio;%10%/m/a,b.mp4,1.880000,3.000000;\
         %14%/c/silence.mka,0.000000,0.120000;%10%/m/a,b.mp4,0.000000,1.880000"
    );
}

#[test]
fn edl_segments_use_file_timestamps_when_the_container_starts_late() {
    let info = MediaInfo {
        duration: s(10.0),
        start_time: s(1.4),
        video: Some(h264()),
        audio_streams: vec![aac(1, 48000, None)],
        ..Default::default()
    };
    let state = ProjectState::new(
        vec![source("s", "/m/a.ts")],
        Timeline::new(vec![Clip { audio_offset: ms(-100.0), ..clip("1", "s", 2.0, 5.0) }]),
        vec![],
    );
    let edl = timeline_edl::build(&state, &[("s".to_string(), info)].into(), "/c/silence.mka").unwrap();
    assert_eq!(
        edl,
        "edl://!new_stream;!track_meta,title=mane-video;%7%/m/a.ts,3.400000,3.000000;!new_stream;!track_meta,title=mane-audio;%7%/m/a.ts,3.500000,3.000000"
    );
}

#[test]
fn edl_byte_counts_use_utf8() {
    let info = MediaInfo { duration: s(10.0), video: Some(h264()), ..Default::default() };
    let state = ProjectState::new(vec![source("s", "/м.mp4")], Timeline::new(vec![clip("1", "s", 0.0, 1.0)]), vec![]);
    let edl = timeline_edl::build(&state, &[("s".to_string(), info)].into(), "/c/silence.mka").unwrap();
    assert!(edl.contains("%7%/м.mp4,"), "{edl}");
}

#[test]
fn loops_from_the_slipped_phase() {
    for (vs, len, slip, period, phase, wraps) in [
        (1.0, 1.0, 1.0, 3.0, 0.0, false),
        (1.0, 7.0, 1.0, 3.0, 0.0, true),
        (1.0, 2.0, 1.5, 3.0, 2.5, true),
        (4.0, 1.0, 1.0, 3.0, 0.0, false),
        (1.0, 1.5, -1.0, 3.0, 2.0, true),
    ] {
        let l = AudioLoop::compute(s(vs), s(len), s(slip), s(period));
        assert!((l.phase.total_seconds() - phase).abs() < 1e-6);
        assert_eq!(l.wraps(), wraps);
        assert_eq!(l.pieces().iter().map(|p| p.1).sum::<Time>(), s(len));
    }
}

#[test]
fn pieces_run_to_the_end_then_whole_files_then_the_rest() {
    let p = AudioLoop { phase: s(2.5), period: s(3.0), length: s(7.0) }.pieces();
    assert_eq!(p, vec![(s(2.5), s(0.5)), (s(0.0), s(3.0)), (s(0.0), s(3.0)), (s(0.0), s(0.5))]);
    assert!(!AudioLoop::can_loop(ms(50.0)));
}

fn c(id: &str, in_s: f64, out_s: f64, offset_ms: f64) -> Clip {
    Clip { audio_offset: ms(offset_ms), ..clip(id, "s", in_s, out_s) }
}

#[test]
fn replacing_starts_the_file_at_the_in_point_and_keeps_the_own_offset() {
    let t = te::replace_audio(&Timeline::new(vec![c("a", 2.0, 8.0, 120.0)]), "a", "f");
    let cl = &t.clips()[0];
    assert_eq!(cl.replaced_audio, Some(AudioReplacement { file_id: "f".into(), anchor: s(2.0), ..Default::default() }));
    assert_eq!(cl.audio_slip(), s(2.0));
    assert_eq!(cl.audio_in(), Time::ZERO);
    assert_eq!(cl.active_audio_offset(), Time::ZERO);
    let nudged = te::nudge_audio_offset(&t, &ids(&["a"]), s(90.0));
    assert_eq!(nudged.clips()[0].replaced_audio.as_ref().unwrap().offset, s(90.0));
    assert_eq!(nudged.clips()[0].audio_offset, ms(120.0));
    let restored = te::restore_original_audio(&nudged, &ids(&["a"]));
    assert!(restored.clips()[0].replaced_audio.is_none());
    assert_eq!(restored.clips()[0].active_audio_offset(), ms(120.0));
}

#[test]
fn limits_differ_for_own_and_replaced_audio() {
    let t = te::replace_audio(&Timeline::new(vec![c("a", 0.0, 5.0, 0.0), c("b", 5.0, 9.0, 0.0)]), "b", "f");
    let t = te::set_audio_offset(&t, &ids(&["a", "b"]), s(30.0 * 3600.0));
    assert_eq!(t.clips()[0].audio_offset, te::MAX_AUDIO_OFFSET);
    assert_eq!(t.clips()[1].replaced_audio.as_ref().unwrap().offset, te::MAX_REPLACED_AUDIO_OFFSET);
}

#[test]
fn a_new_drop_resets_the_offset_but_keeps_the_loop() {
    let t = te::replace_audio(&Timeline::new(vec![c("a", 1.0, 8.0, 0.0)]), "a", "f");
    let t = te::set_audio_loop(&te::nudge_audio_offset(&t, &ids(&["a"]), s(1.0)), &ids(&["a"]), true);
    let again = te::replace_audio(&t, "a", "g").clips()[0].replaced_audio.clone().unwrap();
    assert_eq!((again.file_id.as_str(), again.offset, again.looped), ("g", Time::ZERO, true));
}

#[test]
fn splitting_continues_the_sound_on_both_halves() {
    let t = te::replace_audio(&Timeline::new(vec![c("a", 1.0, 8.0, 0.0)]), "a", "f");
    let (split, _) = te::split(&t, s(3.0), &|_| None);
    assert!(split.clips().iter().all(|c| c.replaced_audio.as_ref().unwrap().anchor == s(1.0)));
    assert_eq!(split.clips()[1].audio_in(), s(3.0));
}

#[test]
fn source_wide_offsets_leave_replacements_alone_and_removed_files_restore_clips() {
    let t = te::replace_audio(&Timeline::new(vec![c("a", 0.0, 5.0, 0.0), c("b", 5.0, 9.0, 0.0)]), "b", "f");
    let t = te::set_replaced_audio_offset_for_file(&te::set_audio_offset_for_source(&t, "s", s(0.2)), "f", s(4.0));
    assert!(t.clips().iter().all(|c| c.audio_offset == s(0.2)));
    assert_eq!(t.clips()[1].replaced_audio.as_ref().unwrap().offset, s(4.0));
    assert!(te::remove_audio_file(&t, "f").clips().iter().all(|c| c.replaced_audio.is_none()));
}

#[test]
fn project_files_keep_audio_files_and_replacements() {
    let dir = tempfile::tempdir().unwrap();
    let song = dir.path().join("audio").join("song.m4a");
    std::fs::create_dir_all(song.parent().unwrap()).unwrap();
    std::fs::write(&song, "x").unwrap();
    let cl = Clip {
        replaced_audio: Some(AudioReplacement { file_id: "f".into(), anchor: s(1.0), offset: s(-2.5), looped: true }),
        ..c("c", 1.0, 4.0, 0.0)
    };
    let state = ProjectState::new(
        vec![source("s", "/m/a.mp4")],
        Timeline::new(vec![cl]),
        vec![SourceMedia { audio_stream_index: 1, ..source("f", &song.to_string_lossy()) }],
    );
    let path = dir.path().join("p.mfvp");
    let json = project_file::serialize(&Project { state: state.clone(), ..Default::default() }, &path);
    assert!(json.contains("\"version\": 2"));
    assert!(json.contains("\"relativePath\": \"audio/song.m4a\""));
    assert_eq!(project_file::deserialize(&json, &path).unwrap().state, state);
    let plain = ProjectState::new(state.sources.clone(), Timeline::new(vec![c("c", 1.0, 4.0, 0.0)]), vec![]);
    assert!(project_file::serialize(&Project { state: plain, ..Default::default() }, &path).contains("\"version\": 1"));
    let orphan = project_file::deserialize(&json.replace("\"id\": \"f\"", "\"id\": \"g\""), &path).unwrap();
    assert!(orphan.state.timeline.clips()[0].replaced_audio.is_none());
    assert!(project_file::deserialize(&json.replace("\"version\": 2", "\"version\": 4"), &path).is_err());
}

fn video_media() -> MediaInfo {
    MediaInfo { duration: s(10.0), video: Some(h264()), audio_streams: vec![aac(1, 48000, None)], ..Default::default() }
}

fn tone_media() -> MediaInfo {
    MediaInfo { duration: s(3.0), audio_streams: vec![aac(0, 44100, Some(3.0))], ..Default::default() }
}

fn replaced_state(cl: Clip) -> ProjectState {
    ProjectState::new(vec![source("s", "/m/a.mp4")], Timeline::new(vec![cl]), vec![source("f", "/m/t.m4a")])
}

#[test]
fn edl_plays_the_replacement_slipped_or_looped() {
    let media: HashMap<String, MediaInfo> = [("s".into(), video_media()), ("f".into(), tone_media())].into();
    let later = Clip {
        replaced_audio: Some(AudioReplacement { file_id: "f".into(), anchor: s(1.0), offset: s(0.5), looped: false }),
        ..c("c", 1.0, 5.0, 0.0)
    };
    assert!(timeline_edl::build(&replaced_state(later.clone()), &media, "/c/silence.mka").unwrap().ends_with(
        "!new_stream;!track_meta,title=mane-audio;%14%/c/silence.mka,0.000000,0.500000;%8%/m/t.m4a,0.000000,3.000000;%14%/c/silence.mka,0.000000,0.500000"
    ));
    let mut looped = later.clone();
    looped.replaced_audio.as_mut().unwrap().looped = true;
    assert!(timeline_edl::build(&replaced_state(looped), &media, "/c/silence.mka").unwrap().ends_with(
        "!new_stream;!track_meta,title=mane-audio;%8%/m/t.m4a,2.500000,0.500000;%8%/m/t.m4a,0.000000,3.000000;%8%/m/t.m4a,0.000000,0.500000"
    ));
    let half = Clip {
        replaced_audio: Some(AudioReplacement { file_id: "f".into(), anchor: s(1.0), offset: Time::ZERO, looped: true }),
        ..c("c", 4.5, 5.5, 0.0)
    };
    assert!(
        timeline_edl::build(&replaced_state(half), &media, "/c/silence.mka")
            .unwrap()
            .ends_with("!new_stream;!track_meta,title=mane-audio;%8%/m/t.m4a,0.500000,1.000000")
    );
    let only_video: HashMap<String, MediaInfo> = [("s".into(), video_media())].into();
    assert!(!timeline_edl::build(&replaced_state(later), &only_video, "/c/silence.mka").unwrap().contains("/m/t.m4a"));
}

fn plays(id: &str, file: Option<&str>) -> Clip {
    Clip {
        id: id.into(),
        replaced_audio: file.map(|f| AudioReplacement { file_id: f.into(), ..Default::default() }),
        ..Clip::create("v", s(0.0), s(2.0))
    }
}

fn pstate(clips: Vec<Clip>) -> ProjectState {
    ProjectState::new(vec![source("v", "/m/a.mp4")], Timeline::new(clips), vec![source("f", "/m/song.m4a")])
}

#[test]
fn a_path_is_listed_once_and_keeps_its_id() {
    let (state, id) = pe::add_file(&pstate(vec![]), FileKind::Audio, source("new", "/m/song.m4a"));
    assert_eq!(id, "f");
    assert_eq!(state.audio_files.len(), 1);
    let (state, id) = pe::add_file(&state, FileKind::Video, source("w", "/m/b.mp4"));
    assert_eq!((id.as_str(), state.sources.len()), ("w", 2));
}

#[test]
fn removing_an_audio_file_gives_its_clips_their_own_sound_back() {
    let state = pe::remove_file(&pstate(vec![plays("a", Some("f")), plays("b", None)]), FileKind::Audio, "f");
    assert!(state.audio_files.is_empty());
    assert!(state.timeline.clips().iter().all(|c| c.replaced_audio.is_none()));
    assert_eq!(state.timeline.clips().len(), 2);
}

#[test]
fn removing_a_video_removes_its_clips() {
    let before = pstate(vec![plays("a", Some("f"))]);
    assert_eq!(pe::clips_using(&before, FileKind::Video, "v"), 1);
    assert_eq!(pe::clips_using(&before, FileKind::Audio, "f"), 1);
    let state = pe::remove_file(&before, FileKind::Video, "v");
    assert!(state.sources.is_empty());
    assert!(state.timeline.clips().is_empty());
    assert_eq!(state.audio_files.len(), 1);
}

#[test]
fn replacing_a_clips_audio_needs_both_the_clip_and_the_file() {
    let state = pstate(vec![plays("a", None)]);
    assert_eq!(pe::replace_clip_audio(&state, "a", "f").timeline.clips()[0].replaced_audio.as_ref().unwrap().file_id, "f");
    assert_eq!(pe::replace_clip_audio(&state, "a", "gone"), state);
    assert_eq!(pe::replace_clip_audio(&state, "gone", "f"), state);
}

#[test]
fn appending_needs_a_listed_video() {
    assert_eq!(pe::append_clip(&pstate(vec![]), "v", s(1.0), s(3.0)).timeline.clips().len(), 1);
    assert!(pe::append_clip(&pstate(vec![]), "gone", s(1.0), s(3.0)).timeline.clips().is_empty());
}

fn dropped() -> Vec<ProbedFile> {
    let movie = MediaInfo { duration: s(5.0), video: Some(h264()), audio_streams: vec![aac(1, 48000, None)], ..Default::default() };
    let tune = MediaInfo {
        duration: s(3.0),
        audio_streams: vec![AudioStreamInfo { codec: "mp3".into(), ..aac(0, 44100, None) }],
        ..Default::default()
    };
    vec![
        ProbedFile { path: "/in/movie.mp4".into(), info: Some(movie), error: None },
        ProbedFile { path: "/in/tune.mp3".into(), info: Some(tune), error: None },
        ProbedFile { path: "/in/notes.txt".into(), info: Some(MediaInfo::default()), error: None },
        ProbedFile { path: "/in/broken.mov".into(), info: None, error: Some("Invalid data".into()) },
    ]
}

fn new_file(path: &str) -> SourceMedia {
    source(&format!("id:{path}"), path)
}

#[test]
fn added_files_go_where_their_content_belongs() {
    let added = pe::add_probed(&pstate(vec![]), &dropped(), AddRouting::ByContent, &new_file);
    assert_eq!(added.videos, ["id:/in/movie.mp4"]);
    assert_eq!(added.audio_files, ["id:/in/tune.mp3"]);
    assert_eq!(added.skipped, ["notes.txt: no video or audio stream", "broken.mov: Invalid data"]);
    assert_eq!(added.state.timeline.clips().len(), 1);
    assert_eq!(added.state.timeline.clips()[0].duration(), s(5.0));
    assert_eq!(added.state.audio_files.len(), 2);
}

#[test]
fn added_as_audio_every_file_with_sound_is_an_audio_file() {
    let added = pe::add_probed(&pstate(vec![]), &dropped(), AddRouting::AllAsAudio, &new_file);
    assert!(added.videos.is_empty());
    assert_eq!(added.audio_files, ["id:/in/movie.mp4", "id:/in/tune.mp3"]);
    assert_eq!(added.skipped, ["notes.txt: no audio stream", "broken.mov: Invalid data"]);
    assert!(added.state.timeline.clips().is_empty());
}

#[test]
fn orphans_are_dropped_or_fall_back_to_their_own_sound() {
    let orphan = Clip { source_id: "gone".into(), ..plays("x", None) };
    let state = pe::drop_orphans(&pstate(vec![plays("a", Some("gone")), plays("b", Some("f")), orphan]));
    let order: Vec<_> = state.timeline.clips().iter().map(|c| c.id.as_str()).collect();
    assert_eq!(order, ["a", "b"]);
    assert!(state.timeline.clips()[0].replaced_audio.is_none());
    assert_eq!(state.timeline.clips()[1].replaced_audio.as_ref().unwrap().file_id, "f");
}
