mod common;
use common::*;
use mane_core::editing::timeline_snap;
use mane_core::ffmpeg::preset_analysis::PresetAnalysis;
use mane_core::ffmpeg::progress::ProgressParser;
use mane_core::model::*;
use mane_core::persistence::presets::PresetStore;
use mane_core::persistence::project_file;
use mane_core::persistence::settings::{SettingsStore, ThemeChoice};
use mane_core::platform::file_association;
use std::path::Path;

fn snap(tl: &Timeline, at: f64, radius: f64, end: &dyn Fn(&Clip) -> Option<Time>) -> Option<Time> {
    timeline_snap::nearest(tl, s(at), s(radius), end)
}

fn c(id: &str, in_s: f64, out_s: f64, offset_s: f64) -> Clip {
    Clip { audio_offset: s(offset_s), ..clip(id, "s", in_s, out_s) }
}

#[test]
fn snaps_to_the_nearest_edge_within_the_radius_only() {
    let tl = Timeline::new(vec![c("a", 0.0, 2.0, 0.0), c("b", 0.0, 2.0, 0.0)]);
    let none = |_: &Clip| None;
    assert_eq!(snap(&tl, 0.05, 0.1, &none), Some(s(0.0)));
    assert_eq!(snap(&tl, 1.95, 0.1, &none), Some(s(2.0)));
    assert_eq!(snap(&tl, 4.08, 0.1, &none), Some(s(4.0)));
    assert_eq!(snap(&tl, 1.0, 0.1, &none), None);
    assert_eq!(snap(&tl, 4.2, 0.1, &none), None);
}

#[test]
fn audio_edges_inside_a_clip_compete_with_its_edges() {
    let tl = Timeline::new(vec![c("a", 0.0, 2.0, 0.1)]);
    let end = |_: &Clip| Some(s(1.5));
    assert_eq!(snap(&tl, 0.08, 0.3, &end), Some(s(0.1)));
    assert_eq!(snap(&tl, 1.75, 0.3, &end), Some(s(1.6)));
    assert_eq!(snap(&tl, 1.85, 0.3, &end), Some(s(2.0)));
    assert_eq!(snap(&tl, 1.75, 0.3, &|_| None), Some(s(2.0)));
}

#[test]
fn only_edges_between_sound_and_silence_count() {
    let late = Timeline::new(vec![c("a", 0.2, 2.2, 0.6)]);
    assert_eq!(snap(&late, 0.5, 0.3, &|_| Some(s(60.0))), Some(s(0.4)));
    assert_eq!(snap(&late, 1.5, 0.3, &|_| Some(s(60.0))), None);
    let silent = Timeline::new(vec![c("b", 5.0, 7.0, 0.0)]);
    assert_eq!(snap(&silent, 1.0, 0.3, &|_| Some(s(0.5))), None);
}

const HEADER: &str = "\"format\": \"mane-ffmpeg-project\", \"version\": 1";

#[test]
fn missing_lists_and_entries_load_as_empty() {
    let empty = project_file::deserialize(&format!("{{ {HEADER}, \"sources\": null, \"clips\": null }}"), Path::new("/p/x.mfvp")).unwrap();
    assert!(empty.state.sources.is_empty());
    assert!(empty.state.timeline.clips().is_empty());
    let partial = project_file::deserialize(
        &format!(
            r#"{{ {HEADER},
              "sources": [null, {{ "id": "s", "path": "/m/a.mp4" }}, {{ "id": null, "path": "/m/b.mp4" }}],
              "clips": [null, {{ "id": "c", "source": "s", "in": "00:00:01", "out": "00:00:02" }}, {{ "id": "d", "source": null }}] }}"#
        ),
        Path::new("/p/x.mfvp"),
    );
    // A null id is a damaged entry in .NET too only when it can't be read; here null strings are skipped.
    let partial = partial.unwrap();
    assert_eq!(partial.state.sources.len(), 1);
    assert_eq!(partial.state.sources[0].id, "s");
    assert_eq!(partial.state.timeline.clips().len(), 1);
    assert_eq!(partial.state.timeline.clips()[0].id, "c");
}

#[test]
fn any_unreadable_file_is_a_project_file_error() {
    assert!(project_file::deserialize(&format!("{{ {HEADER}, \"clips\": [{{ \"in\": \"soon\" }}] }}"), Path::new("/p/x.mfvp")).is_err());
    assert!(
        project_file::deserialize(
            &format!("{{ {HEADER}, \"sources\": [{{ \"id\": \"s\", \"path\": \"/gone.mp4\", \"relativePath\": \"a\\u0000b\" }}] }}"),
            Path::new("/p/x.mfvp")
        )
        .is_err()
    );
}

#[test]
fn round_trips_edits_and_layout_with_relative_paths() {
    let dir = tempfile::tempdir().unwrap();
    let media = dir.path().join("media").join("clip 'one'.mp4");
    std::fs::create_dir_all(media.parent().unwrap()).unwrap();
    std::fs::write(&media, "x").unwrap();
    let project = Project {
        state: ProjectState::new(
            vec![SourceMedia { file_size: 1, audio_stream_index: 1, ..source("s1", &media.to_string_lossy()) }],
            Timeline::new(vec![Clip { audio_offset: ms(-80.0), ..clip("c1", "s1", 1.5, 4.0) }]),
            vec![],
        ),
        export: ExportSettings {
            preset_id: Some(Preset::for_storage().id),
            preset_snapshot: Some(Preset::for_storage()),
            output_path: Some("/tmp/out.mp4".into()),
        },
        layout: ProjectLayout {
            timeline_zoom: 42.5,
            playhead: s(3.0),
            window: Some(WindowBounds { x: 10, y: 20, width: 1200.0, height: 800.0, maximized: false }),
            ..Default::default()
        },
    };
    let path = dir.path().join("a.mfvp");
    let json = project_file::serialize(&project, &path);
    assert!(json.contains("\"relativePath\": \"media/clip 'one'.mp4\""), "{json}");
    let moved = tempfile::tempdir().unwrap();
    let moved_dir = moved.path().join("moved");
    std::fs::rename(dir.path(), &moved_dir).unwrap();
    let loaded = project_file::deserialize(&json, &moved_dir.join("a.mfvp")).unwrap();
    assert_eq!(loaded.state.sources[0].path, moved_dir.join("media").join("clip 'one'.mp4").to_string_lossy());
    assert_eq!(loaded.state.timeline.clips()[0], project.state.timeline.clips()[0]);
    assert_eq!(loaded.state.sources[0].audio_stream_index, 1);
    assert_eq!(loaded.export, project.export);
    assert_eq!(loaded.layout, project.layout);
    std::mem::forget(dir);
}

#[test]
fn reads_a_project_written_by_the_dotnet_app() {
    let json = r#"{
  "format": "mane-ffmpeg-project",
  "version": 3,
  "sources": [ { "id": "a1", "path": "/m/a.mp4", "relativePath": "a.mp4", "fileSize": 10, "lastWriteUtc": "2026-10-26T12:34:56.1234567Z", "audioStream": 0 } ],
  "audioFiles": [ { "id": "f1", "path": "/m/s.mp3", "relativePath": "s.mp3", "fileSize": 5, "lastWriteUtc": "2026-10-26T12:00:00Z", "audioStream": 0 } ],
  "clips": [ { "id": "c1", "source": "a1", "in": "00:00:01.5000000", "out": "00:00:04", "audioOffset": "-00:00:00.0800000",
               "replacedAudio": { "file": "f1", "anchor": "00:00:01.5000000", "offset": "00:00:00", "loop": true } } ],
  "crop": { "x": 0.1, "y": 0.2, "width": 0.5, "height": 0.5 },
  "export": { "presetId": "builtin-share", "outputPath": "/m/out.mp4" },
  "layout": { "leftPaneWidth": 320, "timelineZoom": 0, "timelineScroll": 0, "playhead": "00:00:02", "volume": 80, "compact": true }
}"#;
    let p = project_file::deserialize(json, Path::new("/m/p.mfvp")).unwrap();
    let c = &p.state.timeline.clips()[0];
    assert_eq!(c.r#in, s(1.5));
    assert_eq!(c.audio_offset, ms(-80.0));
    assert!(c.replaced_audio.as_ref().unwrap().looped);
    assert_eq!(p.state.crop(), Some(CropRect::new(0.1, 0.2, 0.5, 0.5)));
    assert_eq!(p.state.sources[0].last_write_utc.iso(), "2026-10-26T12:34:56.1234567Z");
    assert!(p.layout.compact);
    assert_eq!(p.layout.volume, 80.0);
    let again = project_file::serialize(&p, Path::new("/m/p.mfvp"));
    assert!(again.contains("\"version\": 3"));
    assert!(again.contains("\"in\": \"00:00:01.5000000\""));
}

#[test]
fn rejects_other_json_and_newer_versions() {
    assert!(project_file::deserialize("{\"hello\":1}", Path::new("/tmp/x.mfvp")).is_err());
    assert!(project_file::deserialize("{\"format\":\"mane-ffmpeg-project\",\"version\":99}", Path::new("/tmp/x.mfvp")).is_err());
}

fn names(store: &PresetStore) -> Vec<&str> {
    store.presets().iter().map(|p| p.name.as_str()).collect()
}

fn temp_presets(content: Option<&str>) -> (tempfile::TempDir, std::path::PathBuf) {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("presets.json");
    if let Some(c) = content {
        std::fs::write(&path, c).unwrap();
    }
    (dir, path)
}

#[test]
fn new_store_has_built_ins_with_for_storage_first() {
    let (_d, path) = temp_presets(None);
    let store = PresetStore::new(Some(path));
    assert_eq!(
        names(&store),
        ["for storage (balanced)", "for share (compatibility)", "harsh compression", "copy (w/o encode)", "copy to mp4 (w/o encode)"]
    );
    assert!(store.presets()[0].arguments.contains("yuv420p10le"));
    assert_eq!(store.presets()[2].arguments, "-c:v libx265 -crf 28 -preset slow -c:a copy");
}

#[test]
fn moved_preset_keeps_its_place_after_reloading() {
    let (_d, path) = temp_presets(None);
    let mut store = PresetStore::new(Some(path.clone()));
    store.move_to(&Preset::plane_copy().id, 0);
    assert_eq!(
        names(&store),
        ["copy (w/o encode)", "for storage (balanced)", "for share (compatibility)", "harsh compression", "copy to mp4 (w/o encode)"]
    );
    store.move_to(&Preset::for_storage().id, 99);
    assert_eq!(
        names(&PresetStore::new(Some(path))),
        ["copy (w/o encode)", "for share (compatibility)", "harsh compression", "copy to mp4 (w/o encode)", "for storage (balanced)"]
    );
}

#[test]
fn version_1_file_gets_new_for_storage_unless_edited_and_gains_harsh() {
    for edited in [false, true] {
        let storage = if edited { "-c:v libx265 -crf 20 -c:a copy" } else { preset::FOR_STORAGE_V1_ARGUMENTS };
        let (_d, path) = temp_presets(Some(&format!(
            r#"{{ "version": 1, "presets": [
              {{ "id": "builtin-for-storage", "name": "for storage", "arguments": "{storage}", "extension": "mp4", "audioFallbackArguments": "-c:a aac -b:a 256k" }},
              {{ "id": "mine", "name": "mine", "arguments": "-c:v libx264", "extension": "mkv", "audioFallbackArguments": "-c:a aac -b:a 256k" }} ] }}"#
        )));
        let store = PresetStore::new(Some(path.clone()));
        assert_eq!(
            names(&store),
            ["for storage (balanced)", "for share (compatibility)", "harsh compression", "mine", "copy to mp4 (w/o encode)"]
        );
        assert_eq!(store.presets()[0].arguments, if edited { storage.to_string() } else { Preset::for_storage().arguments });
        assert!(std::fs::read_to_string(&path).unwrap().contains("\"version\": 4"));
        let again = PresetStore::new(Some(path));
        let a: Vec<_> = store.presets().iter().map(|p| &p.arguments).collect();
        let b: Vec<_> = again.presets().iter().map(|p| &p.arguments).collect();
        assert_eq!(a, b);
    }
}

#[test]
fn version_2_file_gains_share_after_harsh_and_keeps_the_rest() {
    let (_d, path) = temp_presets(Some(
        r#"{ "version": 2, "presets": [
          { "id": "builtin-for-storage", "name": "for storage", "arguments": "-c:v libx265 -crf 20 -c:a copy" },
          { "id": "builtin-harsh", "name": "harsh", "arguments": "-c:v libx265 -crf 28 -c:a copy" },
          { "id": "mine", "name": "mine", "arguments": "-c:v libx264" } ] }"#,
    ));
    let store = PresetStore::new(Some(path.clone()));
    assert_eq!(
        names(&store),
        ["for storage (balanced)", "for share (compatibility)", "harsh compression", "mine", "copy to mp4 (w/o encode)"]
    );
    assert_eq!(store.presets()[0].arguments, "-c:v libx265 -crf 20 -c:a copy");
    assert!(std::fs::read_to_string(&path).unwrap().contains("\"version\": 4"));
}

#[test]
fn version_3_file_renames_built_ins_unless_edited_and_moves_share_second() {
    let (_d, path) = temp_presets(Some(
        r#"{ "version": 3, "presets": [
          { "id": "builtin-for-storage", "name": "my storage", "arguments": "-c:v libx265 -crf 20 -c:a copy" },
          { "id": "builtin-harsh", "name": "harsh", "arguments": "-c:v libx265 -crf 28 -c:a copy" },
          { "id": "builtin-share", "name": "share", "arguments": "-c:v libx264" },
          { "id": "builtin-plane-copy", "name": "plane copy", "arguments": "-c:v copy -c:a copy" },
          { "id": "mine", "name": "plane copy but mp4", "arguments": "-c:v copy -c:a copy" } ] }"#,
    ));
    let mut store = PresetStore::new(Some(path.clone()));
    assert_eq!(
        names(&store),
        ["my storage", "for share (compatibility)", "harsh compression", "copy (w/o encode)", "copy to mp4 (w/o encode)"]
    );
    assert!(std::fs::read_to_string(&path).unwrap().contains("\"version\": 4"));
    assert_eq!(store.presets().last().unwrap().id, Preset::copy_to_mp4().id);
    let saved = Preset {
        id: "mine".into(),
        name: "plane copy but mp4".into(),
        arguments: "-c:v copy -c:a copy".into(),
        extension: "mp4".into(),
        audio_fallback_arguments: preset::DEFAULT_AUDIO_FALLBACK.into(),
    };
    let last = Preset { extension: "mp4".into(), ..store.presets().last().unwrap().clone() };
    store.save(last);
    assert_eq!(store.find_for(Some("mine"), Some(&saved)).map(|p| p.id.clone()), Some(Preset::copy_to_mp4().id));
    let other = Preset { arguments: "-c:v copy -c:a copy -map 0".into(), ..saved.clone() };
    assert!(store.find_for(Some("mine"), Some(&other)).is_none());
    assert!(store.find_for(Some("mine"), None).is_none());
}

#[test]
fn version_3_file_without_a_copy_to_mp4_preset_gains_the_built_in_after_copy() {
    let (_d, path) = temp_presets(Some(
        r#"{ "version": 3, "presets": [
          { "id": "builtin-plane-copy", "name": "plane copy", "arguments": "-c:v copy -c:a copy" },
          { "id": "mine", "name": "mine", "arguments": "-c:v libx264" } ] }"#,
    ));
    assert_eq!(names(&PresetStore::new(Some(path))), ["copy (w/o encode)", "copy to mp4 (w/o encode)", "mine"]);
}

#[test]
fn share_preset_is_valid_and_encodes_audio() {
    let a = PresetAnalysis::of(&Preset::share());
    assert!(a.is_valid());
    assert!(a.warnings.is_empty());
    assert_eq!(a.arguments.as_ref().unwrap().video_codec.as_deref(), Some("libx264"));
    assert!(!a.arguments.unwrap().audio_copy());
}

#[test]
fn an_unwritable_folder_keeps_changes_in_memory_and_reports_it() {
    use std::os::unix::fs::PermissionsExt;
    let dir = tempfile::tempdir().unwrap();
    let mut store = PresetStore::new(Some(dir.path().join("presets.json")));
    std::fs::set_permissions(dir.path(), std::fs::Permissions::from_mode(0o500)).unwrap();
    store.save(Preset { id: "mine".into(), name: "mine".into(), ..Preset::plane_copy() });
    assert!(store.presets().iter().any(|p| p.id == "mine"));
    let root = unsafe { libc_geteuid() } == 0;
    if !root {
        assert!(store.save_error.is_some());
    }
    let mut settings = SettingsStore::new(Some(dir.path().join("settings.json")));
    settings.add_recent_project("/p/x.mfvp");
    assert_eq!(settings.current().recent_projects, vec!["/p/x.mfvp".to_string()]);
    std::fs::set_permissions(dir.path(), std::fs::Permissions::from_mode(0o700)).unwrap();
}

unsafe extern "C" {
    #[link_name = "geteuid"]
    fn libc_geteuid() -> u32;
}

#[test]
fn settings_saved_before_a_setting_existed_get_its_default() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("settings.json");
    std::fs::write(&path, r#"{ "hardwareDecoding": false, "theme": "Light" }"#).unwrap();
    let store = SettingsStore::new(Some(path));
    let s = store.current();
    assert!(!s.hardware_decoding);
    assert_eq!(s.theme, ThemeChoice::Light);
    assert!(s.notifications);
    assert!(s.ask_to_install_at_startup);
}

#[test]
fn missing_fields_get_defaults() {
    let (_d, path) =
        temp_presets(Some(r#"{ "version": 4, "presets": [ { "id": "mine", "name": "mine", "arguments": "-c:v libx264" } ] }"#));
    let store = PresetStore::new(Some(path));
    assert_eq!(store.presets().len(), 1);
    assert_eq!(store.presets()[0].audio_fallback_arguments, preset::DEFAULT_AUDIO_FALLBACK);
    assert_eq!(store.presets()[0].extension, "");
}

#[test]
fn emits_snapshot_per_progress_line() {
    let mut parser = ProgressParser::default();
    let seen: Vec<_> = "frame=10\nout_time_us=1500000\nspeed=2.5x\nprogress=continue\nout_time_us=N/A\nspeed=N/A\nprogress=end"
        .split('\n')
        .filter_map(|l| parser.feed(l))
        .collect();
    assert_eq!(seen.len(), 2);
    assert_eq!(seen[0].position, s(1.5));
    assert_eq!(seen[0].speed, Some(2.5));
    assert!(seen[1].finished);
    assert_eq!(seen[1].speed, None);
}

#[test]
fn quotes_the_executable_for_a_desktop_entry() {
    for (path, expected) in [
        ("/opt/mane/ManeFfmpeg", "\"/opt/mane/ManeFfmpeg\""),
        ("/home/a b/Mane FFmpeg/ManeFfmpeg", "\"/home/a b/Mane FFmpeg/ManeFfmpeg\""),
        ("/x/$HOME`id`/100%/ManeFfmpeg", "\"/x/\\\\$HOME\\\\`id\\\\`/100%%/ManeFfmpeg\""),
        ("/x/a\\b\"c", "\"/x/a\\\\\\\\b\\\\\"c\""),
    ] {
        assert_eq!(file_association::exec_argument(path), expected);
    }
}

#[test]
fn removes_only_the_project_type_from_mimeapps_list() {
    let list = "[Default Applications]\nvideo/mp4=vlc.desktop\napplication/x-mane-ffmpeg-project=mane-ffmpeg.desktop\n\n[Added Associations]\napplication/x-mane-ffmpeg-project=mane-ffmpeg.desktop;\napplication/x-mane-ffmpeg-project-old=x.desktop;\n";
    assert_eq!(
        file_association::without_associations(list, "application/x-mane-ffmpeg-project"),
        "[Default Applications]\nvideo/mp4=vlc.desktop\n\n[Added Associations]\napplication/x-mane-ffmpeg-project-old=x.desktop;\n"
    );
}

#[test]
fn places_slipped_audio() {
    for (vs, len, off, end, lead, from, rl, tail) in [
        (2.0, 3.0, 120.0, 10.0, 0.0, 1.88, 3.0, 0.0),
        (0.0, 2.0, 120.0, 10.0, 0.12, 0.0, 1.88, 0.0),
        (8.0, 2.0, -500.0, 10.0, 0.0, 8.5, 1.5, 0.5),
        (0.0, 1.0, 2000.0, 10.0, 1.0, 0.0, 0.0, 0.0),
        (9.0, 1.0, -2000.0, 10.0, 0.0, 11.0, 0.0, 1.0),
        (1.0, 4.0, 3000.0, 2.5, 2.0, 0.0, 2.0, 0.0),
    ] {
        let p = AudioPlacement::compute(s(vs), s(len), ms(off), s(end));
        let near = |a: Time, b: f64| (a.total_seconds() - b).abs() < 1e-6;
        assert!(near(p.lead, lead) && near(p.read_from, from) && near(p.read_length, rl) && near(p.tail(), tail), "{vs} {off}: {p:?}");
        assert_eq!(rl == 0.0, p.is_silent());
    }
}

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

fn span(at: f64, len: f64, from: Option<f64>) -> AudioSpan {
    AudioSpan { at: s(at), length: s(len), read_from: from.map(s) }
}

#[test]
fn placed_audio_is_one_read_with_silence_around_it() {
    let l = layout(&own(1.0, 4.0, 1.5), 10.0);
    assert!(matches!(l, AudioLayout::Placed { .. }));
    assert_eq!(l.spans(), vec![span(0.0, 0.5, None), span(0.5, 2.5, Some(0.0))]);
    assert_eq!(l.edges(s(0.0), s(3.0)), vec![s(0.5)]);
    assert!(l.edges(s(1.0), s(3.0)).is_empty());
    assert_eq!(l.file_time(s(0.0)), s(-0.5));
    assert_eq!(l.file_time(s(2.0)), s(1.5));
}

#[test]
fn audio_wholly_past_the_file_is_one_silence() {
    let l = layout(&own(5.0, 7.0, 0.0), 0.5);
    assert_eq!(l.spans(), vec![span(0.0, 2.0, None)]);
    assert!(l.edges(s(0.0), s(2.0)).is_empty());
}

#[test]
fn looped_audio_repeats_without_silence_and_marks_its_seams() {
    let l = layout(&replaced(1.0, 8.0, 1.0, 0.5, true), 3.0);
    assert!(matches!(l, AudioLayout::Looped(_)));
    assert_eq!(l.spans(), vec![span(0.0, 0.5, Some(2.5)), span(0.5, 3.0, Some(0.0)), span(3.5, 3.0, Some(0.0)), span(6.5, 0.5, Some(0.0))]);
    assert_eq!(l.edges(s(0.0), s(7.0)), vec![s(0.5), s(3.5), s(6.5)]);
    assert_eq!(l.edges(s(3.0), s(4.0)), vec![s(3.5)]);
    assert_eq!(l.file_time(s(0.0)), s(2.5));
    assert_eq!(l.file_time(s(4.0)), s(0.5));
}
