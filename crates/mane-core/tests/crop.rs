mod common;
use common::*;
use mane_core::ffmpeg::export_planner::{self as planner, ExportJob, ExportMode, ExportPlan};
use mane_core::media::keyframes::KeyframeIndex;
use mane_core::model::*;
use mane_core::persistence::project_file;
use std::path::Path;

fn near(a: f64, b: f64) -> bool {
    (a - b).abs() < 1e-6
}

#[test]
fn pixels_are_even_and_inside_the_frame() {
    assert_eq!(CropRect::new(0.25, 0.0, 0.5, 1.0).to_pixels(1920, 1080), PixelCrop { x: 480, y: 0, width: 960, height: 1080 });
    let third = 1.0 / 3.0;
    assert_eq!(CropRect::new(third, third, third, third).to_pixels(1920, 1080), PixelCrop { x: 640, y: 360, width: 640, height: 360 });
    let odd = CropRect::new(0.5, 0.5, 0.5, 0.5).to_pixels(1279, 719);
    assert!(odd.x % 2 == 0 && odd.y % 2 == 0 && odd.width % 2 == 0 && odd.height % 2 == 0);
    assert!(odd.x + odd.width <= 1278 && odd.y + odd.height <= 718);
    assert_eq!(CropRect::new(0.999, 0.0, 0.0001, 1.0).to_pixels(100, 100).width, 2);
    assert_eq!(PixelCrop { x: 480, y: 0, width: 960, height: 1080 }.filter(), "crop=960:1080:480:0");
}

#[test]
fn clamping_keeps_the_crop_inside_the_frame() {
    assert_eq!(CropRect::new(0.7, -0.2, 0.5, 1.5).clamped(), Some(CropRect::new(0.5, 0.0, 0.5, 1.0)));
    assert_eq!(CropRect::new(0.5, 0.5, 0.0, 0.0).clamped().unwrap().width, CropRect::MIN_SIZE);
    assert!(CropRect::new(f64::NAN, 0.0, 1.0, 1.0).clamped().is_none());
    assert!(CropRect::new(0.0, 0.0, 1.0, 1.0).is_full());
    assert!(!CropRect::new(0.0, 0.0, 0.99, 1.0).is_full());
}

#[test]
fn reshaping_keeps_the_center_and_fits_the_frame() {
    let wide = 16.0 / 9.0;
    let sq = CropRect::FULL.with_aspect(1.0, wide);
    assert!(near(sq.width, 0.5625) && near(sq.height, 1.0) && near(sq.x, 0.21875));
    assert!(near(sq.width / sq.height * wide, 1.0));
    let corner = CropRect::new(0.0, 0.0, 0.2, 0.2).with_aspect(9.0 / 16.0, wide);
    assert!(near(corner.width / corner.height * wide, 9.0 / 16.0));
    assert!(near(corner.width * corner.height, 0.04));
    assert!(corner.x >= 0.0 && corner.y >= 0.0);
}

#[test]
fn a_crop_of_the_whole_frame_is_no_crop() {
    let state = ProjectState::empty();
    assert!(state.with_crop(Some(CropRect::FULL)).crop().is_none());
    assert!(state.with_crop(Some(CropRect::new(-0.1, -0.1, 1.3, 1.3))).crop().is_none());
    let cropped = state.with_crop(Some(CropRect::new(0.1, 0.1, 0.5, 0.5)));
    assert!(cropped.crop().is_some());
    assert_ne!(state, cropped);
    assert_eq!(cropped, state.with_crop(Some(CropRect::new(0.1, 0.1, 0.5, 0.5))));
    assert_eq!(state.with_crop(Some(CropRect::new(0.8, 0.0, 0.5, 1.0))).crop(), Some(CropRect::new(0.5, 0.0, 0.5, 1.0)));
    assert!(state.with_crop(Some(CropRect::new(f64::NAN, 0.0, 1.0, 1.0))).crop().is_none());
}

fn media(width: i32, height: i32, rotation: i32, sar: Rational) -> MediaInfo {
    MediaInfo {
        duration: s(10.0),
        format_name: "mov,mp4,m4a,3gp,3g2,mj2".into(),
        video: Some(VideoStreamInfo {
            index: 0,
            codec: "h264".into(),
            width,
            height,
            frame_rate: Rational::new(30, 1),
            pixel_format: "yuv420p".into(),
            rotation,
            sample_aspect_ratio: sar,
            ..Default::default()
        }),
        audio_streams: vec![AudioStreamInfo {
            index: 1,
            codec: "aac".into(),
            sample_rate: 48000,
            channels: 2,
            channel_layout: "stereo".into(),
            duration: Some(s(10.0)),
            ..Default::default()
        }],
        ..Default::default()
    }
}

fn hd() -> MediaInfo {
    media(1920, 1080, 0, Rational::ONE)
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

const LEFT_HALF: CropRect = CropRect::new(0.0, 0.0, 0.5, 1.0);

fn job(preset: Preset, media: Vec<(&str, MediaInfo)>, crop: Option<CropRect>, clips: Vec<Clip>) -> ExportJob {
    ExportJob {
        state: ProjectState::new(media.iter().map(|(id, _)| source(id, &format!("/m/{id}.src"))).collect(), Timeline::new(clips), vec![])
            .with_crop(crop),
        keyframes: media
            .iter()
            .map(|(id, _)| (id.to_string(), KeyframeIndex { times: (0..10).map(|i| s(i as f64)).collect(), reorder_delay: Time::ZERO }))
            .collect(),
        media: media.into_iter().map(|(id, m)| (id.to_string(), m)).collect(),
        preset,
        output_path: "/out/edit.mp4".into(),
        work_directory: "/work".into(),
    }
}

fn cmd(plan: &ExportPlan) -> String {
    plan.command_text("ffmpeg")
}

fn cl(src: &str, a: f64, b: f64) -> Clip {
    Clip::create(src, s(a), s(b))
}

#[test]
fn a_whole_file_is_cropped_by_the_preset_run() {
    let plan = planner::plan(&job(encode(), vec![("a", hd())], Some(LEFT_HALF), vec![cl("a", 0.0, 10.0)])).unwrap();
    assert_eq!(plan.mode, ExportMode::Passthrough);
    assert!(cmd(&plan).contains("-vf crop=960:1080:0:0 -c:v libx264"));
    assert!(plan.notes.iter().any(|n| n.contains("960×1080")));
}

#[test]
fn the_crop_comes_before_the_preset_filter() {
    let preset = Preset { arguments: "-vf scale=640:-2 -c:v libx264 -c:a copy".into(), ..encode() };
    let single = planner::plan(&job(preset.clone(), vec![("a", hd())], Some(LEFT_HALF), vec![cl("a", 1.0, 4.0)])).unwrap();
    assert_eq!(single.mode, ExportMode::SingleClipAudioCopy);
    assert!(cmd(&single).contains("-vf crop=960:1080:0:0,scale=640:-2"));
    let joined = planner::plan(&job(preset, vec![("a", hd())], Some(LEFT_HALF), vec![cl("a", 1.0, 3.0), cl("a", 5.0, 6.0)])).unwrap();
    assert_eq!(joined.mode, ExportMode::Encode);
    assert!(cmd(&joined).contains("concat=n=2:v=1:a=1[vcat][aout];[vcat]crop=960:1080:0:0,scale=640:-2[vout]"));
}

#[test]
fn joined_clips_are_cropped_after_being_fitted_to_the_first() {
    let m = vec![("a", media(1280, 720, 0, Rational::ONE)), ("b", hd())];
    let plan = planner::plan(&job(encode(), m, Some(LEFT_HALF), vec![cl("a", 1.0, 3.0), cl("b", 1.0, 3.0)])).unwrap();
    assert!(cmd(&plan).contains("scale=1280:720"));
    assert!(cmd(&plan).contains("[vcat]crop=640:720:0:0[vout]"));
}

#[test]
fn a_portrait_phone_video_is_cropped_upright() {
    let plan = planner::plan(&job(
        encode(),
        vec![("a", media(1920, 1080, 90, Rational::ONE))],
        Some(CropRect::new(0.0, 0.25, 1.0, 0.5)),
        vec![cl("a", 0.0, 10.0)],
    ))
    .unwrap();
    assert!(cmd(&plan).contains("crop=1080:960:0:480"));
}

#[test]
fn the_frame_the_preview_shows_is_the_frame_the_export_crops() {
    let ana = || media(1440, 1080, 0, Rational::new(4, 3));
    for m in [
        vec![("a", ana())],
        vec![("a", media(1279, 719, 0, Rational::ONE))],
        vec![("a", media(1920, 1080, 90, Rational::ONE))],
        vec![("a", media(1280, 720, 0, Rational::ONE)), ("b", media(640, 480, 0, Rational::ONE))],
        vec![("a", ana()), ("b", hd())],
    ] {
        let clips: Vec<Clip> = m.iter().map(|(id, _)| cl(id, 1.0, 3.0)).collect();
        let videos: Vec<&VideoStreamInfo> = m.iter().map(|(_, i)| i.video.as_ref().unwrap()).collect();
        let frame = OutputFrame::of(&videos);
        let crop = CropRect::new(0.1, 0.2, 0.5, 0.6);
        let plan = planner::plan(&job(encode(), m.clone(), Some(crop), clips)).unwrap();
        assert!(cmd(&plan).contains(&frame.pixels(&crop).filter()));
        assert!(plan.notes.iter().any(|n| n.contains(&format!("of the {}×{} frame", frame.width, frame.height))));
    }
}

#[test]
fn an_anamorphic_frame_is_cropped_in_its_stored_pixels() {
    let ana = media(1440, 1080, 0, Rational::new(4, 3)).video.unwrap();
    let small = media(1280, 720, 0, Rational::ONE).video.unwrap();
    assert_eq!(OutputFrame::of(&[&ana]), OutputFrame { width: 1440, height: 1080, aspect: 16.0 / 9.0, normalized: false });
    assert_eq!(OutputFrame::of(&[&ana, &small]), OutputFrame { width: 1920, height: 1080, aspect: 16.0 / 9.0, normalized: true });
    assert_eq!(OutputFrame::of(&[&ana]).pixels(&LEFT_HALF), PixelCrop { x: 0, y: 0, width: 720, height: 1080 });
}

#[test]
fn a_clip_of_another_shape_is_fitted_and_centered_in_the_frame() {
    assert_eq!(OutputFrame::picture_area(1.0, 16.0 / 9.0), CropRect::new(0.21875, 0.0, 0.5625, 1.0));
    let wide = OutputFrame::picture_area(21.0 / 9.0, 16.0 / 9.0);
    assert_eq!((wide.x, wide.width), (0.0, 1.0));
    assert!(near(wide.height, 16.0 / 21.0));
    assert!(near(wide.y, (1.0 - wide.height) / 2.0));
    assert_eq!(OutputFrame::picture_area(16.0 / 9.0, 16.0 / 9.0), CropRect::FULL);
    assert_eq!(OutputFrame::picture_area(0.0, 16.0 / 9.0), CropRect::FULL);
    let a = media(1280, 720, 0, Rational::ONE);
    let b = media(1080, 1080, 0, Rational::ONE);
    let frame = OutputFrame::of(&[a.video.as_ref().unwrap(), b.video.as_ref().unwrap()]);
    let plan = planner::plan(&job(encode(), vec![("a", a.clone()), ("b", b)], None, vec![cl("a", 1.0, 3.0), cl("b", 1.0, 3.0)])).unwrap();
    assert!(cmd(&plan).contains(&frame.fit_filter()));
    assert_eq!(OutputFrame::of(&[a.video.as_ref().unwrap()]).fit_filter(), "");
}

#[test]
fn stream_copy_refuses_a_crop() {
    let e = planner::plan(&job(Preset::plane_copy(), vec![("a", hd())], Some(LEFT_HALF), vec![cl("a", 0.0, 10.0)])).unwrap_err();
    assert!(e.0.contains("can't be cropped"));
    assert_eq!(
        planner::plan(&job(Preset::plane_copy(), vec![("a", hd())], None, vec![cl("a", 0.0, 10.0)])).unwrap().mode,
        ExportMode::Passthrough
    );
}

#[test]
fn no_crop_leaves_the_commands_as_they_were() {
    let plan = planner::plan(&job(encode(), vec![("a", hd())], None, vec![cl("a", 1.0, 3.0), cl("a", 5.0, 6.0)])).unwrap();
    assert!(!cmd(&plan).contains("crop"));
    assert!(cmd(&plan).contains("concat=n=2:v=1:a=1[vout][aout]"));
}

#[test]
fn projects_keep_the_crop_and_say_they_need_a_newer_app() {
    let state = ProjectState::new(vec![source("s", "/m/a.mp4")], Timeline::new(vec![cl("s", 0.0, 2.0)]), vec![])
        .with_crop(Some(CropRect::new(0.125, 0.25, 0.5, 0.625)));
    let p = Path::new("/p/x.mfvp");
    let json = project_file::serialize(&Project { state: state.clone(), ..Default::default() }, p);
    assert!(json.contains("\"version\": 3"));
    assert!(json.contains("\"crop\""));
    assert_eq!(project_file::deserialize(&json, p).unwrap().state.crop(), state.crop());
    let plain = project_file::serialize(&Project { state: state.with_crop(None), ..Default::default() }, p);
    assert!(plain.contains("\"version\": 1"));
    assert!(!plain.contains("\"crop\""));
}

#[test]
fn a_hand_edited_crop_is_clamped_or_dropped() {
    let h = "\"format\": \"mane-ffmpeg-project\", \"version\": 3";
    let p = Path::new("/p/x.mfvp");
    let outside =
        project_file::deserialize(&format!("{{ {h}, \"crop\": {{ \"x\": 0.8, \"y\": 0, \"width\": 0.5, \"height\": 1 }} }}"), p).unwrap();
    assert_eq!(outside.state.crop(), Some(CropRect::new(0.5, 0.0, 0.5, 1.0)));
    let whole = project_file::deserialize(&format!("{{ {h}, \"crop\": {{ \"x\": 0 }} }}"), p).unwrap();
    assert!(whole.state.crop().is_none());
}
