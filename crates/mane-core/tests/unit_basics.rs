mod common;
use common::*;
use mane_core::editing::timeline_editor as te;
use mane_core::editing::{CutGrid, UndoStack};
use mane_core::ffmpeg::export_planner::{self, ExportJob};
use mane_core::ffmpeg::preset_analysis::PresetAnalysis;
use mane_core::ffmpeg::preset_arguments::{PresetArguments, tokenize};
use mane_core::model::time::{self, Time};
use mane_core::model::*;
use std::collections::HashMap;
use std::sync::Arc;

#[test]
fn seconds_floors_to_microseconds() {
    for (secs, expected) in [(12.3456789, "12.345678"), (0.0, "0.000000"), (3600.5, "3600.500000"), (-0.12, "-0.120000")] {
        assert_eq!(time::seconds(Time((secs * 10_000_000f64).round() as i64)), expected);
    }
}

#[test]
fn try_parse_accepts_common_forms() {
    for (text, secs) in [("1:02:03.5", 3723.5), ("02:03,25", 123.25), ("42", 42.0), ("-0.12", -0.12)] {
        let t = time::try_parse(text).unwrap();
        assert!((t.total_seconds() - secs).abs() < 1e-6, "{text}");
    }
}

#[test]
fn snap_to_frame_lands_on_frame_starts_that_ffmpeg_keeps() {
    let ntsc = Rational::new(30000, 1001);
    let snapped = time::snap_to_frame(s(10.02), ntsc, Time::ZERO);
    let exact = 300.0 * 1001.0 / 30000.0;
    assert!(snapped.total_seconds() >= exact);
    assert!(time::seconds(snapped).parse::<f64>().unwrap() <= exact + 1e-6);
}

#[test]
fn timespan_strings_round_trip_like_dotnet() {
    for (ticks, text) in
        [(0, "00:00:00"), (15_000_000, "00:00:01.5000000"), (-1234, "-00:00:00.0001234"), (Time::from_secs(90061).0, "1.01:01:01")]
    {
        assert_eq!(time::timespan_string(Time(ticks)), text);
        assert_eq!(time::parse_timespan(text), Some(Time(ticks)));
    }
    assert_eq!(time::parse_timespan("00:00:01.5"), Some(Time(15_000_000)));
}

#[test]
fn evaluates_simple_arithmetic() {
    for (text, expected) in [
        ("0-700", -700.0),
        (" -700 ", -700.0),
        ("−120", -120.0),
        ("100 + 20.5", 120.5),
        ("1,5*2", 3.0),
        ("2*(40+1)", 82.0),
        ("1000/3*3", 1000.0),
        ("10-2-3", 5.0),
        ("-(5-8)", 3.0),
        ("--4", 4.0),
        ("3 × 4 ÷ 2", 6.0),
    ] {
        let v = arithmetic::try_evaluate(text).unwrap_or_else(|| panic!("{text}"));
        assert!((v - expected).abs() < 1e-9, "{text}");
    }
}

#[test]
fn refuses_what_is_not_a_finite_number() {
    for text in ["", "0-", "abc", "(1+2", "1+2)", "1..2", "5/0", "12 ms"] {
        assert!(arithmetic::try_evaluate(text).is_none(), "{text}");
    }
}

fn v(items: &[&str]) -> Vec<String> {
    items.iter().map(|s| s.to_string()).collect()
}

#[test]
fn detects_codecs_and_copy() {
    let a = PresetArguments::parse("-c:v libx265 -crf 28 -preset slow -c:a copy").unwrap();
    assert_eq!(a.video_codec.as_deref(), Some("libx265"));
    assert!(a.audio_copy());
    assert!(!a.video_copy());
    assert_eq!(a.output_tokens, v(&["-c:v", "libx265", "-crf", "28", "-preset", "slow", "-c:a", "copy"]));
}

#[test]
fn generic_codec_applies_to_both_and_is_narrowed_on_fallback() {
    let a = PresetArguments::parse("-c copy").unwrap();
    assert!(a.video_copy() && a.audio_copy());
    assert_eq!(a.with_audio_codec_replaced(&v(&["-c:a", "aac"])), v(&["-c:v", "copy", "-c:a", "aac"]));
}

#[test]
fn audio_codec_is_replaced_by_fallback() {
    let a = PresetArguments::parse("-vcodec libx264 -acodec copy -movflags +faststart").unwrap();
    assert_eq!(
        a.with_audio_codec_replaced(&v(&["-c:a", "aac", "-b:a", "256k"])),
        v(&["-vcodec", "libx264", "-movflags", "+faststart", "-c:a", "aac", "-b:a", "256k"])
    );
    assert!(a.sets_mov_flags);
}

#[test]
fn filters_and_managed_options_are_pulled_out() {
    let a = PresetArguments::parse("-y -i in.mp4 -vf \"scale=1280:-2\" -af 'volume=2' -map 0 -c:v libx264 -loglevel quiet").unwrap();
    assert_eq!(a.video_filter.as_deref(), Some("scale=1280:-2"));
    assert_eq!(a.audio_filter.as_deref(), Some("volume=2"));
    assert_eq!(a.output_tokens, v(&["-c:v", "libx264"]));
    assert_eq!(a.warnings.len(), 3);
}

#[test]
fn tokenizer_handles_quotes_and_escapes() {
    assert_eq!(
        tokenize("-metadata \"title=My \\\"cut\\\"\" -x 'a b' c\\\\d").unwrap(),
        v(&["-metadata", "title=My \"cut\"", "-x", "a b", "c\\d"])
    );
    assert!(tokenize("-vf 'scale").is_err());
}

#[test]
fn stray_values_are_refused() {
    let e = PresetArguments::parse("-c:v libx264 -f mpegts tcp://127.0.0.1:9").unwrap_err();
    assert!(e.contains("tcp://127.0.0.1:9"));
    assert!(PresetArguments::parse("-c copy /tmp/side.mkv").is_err());
    assert_eq!(PresetArguments::parse("-c:v libx264 -bf -1").unwrap().output_tokens, v(&["-c:v", "libx264", "-bf", "-1"]));
}

#[test]
fn unusable_presets_are_errors() {
    for (args, expected) in [
        ("-c:v libx264 -vn", "disables video"),
        ("-c:v copy -vf scale=640:-2", "video filters"),
        ("-c:a copy -af volume=2", "audio filters"),
        ("-c:v libx264 extra.mkv", "extra.mkv"),
        ("-vf 'scale", "quote"),
    ] {
        let a = PresetAnalysis::of_text(args, preset::DEFAULT_AUDIO_FALLBACK);
        assert!(!a.is_valid());
        assert!(a.errors.iter().any(|e| e.to_lowercase().contains(expected)), "{args}: {:?}", a.errors);
    }
}

#[test]
fn bad_fallback_is_an_error_and_warnings_pass_through() {
    let a = PresetAnalysis::of_text("-y -c:v copy -c:a copy", "-c:a 'aac");
    assert!(a.errors.iter().any(|e| e.starts_with("Audio fallback")));
    assert_eq!(a.warnings.len(), 1);
    assert!(a.video_copy());
}

#[test]
fn audio_summary_follows_the_planner() {
    assert!(PresetAnalysis::of(&Preset::plane_copy()).audio_summary("-c:a aac").unwrap().contains("audio offset"));
    assert!(PresetAnalysis::of(&Preset::for_storage()).audio_summary("-c:a aac").unwrap().contains("single clip"));
    assert!(PresetAnalysis::of_text("-c:v libx264 -an", "").audio_summary("").unwrap().contains("dropped"));
}

#[test]
fn planner_refuses_an_invalid_preset_by_name() {
    let job = ExportJob {
        state: ProjectState::new(vec![source("s", "/m/a.mp4")], Timeline::new(vec![clip("c", "s", 0.0, 1.0)]), vec![]),
        media: HashMap::new(),
        preset: Preset { name: "broken".into(), arguments: "-c:v copy -vn".into(), ..Preset::plane_copy() },
        output_path: "/m/out.mp4".into(),
        work_directory: "/tmp/w".into(),
        keyframes: HashMap::new(),
    };
    let e = export_planner::plan(&job).unwrap_err();
    assert!(e.0.contains("\"broken\""));
}

fn c(id: &str, in_s: f64, out_s: f64, offset_ms: f64) -> Clip {
    Clip { audio_offset: ms(offset_ms), ..clip(id, "s", in_s, out_s) }
}

fn t(clips: Vec<Clip>) -> Timeline {
    Timeline::new(clips)
}

fn frames30() -> CutGrid {
    CutGrid { duration: s(20.0), frame_rate: Rational::new(30, 1), ..Default::default() }
}

#[test]
fn split_keeps_offset_on_both_halves() {
    let (tl, right) = te::split(&t(vec![c("a", 0.0, 10.0, 120.0)]), s(4.0), &|_| None);
    assert!(right.is_some());
    assert_eq!(tl.clips().len(), 2);
    assert_eq!(tl.clips()[0].out, s(4.0));
    assert_eq!(tl.clips()[1].r#in, s(4.0));
    assert!(tl.clips().iter().all(|c| c.audio_offset == ms(120.0)));
}

#[test]
fn split_at_edge_does_nothing() {
    let original = t(vec![c("a", 0.0, 10.0, 0.0)]);
    let (tl, right) = te::split(&original, ms(10.0), &|_| None);
    assert!(right.is_none());
    assert_eq!(tl, original);
}

#[test]
fn split_maps_the_playhead_into_the_source_and_snaps() {
    let timeline = t(vec![c("a", 0.0, 2.0, 0.0), c("b", 10.0, 15.0, 0.0)]);
    let (by_frame, _) = te::split(&timeline, s(3.51), &|_| Some(frames30()));
    assert_eq!(by_frame.clips()[2].r#in, time::frame_start(345, Rational::new(30, 1)));
    let kf = CutGrid { keyframes: Some(vec![s(10.0), s(12.0)]), ..frames30() };
    let (by_key, _) = te::split(&timeline, s(3.51), &|_| Some(kf.clone()));
    assert_eq!(by_key.clips()[1].out, s(12.0));
    assert_eq!(by_key.clips()[2].r#in, s(12.0));
}

#[test]
fn deleting_keeps_the_time_on_the_same_moment() {
    let tl = t(vec![c("a", 0.0, 2.0, 0.0), c("b", 0.0, 3.0, 0.0), c("c", 0.0, 4.0, 0.0)]);
    for (deleted, at, expected) in [("b", 7.0, 4.0), ("c", 3.0, 3.0), ("b", 3.5, 2.0), ("b", 5.0, 2.0), ("a", 2.0, 0.0), ("ac", 9.0, 3.0)] {
        let set = deleted.chars().map(|c| c.to_string()).collect();
        assert_eq!(te::time_after_delete(&tl, &set, s(at)), s(expected), "{deleted} {at}");
    }
}

#[test]
fn inspector_ranges_snap_like_trims() {
    let kf = CutGrid { keyframes: Some(vec![Time::ZERO, s(3.0)]), ..frames30() };
    let g = |_: &str| Some(kf.clone());
    let tl = te::set_range(&t(vec![c("a", 2.0, 5.0, 0.0)]), "a", s(2.6), s(7.01), &g);
    assert_eq!(tl.clips()[0].r#in, s(3.0));
    assert_eq!(tl.clips()[0].out, time::frame_start(210, Rational::new(30, 1)));
    assert_eq!(te::trim_in(&t(vec![c("a", 2.0, 5.0, 0.0)]), "a", s(2.9), &g).clips()[0].r#in, s(3.0));
}

#[test]
fn move_keeps_relative_order() {
    let tl = t(vec![c("a", 0.0, 1.0, 0.0), c("b", 1.0, 2.0, 0.0), c("c", 2.0, 3.0, 0.0), c("d", 3.0, 4.0, 0.0)]);
    for (index, expected) in [(0, "bcad"), (4, "adbc"), (1, "abcd"), (3, "abcd")] {
        let moved = te::move_clips(&tl, &ids(&["b", "c"]), index);
        let order: String = moved.clips().iter().map(|c| c.id.as_str()).collect();
        assert_eq!(order, expected);
    }
}

#[test]
fn trim_is_clamped_to_source_and_minimum_length() {
    let tl = t(vec![c("a", 2.0, 5.0, 0.0)]);
    let none = |_: &str| None;
    assert_eq!(te::trim_in(&tl, "a", s(-3.0), &none).clips()[0].r#in, Time::ZERO);
    assert_eq!(te::trim_in(&tl, "a", s(9.0), &none).clips()[0].r#in, s(5.0) - te::MIN_CLIP_DURATION);
    assert_eq!(te::trim_out(&tl, "a", s(20.0), &|_| Some(CutGrid::new(s(8.0)))).clips()[0].out, s(8.0));
    assert_eq!(te::trim_out(&tl, "a", s(9.0), &none).clips()[0].out, s(5.0));
    assert_eq!(te::trim_out(&tl, "a", s(4.23), &|_| Some(frames30())).clips()[0].out, time::frame_start(127, Rational::new(30, 1)));
}

#[test]
fn locate_finds_clip_and_start() {
    let tl = t(vec![c("a", 0.0, 2.0, 0.0), c("b", 10.0, 13.0, 0.0), c("c", 0.0, 1.0, 0.0)]);
    assert_eq!(tl.locate(s(-1.0)), Some((0, Time::ZERO)));
    assert_eq!(tl.locate(s(1.999)), Some((0, Time::ZERO)));
    assert_eq!(tl.locate(s(2.0)), Some((1, s(2.0))));
    assert_eq!(tl.locate(s(3.0)), Some((1, s(2.0))));
    assert_eq!(tl.locate(s(99.0)), Some((2, s(5.0))));
    assert_eq!(tl.duration(), s(6.0));
    assert_eq!(Timeline::empty().locate(Time::ZERO), None);
    assert_eq!(Timeline::empty().duration(), Time::ZERO);
}

#[test]
fn bounds_follow_a_changed_clip_list() {
    let tl = t(vec![c("a", 0.0, 2.0, 0.0), c("b", 10.0, 13.0, 0.0)]);
    assert_eq!(tl.bounds(), &[Time::ZERO, s(2.0), s(5.0)]);
    let longer = te::append(&tl, c("c", 0.0, 4.0, 0.0));
    assert_eq!(longer.duration(), s(9.0));
    assert_eq!(longer.start_of(2), s(5.0));
    assert_eq!(tl.duration(), s(5.0));
}

#[test]
fn undo_collapses_a_gesture_into_one_step() {
    let mut stack = UndoStack::new(t(vec![c("a", 0.0, 10.0, 0.0)]));
    stack.begin_gesture();
    for _ in 0..2 {
        let next = te::nudge_audio_offset(stack.current(), &ids(&["a"]), ms(10.0));
        stack.replace_current(next);
    }
    stack.end_gesture();
    assert_eq!(stack.current().clips()[0].audio_offset, ms(20.0));
    assert!(stack.undo());
    assert_eq!(stack.current().clips()[0].audio_offset, Time::ZERO);
    assert!(!stack.can_undo());
    assert!(stack.redo());
    assert_eq!(stack.current().clips()[0].audio_offset, ms(20.0));
}

#[test]
fn rewind_to_drops_the_steps_since_and_keeps_the_ones_before() {
    let nudge = |tl: &Timeline| te::nudge_audio_offset(tl, &ids(&["a"]), ms(10.0));
    let mut stack = UndoStack::new(t(vec![c("a", 0.0, 10.0, 0.0)]));
    let initial = stack.current().clone();
    let n = nudge(stack.current());
    stack.commit(n);
    let checkpoint = stack.current().clone();
    for _ in 0..2 {
        let n = nudge(stack.current());
        stack.commit(n);
    }
    assert!(stack.rewind_to(&checkpoint));
    assert!(Arc::ptr_eq(&checkpoint, stack.current()));
    assert!(!stack.can_redo());
    assert!(stack.undo());
    assert!(Arc::ptr_eq(&initial, stack.current()));
    assert!(!stack.can_undo());
    assert!(!stack.rewind_to(&checkpoint));
    assert!(Arc::ptr_eq(&initial, stack.current()));
    assert!(stack.can_redo());
}
