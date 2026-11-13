mod common;
use common::*;
use mane_core::ffmpeg::export_planner::{self as planner, ExportJob, ExportMode};
use mane_core::media::keyframes::KeyframeIndex;
use mane_core::model::*;
use std::collections::HashMap;

fn media(seconds: f64, format: &str, start: f64, codec: &str) -> MediaInfo {
    MediaInfo {
        duration: s(seconds),
        start_time: s(start),
        format_name: format.into(),
        video: Some(VideoStreamInfo {
            index: 0,
            codec: codec.into(),
            width: 1920,
            height: 1080,
            frame_rate: Rational::new(30, 1),
            pixel_format: "yuv420p".into(),
            ..Default::default()
        }),
        audio_streams: vec![AudioStreamInfo {
            index: 1,
            codec: "aac".into(),
            sample_rate: 48000,
            channels: 2,
            channel_layout: "stereo".into(),
            duration: Some(s(seconds)),
            ..Default::default()
        }],
        ..Default::default()
    }
}

const MP4: &str = "mov,mp4,m4a,3gp,3g2,mj2";

fn every_second() -> KeyframeIndex {
    KeyframeIndex { times: (0..20).map(|i| s(i as f64)).collect(), reorder_delay: Time(10_000_000 * 2 / 30) }
}

fn c(src: &str, in_s: f64, out_s: f64, offset_ms: f64) -> Clip {
    Clip { audio_offset: ms(offset_ms), ..Clip::create(src, s(in_s), s(out_s)) }
}

fn job(preset: Preset, media: Vec<(&str, MediaInfo)>, clips: Vec<Clip>) -> ExportJob {
    ExportJob {
        state: ProjectState::new(media.iter().map(|(id, _)| source(id, &format!("/m/{id}.src"))).collect(), Timeline::new(clips), vec![]),
        keyframes: media.iter().map(|(id, _)| (id.to_string(), every_second())).collect(),
        media: media.into_iter().map(|(id, m)| (id.to_string(), m)).collect(),
        preset,
        output_path: "/out/edit.mp4".into(),
        work_directory: "/work".into(),
    }
}

fn encode() -> Preset {
    Preset {
        id: "e".into(),
        name: "encode".into(),
        arguments: "-c:v libx264 -crf 20 -c:a copy".into(),
        extension: "mp4".into(),
        audio_fallback_arguments: preset::DEFAULT_AUDIO_FALLBACK.into(),
    }
}

#[test]
fn picks_the_mode_from_the_edit_and_the_preset() {
    for (edit, copy, expected) in [
        ("whole", false, ExportMode::Passthrough),
        ("trimmed", false, ExportMode::SingleClipAudioCopy),
        ("silent", false, ExportMode::Encode),
        ("two", false, ExportMode::Encode),
        ("two", true, ExportMode::StreamCopy),
        ("whole", true, ExportMode::Passthrough),
    ] {
        let clips = match edit {
            "whole" => vec![c("a", 0.0, 10.0, 0.0)],
            "trimmed" => vec![c("a", 1.0, 4.0, 200.0)],
            "silent" => vec![c("a", 1.0, 4.0, -20000.0)],
            _ => vec![c("a", 1.0, 3.0, 0.0), c("a", 5.0, 6.0, 0.0)],
        };
        let plan =
            planner::plan(&job(if copy { Preset::plane_copy() } else { encode() }, vec![("a", media(10.0, MP4, 0.0, "h264"))], clips))
                .unwrap();
        assert_eq!(plan.mode, expected, "{edit} {copy}");
        assert_eq!(plan.output_path, "/out/edit.mp4");
        assert_eq!(plan.work_directory, "/work");
    }
}

#[test]
fn stream_copy_copies_matching_audio_and_shifts_filtered_audio() {
    let m = || vec![("a", media(10.0, MP4, 0.0, "h264"))];
    let copied = planner::plan(&job(Preset::plane_copy(), m(), vec![c("a", 1.0, 3.0, 0.0), c("a", 5.0, 6.0, 0.0)])).unwrap();
    let last = copied.steps.last().unwrap();
    assert!(last.shift.is_none());
    assert!(last.arguments.contains(&"0:1".to_string()));
    let shifted = planner::plan(&job(Preset::plane_copy(), m(), vec![c("a", 1.0, 3.0, 0.0), c("a", 5.0, 6.0, 40.0)])).unwrap();
    let last = shifted.steps.last().unwrap();
    assert_eq!(last.shift.as_ref().unwrap().list_path, "/work/segments.ffconcat");
    assert!(last.arguments.iter().any(|a| a.contains("PTS+$SHIFT/TB")));
    assert!(last.arguments_for("-0.077000").iter().any(|a| a.contains("PTS+-0.077000/TB")));
    assert!(shifted.notes.iter().any(|n| n.contains("offsets")));
}

#[test]
fn refuses_what_it_cannot_export() {
    let both = || vec![("a", media(10.0, MP4, 0.0, "h264")), ("b", media(10.0, MP4, 0.0, "hevc"))];
    let refusal = |j: ExportJob| planner::plan(&j).unwrap_err().0;
    assert!(refusal(job(encode(), both(), vec![])).contains("empty"));
    assert!(refusal(job(Preset::plane_copy(), both(), vec![c("a", 1.0, 3.0, 0.0), c("b", 1.0, 3.0, 0.0)])).contains("same format"));
    let mut missing = job(encode(), vec![("a", media(10.0, MP4, 0.0, "h264"))], vec![c("missing", 1.0, 3.0, 0.0)]);
    missing.state = ProjectState::new(vec![source("missing", "/m/missing.src")], Timeline::new(vec![c("missing", 1.0, 3.0, 0.0)]), vec![]);
    assert!(refusal(missing).contains("analyzed"));
    let mut no_keys = job(Preset::plane_copy(), both(), vec![c("a", 1.0, 3.0, 0.0)]);
    no_keys.keyframes = HashMap::new();
    assert!(refusal(no_keys).contains("Keyframes"));
}

#[test]
fn encode_snapshot() {
    let plan =
        planner::plan(&job(encode(), vec![("a", media(10.0, MP4, 0.0, "h264"))], vec![c("a", 1.0, 3.0, 0.0), c("a", 5.0, 6.0, 120.0)]))
            .unwrap();
    assert_eq!(
        plan.command_text("ffmpeg"),
        "ffmpeg -nostdin -hide_banner -loglevel error -nostats -stats_period 0.5 -progress pipe:1 -y -ss 1.000000 -t 2.000000 -i /m/a.src -ss 5.000000 -t 1.000000 -i /m/a.src -ss 1.000000 -t 2.000000 -i /m/a.src -ss 4.880000 -t 1.000000 -i /m/a.src -filter_complex '[0:0]trim=duration=2.000000,setpts=PTS-STARTPTS[v0];[1:0]trim=duration=1.000000,setpts=PTS-STARTPTS[v1];[2:1]aformat=sample_rates=48000:channel_layouts=stereo,apad=whole_dur=2.000000,atrim=duration=2.000000,asetpts=N/SR/TB[a0];[3:1]aformat=sample_rates=48000:channel_layouts=stereo,apad=whole_dur=1.000000,atrim=duration=1.000000,asetpts=N/SR/TB[a1];[v0][a0][v1][a1]concat=n=2:v=1:a=1[vout][aout]' -map '[vout]' -map '[aout]' -c:v libx264 -crf 20 -c:a aac -b:a 256k -movflags +faststart /out/edit.mp4"
    );
}

#[test]
fn transport_stream_copy_with_an_offset_snapshot() {
    let plan = planner::plan(&job(
        Preset::plane_copy(),
        vec![("t", media(10.0, "mpegts", 1.4, "h264"))],
        vec![c("t", 0.0, 3.0, 0.0), c("t", 5.5, 8.0, 40.0)],
    ))
    .unwrap();
    assert_eq!(
        plan.command_text("ffmpeg"),
        r#"# /work/segments.ffconcat:
# ffconcat version 1.0
# file '/work/segment000.mkv'
# duration 3.000000
# file '/work/segment001.mkv'
# duration 3.000000

ffmpeg -nostdin -hide_banner -loglevel error -nostats -stats_period 0.5 -progress pipe:1 -y -ss 0.000000 -i /m/t.src -map 0:0 -c copy -ss -0.083334 -t 3.016666 -f matroska /work/segment000.mkv

ffmpeg -nostdin -hide_banner -loglevel error -nostats -stats_period 0.5 -progress pipe:1 -y -ss 0.000000 -i /m/t.src -map 0:0 -c copy -ss 4.916666 -t 3.016666 -f matroska /work/segment001.mkv

# $SHIFT: minus the start time of /work/segments.ffconcat, probed before this runs
ffmpeg -nostdin -hide_banner -loglevel error -nostats -stats_period 0.5 -progress pipe:1 -y -f concat -safe 0 -i /work/segments.ffconcat -ss 0.000000 -t 3.000000 -i /m/t.src -ss 0.000000 -t 7.960000 -i /m/t.src -filter_complex '[1:1]atrim=start=0.000000:duration=3.000000,asetpts=PTS-STARTPTS,aformat=sample_rates=48000:channel_layouts=stereo,apad=whole_dur=3.000000,atrim=duration=3.000000,asetpts=N/SR/TB[a0];[2:1]atrim=start=4.960000:duration=3.000000,asetpts=PTS-STARTPTS,aformat=sample_rates=48000:channel_layouts=stereo,apad=whole_dur=3.000000,atrim=duration=3.000000,asetpts=N/SR/TB[a1];[a0][a1]concat=n=2:v=0:a=1[ashift];[ashift]asetpts=PTS+$SHIFT/TB[aout]' -map 0:0 -map '[aout]' -c:v copy -c:a aac -b:a 256k -movflags +faststart /out/edit.mp4"#
    );
}

fn tone(seconds: f64, rate: i32, channels: i32, layout: &str, codec: &str, format: &str) -> MediaInfo {
    MediaInfo {
        duration: s(seconds),
        format_name: format.into(),
        audio_streams: vec![AudioStreamInfo {
            index: 0,
            codec: codec.into(),
            sample_rate: rate,
            channels,
            channel_layout: layout.into(),
            duration: Some(s(seconds)),
            ..Default::default()
        }],
        ..Default::default()
    }
}

fn rc(in_s: f64, out_s: f64, looped: bool, offset: f64) -> Clip {
    Clip {
        replaced_audio: Some(AudioReplacement { file_id: "t".into(), anchor: s(in_s), offset: s(offset), looped }),
        ..Clip::create("a", s(in_s), s(out_s))
    }
}

fn rjob(preset: Preset, media: Vec<(&str, MediaInfo)>, clips: Vec<Clip>) -> ExportJob {
    ExportJob {
        state: ProjectState::new(vec![source("a", "/m/a.src")], Timeline::new(clips), vec![source("t", "/m/t.m4a")]),
        media: media.into_iter().map(|(id, m)| (id.to_string(), m)).collect(),
        preset,
        output_path: "/out/edit.mp4".into(),
        work_directory: "/work".into(),
        keyframes: [("a".to_string(), KeyframeIndex { times: (0..10).map(|i| s(i as f64)).collect(), reorder_delay: Time::ZERO })].into(),
    }
}

fn both() -> Vec<(&'static str, MediaInfo)> {
    vec![("a", media(10.0, MP4, 0.0, "h264")), ("t", tone(3.0, 44100, 2, "stereo", "aac", MP4))]
}

#[test]
fn replaced_audio_is_never_copied() {
    assert_eq!(planner::plan(&rjob(encode(), both(), vec![rc(0.0, 10.0, false, 0.0)])).unwrap().mode, ExportMode::Encode);
    let single = planner::plan(&rjob(encode(), both(), vec![rc(1.0, 4.0, false, 0.0)])).unwrap();
    assert_eq!(single.mode, ExportMode::Encode);
    assert!(single.notes.iter().any(|n| n.contains("replaced audio")));
    let copy = planner::plan(&rjob(Preset::plane_copy(), both(), vec![rc(1.0, 4.0, false, 0.0), rc(5.0, 6.0, false, 0.0)])).unwrap();
    assert!(copy.steps.last().unwrap().shift.is_some());
    assert!(copy.steps.last().unwrap().arguments.iter().any(|a| a == "/m/t.m4a"));
}

#[test]
fn refuses_an_audio_file_that_is_not_analyzed() {
    let e = planner::plan(&rjob(encode(), vec![("a", media(10.0, MP4, 0.0, "h264"))], vec![rc(1.0, 4.0, false, 0.0)])).unwrap_err();
    assert!(e.0.contains("t.m4a"));
}

#[test]
fn looped_audio_that_does_not_wrap_is_read_not_silenced() {
    for (in_s, out_s, anchor) in [(7.5, 8.5, 1.0), (1.0, 2.0, 3.5)] {
        let cl = Clip {
            replaced_audio: Some(AudioReplacement { file_id: "t".into(), anchor: s(anchor), offset: Time::ZERO, looped: true }),
            ..Clip::create("a", s(in_s), s(out_s))
        };
        let args = planner::plan(&rjob(encode(), both(), vec![cl])).unwrap().command_text("ffmpeg");
        assert!(!args.contains("anullsrc"));
        assert!(args.contains("-t 1.500000 -i /m/t.m4a"), "{args}");
        assert!(args.contains("[1:0]atrim=start=0.500000:duration=1.000000,asetpts=PTS-STARTPTS,aformat=sample_rates=44100:channel_layouts=stereo,apad=whole_dur=1.000000"));
    }
}

#[test]
fn a_mono_low_rate_file_does_not_downgrade_the_other_clips() {
    let voice = tone(3.0, 22050, 1, "mono", "mp3", "mp3");
    let own = Clip::create("a", s(5.0), s(6.0));
    let args =
        planner::plan(&rjob(encode(), vec![("a", media(10.0, MP4, 0.0, "h264")), ("t", voice)], vec![rc(1.0, 4.0, false, 0.0), own]))
            .unwrap()
            .command_text("ffmpeg");
    assert!(!args.contains("sample_rates=22050"));
    assert!(!args.contains("channel_layouts=mono"));
    assert_eq!(args.matches("aformat=sample_rates=48000:channel_layouts=stereo").count(), 2);
}

#[test]
fn a_short_file_looped_many_times_is_repeated_in_the_graph() {
    let blip = tone(0.25, 44100, 2, "stereo", "aac", MP4);
    let args = planner::plan(&rjob(encode(), vec![("a", media(10.0, MP4, 0.0, "h264")), ("t", blip)], vec![rc(1.0, 8.0, true, 0.0)]))
        .unwrap()
        .command_text("ffmpeg");
    assert!(args.contains("aloop=loop=-1:size=11025"));
    assert_eq!(args.matches(" -i /m/t.m4a").count(), 1);
}

#[test]
fn looped_snapshot() {
    let plan = planner::plan(&rjob(encode(), both(), vec![rc(1.0, 8.0, true, 0.5)])).unwrap();
    assert_eq!(
        plan.command_text("ffmpeg"),
        "ffmpeg -nostdin -hide_banner -loglevel error -nostats -stats_period 0.5 -progress pipe:1 -y -ss 1.000000 -t 7.000000 -i /m/a.src -t 3.000000 -i /m/t.m4a -t 3.000000 -i /m/t.m4a -t 3.000000 -i /m/t.m4a -t 0.500000 -i /m/t.m4a -filter_complex '[0:0]trim=duration=7.000000,setpts=PTS-STARTPTS[v0];[1:0]atrim=start=2.500000:duration=0.500000,asetpts=PTS-STARTPTS,aformat=sample_rates=44100:channel_layouts=stereo,apad=whole_len=22050,atrim=end_sample=22050[lp1];[2:0]aformat=sample_rates=44100:channel_layouts=stereo,apad=whole_len=132300,atrim=end_sample=132300[lp2];[3:0]aformat=sample_rates=44100:channel_layouts=stereo,apad=whole_len=132300,atrim=end_sample=132300[lp3];[4:0]aformat=sample_rates=44100:channel_layouts=stereo,apad=whole_len=22050,atrim=end_sample=22050[lp4];[lp1][lp2][lp3][lp4]concat=n=4:v=0:a=1,asetpts=N/SR/TB[a0];[v0][a0]concat=n=1:v=1:a=1[vout][aout]' -map '[vout]' -map '[aout]' -c:v libx264 -crf 20 -c:a aac -b:a 256k -movflags +faststart /out/edit.mp4"
    );
}
