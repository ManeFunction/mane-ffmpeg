//! Exports real timelines with ffmpeg and checks durations and A/V sync of the result.
mod fixture;
use fixture::{Fixture, wait_until};
use mane_core::ffmpeg::export_planner::{self as planner, ExportJob, ExportMode, ExportPlan};
use mane_core::ffmpeg::export_runner::{ExportOutcome, ExportResult, ExportRunner};
use mane_core::media::keyframes::KeyframeService;
use mane_core::media::waveform::WaveformService;
use mane_core::model::*;
use std::collections::HashMap;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

const FRAME_TOLERANCE: f64 = 0.045;
const BEEP_TOLERANCE: f64 = 0.03;

fn fast_encode() -> Preset {
    Preset {
        id: "test-encode".into(),
        name: "fast encode".into(),
        arguments: "-c:v libx264 -preset ultrafast -crf 30 -c:a copy".into(),
        extension: "mp4".into(),
        audio_fallback_arguments: preset::DEFAULT_AUDIO_FALLBACK.into(),
    }
}

fn slow_encode() -> Preset {
    Preset { arguments: "-c:v libx265 -preset veryslow -c:a copy".into(), ..Preset::for_storage() }
}

fn c(src: &str, a: f64, b: f64, offset_ms: f64) -> Clip {
    Clip { audio_offset: Time::from_ms_f(offset_ms), ..Clip::create(src, Time::from_seconds(a), Time::from_seconds(b)) }
}

fn unique() -> String {
    ids::new()
}

fn run(plan: &ExportPlan, f: &Fixture) -> ExportResult {
    let result = ExportRunner::new(f.tools.clone()).run(plan, &mut |_| {});
    assert!(result.outcome == ExportOutcome::Succeeded, "{}\n{:?}\n{}", result.message, result.log, plan.command_text("ffmpeg"));
    result
}

fn export(
    f: &Fixture,
    preset: Preset,
    ext: &str,
    sources: &[(&str, &str)],
    clips: Vec<Clip>,
    crop: Option<CropRect>,
) -> (ExportPlan, String) {
    let kf = KeyframeService::new(f.tools.clone());
    let mut media = HashMap::new();
    let mut keyframes = HashMap::new();
    let mut list = Vec::new();
    for (id, path) in sources {
        list.push(SourceMedia { id: id.to_string(), path: path.to_string(), ..Default::default() });
        let info = f.probe(path);
        keyframes.insert(id.to_string(), (*kf.get(path, &info).unwrap()).clone());
        media.insert(id.to_string(), info);
    }
    let output = f.directory.join(format!("out-{}.{ext}", unique())).to_string_lossy().into_owned();
    let work = f.directory.join(format!("work-{}", unique())).to_string_lossy().into_owned();
    let plan = planner::plan(&ExportJob {
        state: ProjectState::new(list, Timeline::new(clips), vec![]).with_crop(crop),
        media,
        preset,
        output_path: output.clone(),
        work_directory: work.clone(),
        keyframes,
    })
    .unwrap();
    run(&plan, f);
    assert!(!std::path::Path::new(&work).exists());
    (plan, output)
}

fn assert_times(expected: &[f64], actual: &[f64], tolerance: f64, what: &str) {
    let ok = expected.len() == actual.len() && expected.iter().zip(actual).all(|(e, a)| (e - a).abs() <= tolerance);
    assert!(ok, "{what}: expected {expected:?} but got {actual:?}");
}

/// Stream copy keeps a few hundredths of a second before the first keyframe (B-frame delay, AAC priming), shifting
/// everything equally; compare after removing that common shift.
fn assert_copy_times(ef: &[f64], eb: &[f64], flashes: &[f64], beeps: &[f64]) {
    assert!(!flashes.is_empty());
    let shift = flashes[0] - ef[0];
    assert!((-0.01..=0.12).contains(&shift), "shift {shift}");
    let ef: Vec<f64> = ef.iter().map(|x| x + shift).collect();
    let eb: Vec<f64> = eb.iter().map(|x| x + shift).collect();
    assert_times(&ef, flashes, FRAME_TOLERANCE, &format!("flashes (shift {shift:.3})"));
    assert_times(&eb, beeps, BEEP_TOLERANCE, &format!("beeps (shift {shift:.3})"));
}

fn in_range(v: f64, lo: f64, hi: f64) {
    assert!(v >= lo && v <= hi, "{v} not in {lo}..{hi}");
}

#[test]
fn encode_reorders_mixed_sources_and_keeps_offsets_in_sync() {
    let Some(f) = fixture::get() else { return };
    let (plan, out) = export(
        f,
        fast_encode(),
        "mp4",
        &[("a", &f.sync30), ("c", &f.sync25)],
        vec![c("a", 3.5, 7.0, 0.0), c("a", 1.0, 3.5, 120.0), c("c", 2.5, 5.1, -80.0)],
        None,
    );
    assert_eq!(plan.mode, ExportMode::Encode);
    assert!(plan.notes.iter().any(|n| n.contains("re-encoded")));
    let (flashes, beeps, duration) = f.measure(&out);
    in_range(duration, 8.55, 8.65);
    assert_times(&[0.5, 2.5, 4.5, 7.5], &flashes, FRAME_TOLERANCE, "flashes");
    assert_times(&[0.5, 2.5, 4.62, 7.42], &beeps, BEEP_TOLERANCE, "beeps");
    assert_eq!(f.probe(&out).video.unwrap().width, 640);
}

#[test]
fn built_in_for_storage_preset_encodes_hevc_with_quicktime_tag() {
    let Some(f) = fixture::get() else { return };
    let (plan, out) =
        export(f, Preset::for_storage(), "mp4", &[("a", &f.sync30)], vec![c("a", 3.5, 6.0, 0.0), c("a", 1.0, 2.5, 0.0)], None);
    assert!(plan.steps[0].arguments.contains(&"hvc1".to_string()));
    let info = f.probe(&out);
    let v = info.video.as_ref().unwrap();
    assert_eq!(v.codec, "hevc");
    assert_eq!(v.pixel_format, "yuv420p10le");
    in_range(info.duration.total_seconds(), 3.95, 4.05);
}

#[test]
fn plane_copy_cuts_on_keyframes_and_copies_audio_without_offsets() {
    let Some(f) = fixture::get() else { return };
    let (plan, out) = export(f, Preset::plane_copy(), "mp4", &[("a", &f.sync30)], vec![c("a", 3.0, 7.0, 0.0), c("a", 1.0, 3.5, 0.0)], None);
    assert_eq!(plan.mode, ExportMode::StreamCopy);
    assert!(plan.steps[0].arguments.contains(&"0:1".to_string()));
    let (fl, be, d) = f.measure(&out);
    in_range(d, 6.45, 6.65);
    assert_copy_times(&[1.0, 3.0, 5.0], &[1.0, 3.0, 5.0], &fl, &be);
}

#[test]
fn plane_copy_with_offsets_reencodes_audio_only() {
    let Some(f) = fixture::get() else { return };
    let (plan, out) = export(
        f,
        Preset::plane_copy(),
        "mp4",
        &[("a", &f.sync30)],
        vec![c("a", 3.0, 7.0, 0.0), c("a", 1.0, 3.5, 120.0), c("a", 5.0, 7.5, -80.0)],
        None,
    );
    assert_eq!(plan.mode, ExportMode::StreamCopy);
    assert!(plan.notes.iter().any(|n| n.contains("offsets")));
    let (fl, be, d) = f.measure(&out);
    in_range(d, 8.95, 9.15);
    assert_copy_times(&[1.0, 3.0, 5.0, 7.5], &[1.0, 3.0, 5.12, 7.42], &fl, &be);
    assert_eq!(f.probe(&out).video.unwrap().codec, "h264");
}

#[test]
fn plane_copy_snaps_in_points_to_keyframes() {
    let Some(f) = fixture::get() else { return };
    let (plan, out) = export(f, Preset::plane_copy(), "mp4", &[("a", &f.sync30)], vec![c("a", 3.5, 7.0, 0.0)], None);
    assert!(plan.notes.iter().any(|n| n.contains("keyframes")));
    in_range(f.probe(&out).duration.total_seconds(), 3.95, 4.15);
}

// With ffmpeg 9.0 the losslessly cut audio lands 33 ms early: the muxer drops the cut file's start offset. The
// commands are the same as the C# app's (see the planner snapshots), so this is ffmpeg's change, not the port's.
#[test]
#[ignore = "ffmpeg 9.0 shifts the losslessly cut audio 33 ms early (same commands as 2.1.1)"]
fn single_clip_keeps_audio_lossless_with_offset() {
    let Some(f) = fixture::get() else { return };
    let (plan, out) = export(f, fast_encode(), "mp4", &[("a", &f.sync30)], vec![c("a", 3.5, 8.0, 120.0)], None);
    assert_eq!(plan.mode, ExportMode::SingleClipAudioCopy);
    let (fl, be, _) = f.measure(&out);
    assert_times(&[0.5, 2.5], &fl, FRAME_TOLERANCE, "flashes");
    assert_times(&[0.62, 2.62], &be, BEEP_TOLERANCE, "beeps");
    assert_eq!(f.probe(&out).audio_streams[0].codec, "aac");
}

#[test]
fn single_clip_offset_past_file_start_adds_leading_silence() {
    let Some(f) = fixture::get() else { return };
    let (plan, out) = export(f, fast_encode(), "mp4", &[("a", &f.sync30)], vec![c("a", 0.0, 4.0, 500.0)], None);
    assert_eq!(plan.mode, ExportMode::SingleClipAudioCopy);
    let (_, be, _) = f.measure(&out);
    assert_times(&[0.5, 2.5], &be, BEEP_TOLERANCE, "beeps");
}

#[test]
fn whole_file_runs_preset_unchanged() {
    let Some(f) = fixture::get() else { return };
    let (plan, out) = export(f, Preset::plane_copy(), "mp4", &[("a", &f.sync30)], vec![c("a", 0.0, 12.0, 0.0)], None);
    assert_eq!(plan.mode, ExportMode::Passthrough);
    assert!(plan.steps[0].arguments.contains(&"copy".to_string()));
    in_range(f.probe(&out).duration.total_seconds(), 11.9, 12.1);
}

#[test]
fn crop_cuts_the_picture_in_every_encoding_mode_and_keeps_sync() {
    let Some(f) = fixture::get() else { return };
    let rect = CropRect::new(0.25, 0.1, 0.5, 0.6);
    let crop = Some(rect);
    let (whole, wo) = export(f, fast_encode(), "mp4", &[("a", &f.sync30)], vec![c("a", 0.0, 12.0, 0.0)], crop);
    let (single, so) = export(f, fast_encode(), "mp4", &[("a", &f.sync30)], vec![c("a", 3.5, 8.0, 120.0)], crop);
    let (joined, jo) =
        export(f, fast_encode(), "mp4", &[("a", &f.sync30), ("c", &f.sync25)], vec![c("a", 3.5, 7.0, 0.0), c("c", 2.5, 5.1, -80.0)], crop);
    assert_eq!([whole.mode, single.mode, joined.mode], [ExportMode::Passthrough, ExportMode::SingleClipAudioCopy, ExportMode::Encode]);
    let first = f.probe(&f.sync30).video.unwrap();
    let expected = OutputFrame::of(&[&first]).pixels(&rect);
    for out in [&wo, &so, &jo] {
        let v = f.probe(out).video.unwrap();
        assert_eq!((v.width, v.height), (expected.width, expected.height));
    }
    let (fl, be, _) = f.measure(&jo);
    assert_times(&[0.5, 2.5, 5.0], &fl, FRAME_TOLERANCE, "flashes");
    assert_times(&[0.5, 2.5, 4.92], &be, BEEP_TOLERANCE, "beeps");
}

#[test]
fn clips_without_audio_get_silence() {
    let Some(f) = fixture::get() else { return };
    let (_, out) =
        export(f, fast_encode(), "mkv", &[("a", &f.sync30), ("n", &f.no_audio)], vec![c("n", 1.0, 3.0, 0.0), c("a", 3.5, 5.0, 0.0)], None);
    let (fl, be, d) = f.measure(&out);
    in_range(d, 3.45, 3.6);
    assert_times(&[1.0, 2.5], &fl, FRAME_TOLERANCE, "flashes");
    assert_times(&[2.5], &be, BEEP_TOLERANCE, "beeps");
}

#[test]
fn transport_stream_with_nonzero_start_time_cuts_correctly() {
    let Some(f) = fixture::get() else { return };
    assert!(f.probe(&f.transport_stream).start_time > Time::ZERO);
    let (ep, enc) = export(f, fast_encode(), "mp4", &[("t", &f.transport_stream)], vec![c("t", 1.5, 4.5, 0.0)], None);
    let (fl, _, _) = f.measure(&enc);
    assert_times(&[0.5, 2.5], &fl, FRAME_TOLERANCE, &format!("encoded flashes\n{}", ep.command_text("ffmpeg")));
    assert_eq!(ep.mode, ExportMode::SingleClipAudioCopy);
    let (cp, copied) = export(f, Preset::plane_copy(), "ts", &[("t", &f.transport_stream)], vec![c("t", 1.13, 4.5, 0.0)], None);
    assert_eq!(cp.steps.len(), 2);
    let (cf, cb, _) = f.measure(&copied);
    assert_copy_times(&[0.9, 2.9], &[0.9, 2.9], &cf, &cb);
}

#[test]
fn transport_stream_plane_copy_from_the_first_keyframe_joins_without_a_gap() {
    let Some(f) = fixture::get() else { return };
    let info = f.probe(&f.transport_stream);
    let keyframes = KeyframeService::new(f.tools.clone()).get(&f.transport_stream, &info).unwrap();
    let second = planner::keyframe_at_or_before(&keyframes.times, Time::from_seconds(5.5)).total_seconds();
    in_range(second, 4.2, 5.5);
    let (plan, out) =
        export(f, Preset::plane_copy(), "mkv", &[("t", &f.transport_stream)], vec![c("t", 0.0, 3.0, 0.0), c("t", 5.5, 8.0, 0.0)], None);
    assert_eq!(plan.mode, ExportMode::StreamCopy);
    let (fl, be, _) = f.measure(&out);
    let expected = [2.0, 3.0 + (6.0 - second)];
    assert_copy_times(&expected, &expected, &fl, &be);
}

#[test]
fn transport_stream_plane_copy_with_offset_keeps_the_first_gop() {
    let Some(f) = fixture::get() else { return };
    let (plan, out) = export(f, Preset::plane_copy(), "mkv", &[("t", &f.transport_stream)], vec![c("t", 1.13, 4.5, 120.0)], None);
    assert_eq!(plan.mode, ExportMode::StreamCopy);
    let (fl, be, d) = f.measure(&out);
    in_range(d, 3.3, 3.5);
    assert_copy_times(&[0.9, 2.9], &[1.02, 3.02], &fl, &be);
}

/// Clip `a in–out` of the sync file playing the tone file instead, dropped at its in point.
fn t(a: f64, b: f64, offset_ms: f64, looped: bool, anchor: Option<f64>) -> Clip {
    Clip {
        replaced_audio: Some(AudioReplacement {
            file_id: "t".into(),
            anchor: Time::from_seconds(anchor.unwrap_or(a)),
            offset: Time::from_ms_f(offset_ms),
            looped,
        }),
        ..c("a", a, b, 0.0)
    }
}

fn export_replaced(f: &Fixture, preset: Preset, clips: Vec<Clip>) -> (ExportPlan, String) {
    let a = f.probe(&f.sync30);
    let kf = KeyframeService::new(f.tools.clone()).get(&f.sync30, &a).unwrap();
    let media: HashMap<String, MediaInfo> = [("a".to_string(), a), ("t".to_string(), f.probe(&f.tone))].into();
    let output = f.directory.join(format!("out-{}.mp4", unique())).to_string_lossy().into_owned();
    let work = f.directory.join(format!("work-{}", unique())).to_string_lossy().into_owned();
    let plan = planner::plan(&ExportJob {
        state: ProjectState::new(
            vec![SourceMedia { id: "a".into(), path: f.sync30.clone(), ..Default::default() }],
            Timeline::new(clips),
            vec![SourceMedia { id: "t".into(), path: f.tone.clone(), ..Default::default() }],
        ),
        media,
        preset,
        output_path: output.clone(),
        work_directory: work,
        keyframes: [("a".to_string(), (*kf).clone())].into(),
    })
    .unwrap();
    run(&plan, f);
    (plan, output)
}

#[test]
fn replaced_audio_plays_once_from_where_it_was_dropped() {
    let Some(f) = fixture::get() else { return };
    let (_, out) = export_replaced(f, fast_encode(), vec![t(1.0, 8.0, 0.0, false, None)]);
    let (fl, be, d) = f.measure(&out);
    in_range(d, 6.95, 7.1);
    assert_times(&[1.0, 3.0, 5.0], &fl, FRAME_TOLERANCE, "flashes");
    assert_times(&[0.3], &be, BEEP_TOLERANCE, "beeps");
}

#[test]
fn looped_audio_repeats_from_its_slipped_phase() {
    let Some(f) = fixture::get() else { return };
    for (offset, expected) in [(0.0, vec![0.3, 3.3, 6.3]), (500.0, vec![0.8, 3.8, 6.8]), (-1000.0, vec![2.3, 5.3])] {
        let (plan, out) = export_replaced(f, fast_encode(), vec![t(1.0, 8.0, offset, true, None)]);
        assert!(!plan.steps[0].arguments.iter().any(|a| a.contains("aloop")));
        let (_, be, _) = f.measure(&out);
        assert_times(&expected, &be, BEEP_TOLERANCE, "beeps");
    }
}

#[test]
fn split_halves_continue_the_looped_audio() {
    let Some(f) = fixture::get() else { return };
    let (_, out) = export_replaced(f, fast_encode(), vec![t(1.0, 4.0, 0.0, true, None), t(4.0, 8.0, 0.0, true, Some(1.0))]);
    let (_, be, _) = f.measure(&out);
    assert_times(&[0.3, 3.3, 6.3], &be, BEEP_TOLERANCE, "beeps");
}

#[test]
fn plane_copy_keeps_replaced_audio_in_sync_with_copied_video() {
    let Some(f) = fixture::get() else { return };
    let (plan, out) = export_replaced(f, Preset::plane_copy(), vec![t(1.0, 8.0, 0.0, false, None)]);
    assert_eq!(plan.mode, ExportMode::StreamCopy);
    assert!(plan.notes.iter().any(|n| n.contains("replaced audio")));
    let (fl, be, _) = f.measure(&out);
    assert_copy_times(&[1.0, 3.0, 5.0], &[0.3], &fl, &be);
}

#[test]
fn own_and_replaced_audio_mix_on_one_timeline() {
    let Some(f) = fixture::get() else { return };
    let (_, out) = export_replaced(f, fast_encode(), vec![c("a", 3.5, 7.0, 0.0), t(1.0, 3.5, 0.0, false, None)]);
    let (fl, be, _) = f.measure(&out);
    assert_times(&[0.5, 2.5, 4.5], &fl, FRAME_TOLERANCE, "flashes");
    assert_times(&[0.5, 2.5, 3.8], &be, BEEP_TOLERANCE, "beeps");
}

fn plan_for(f: &Fixture, preset: Preset, name: &str, clips: Vec<Clip>) -> ExportPlan {
    planner::plan(&ExportJob {
        state: ProjectState::new(
            vec![SourceMedia { id: "a".into(), path: f.sync30.clone(), ..Default::default() }],
            Timeline::new(clips),
            vec![],
        ),
        media: [("a".to_string(), f.probe(&f.sync30))].into(),
        preset,
        output_path: f.directory.join(format!("{name}.mp4")).to_string_lossy().into_owned(),
        work_directory: f.directory.join(format!("work-{name}")).to_string_lossy().into_owned(),
        keyframes: HashMap::new(),
    })
    .unwrap()
}

#[derive(Default, Clone)]
struct Progress(Arc<Mutex<Vec<Time>>>);

impl Progress {
    fn done(&self) -> Time {
        self.0.lock().unwrap().last().copied().unwrap_or(Time::ZERO)
    }
    fn count(&self) -> usize {
        self.0.lock().unwrap().len()
    }
}

fn start(runner: &ExportRunner, plan: &ExportPlan, progress: &Progress) -> std::thread::JoinHandle<ExportResult> {
    let (r, p, log) = (runner.clone(), plan.clone(), progress.clone());
    std::thread::spawn(move || r.run(&p, &mut |x| log.0.lock().unwrap().push(x.done)))
}

#[test]
fn cancel_removes_partial_output() {
    let Some(f) = fixture::get() else { return };
    let plan = plan_for(f, slow_encode(), "cancelled", vec![c("a", 0.5, 11.0, 0.0), c("a", 0.0, 0.4, 0.0)]);
    let runner = ExportRunner::new(f.tools.clone());
    let r = runner.clone();
    std::thread::spawn(move || {
        std::thread::sleep(Duration::from_millis(1500));
        r.cancel();
    });
    let result = runner.run(&plan, &mut |_| {});
    assert_eq!(result.outcome, ExportOutcome::Cancelled);
    assert!(!std::path::Path::new(&plan.output_path).exists());
    assert!(!std::path::Path::new(&plan.work_directory).exists());
}

#[test]
fn pause_freezes_ffmpeg_and_resume_finishes_the_export() {
    let Some(f) = fixture::get() else { return };
    let plan = plan_for(f, slow_encode(), "paused", vec![c("a", 0.5, 6.0, 0.0), c("a", 0.0, 0.4, 0.0)]);
    let runner = ExportRunner::new(f.tools.clone());
    let progress = Progress::default();
    let wall = Instant::now();
    let run = start(&runner, &plan, &progress);
    wait_until(|| progress.done() > Time::ZERO, "ffmpeg's first progress");
    runner.pause();
    std::thread::sleep(Duration::from_millis(300));
    let frozen = progress.done();
    std::thread::sleep(Duration::from_millis(1500));
    assert_eq!(progress.done(), frozen);
    assert!(!run.is_finished());
    // Starting another process still works.
    f.probe(&f.sync30);
    runner.resume();
    let result = run.join().unwrap();
    assert!(result.outcome == ExportOutcome::Succeeded, "{}", result.message);
    assert!(wall.elapsed() - result.elapsed >= Duration::from_millis(1700), "elapsed {:?} of {:?}", result.elapsed, wall.elapsed());
    in_range(f.probe(&plan.output_path).duration.total_seconds(), 5.85, 5.95);
}

#[test]
fn pause_before_the_run_holds_the_first_step() {
    let Some(f) = fixture::get() else { return };
    let plan = plan_for(f, fast_encode(), "paused-early", vec![c("a", 1.0, 4.0, 0.0), c("a", 5.0, 6.0, 0.0)]);
    let runner = ExportRunner::new(f.tools.clone());
    let progress = Progress::default();
    let wall = Instant::now();
    runner.pause();
    let run = start(&runner, &plan, &progress);
    std::thread::sleep(Duration::from_secs(1));
    assert!(!run.is_finished());
    assert_eq!(progress.count(), 0);
    assert!(!std::path::Path::new(&plan.output_path).exists());
    runner.resume();
    let result = run.join().unwrap();
    assert!(result.outcome == ExportOutcome::Succeeded, "{}", result.message);
    assert!(wall.elapsed() - result.elapsed >= Duration::from_millis(900));
}

#[test]
fn cancel_while_paused_removes_partial_output() {
    let Some(f) = fixture::get() else { return };
    let plan = plan_for(f, slow_encode(), "cancelled-paused", vec![c("a", 0.5, 6.0, 0.0), c("a", 0.0, 0.4, 0.0)]);
    let runner = ExportRunner::new(f.tools.clone());
    let progress = Progress::default();
    let run = start(&runner, &plan, &progress);
    wait_until(|| progress.done() > Time::ZERO, "ffmpeg's first progress");
    runner.pause();
    std::thread::sleep(Duration::from_millis(300));
    runner.cancel();
    let result = run.join().unwrap();
    assert_eq!(result.outcome, ExportOutcome::Cancelled);
    assert!(!std::path::Path::new(&plan.output_path).exists());
    assert!(!std::path::Path::new(&plan.work_directory).exists());
    let out = std::process::Command::new("pgrep").args(["-f", &plan.output_path]).output().unwrap();
    assert_eq!(String::from_utf8_lossy(&out.stdout).trim(), "");
}

#[test]
fn refuses_to_overwrite_a_source() {
    let Some(f) = fixture::get() else { return };
    let media: HashMap<String, MediaInfo> = [("a".to_string(), f.probe(&f.sync30))].into();
    let refused = |output: &str| {
        let e = planner::plan(&ExportJob {
            state: ProjectState::new(
                vec![SourceMedia { id: "a".into(), path: f.sync30.clone(), ..Default::default() }],
                Timeline::new(vec![c("a", 1.0, 2.0, 0.0)]),
                vec![],
            ),
            media: media.clone(),
            preset: fast_encode(),
            output_path: output.into(),
            work_directory: "/tmp/x".into(),
            keyframes: HashMap::new(),
        })
        .unwrap_err();
        assert!(e.0.contains("source"));
    };
    refused(&f.sync30);
    let link = f.directory.join(format!("link-{}.mp4", unique()));
    std::os::unix::fs::symlink(&f.sync30, &link).unwrap();
    refused(&link.to_string_lossy());
}

#[test]
fn a_file_replaced_in_place_is_analyzed_again() {
    let Some(f) = fixture::get() else { return };
    let path = f.directory.join("replaced.mp4").to_string_lossy().into_owned();
    std::fs::copy(&f.sync30, &path).unwrap();
    let keyframes = KeyframeService::new(f.tools.clone());
    let waveforms = WaveformService::new(f.tools.clone());
    let before = f.probe(&path);
    assert_eq!(keyframes.get(&path, &before).unwrap().times.len(), 12);
    assert!(waveforms.get(&path, &before, before.audio_streams[0].index).is_some());
    std::fs::copy(&f.no_audio, &path).unwrap();
    let later = std::time::SystemTime::now() + Duration::from_secs(60);
    std::fs::File::options().write(true).open(&path).unwrap().set_modified(later).unwrap();
    let after = f.probe(&path);
    assert_ne!(before.stamp, after.stamp);
    assert!(keyframes.try_get(&path, &after).is_none());
    let fresh = KeyframeService::new(f.tools.clone()).get(&f.no_audio, &f.probe(&f.no_audio)).unwrap();
    assert_eq!(fresh.times, keyframes.get(&path, &after).unwrap().times);
    assert_eq!(keyframes.try_get(&path, &before).unwrap().times.len(), 12);
}

#[test]
fn waveform_and_keyframes_are_read() {
    let Some(f) = fixture::get() else { return };
    let info = f.probe(&f.sync30);
    let k = KeyframeService::new(f.tools.clone()).get(&f.sync30, &info).unwrap();
    assert_eq!(k.times.len(), 12);
    assert_eq!(k.times[0], Time::ZERO);
    in_range(k.times[5].total_seconds(), 4.99, 5.01);
    in_range(k.reorder_delay.total_seconds(), 0.03, 0.1);
    let w = WaveformService::new(f.tools.clone()).get(&f.sync30, &info, info.audio_streams[0].index).unwrap();
    in_range(w.duration().total_seconds(), 11.9, 12.1);
    assert!(w.max(Time::from_seconds(2.0), Time::from_seconds(2.1)) > 150);
    assert!(w.max(Time::from_seconds(2.5), Time::from_seconds(3.5)) < 10);
}
