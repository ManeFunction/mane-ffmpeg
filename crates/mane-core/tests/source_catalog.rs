mod fixture;
use fixture::wait_until;
use mane_core::ffmpeg::{FfmpegTools, ffprobe};
use mane_core::media::analyzers::MediaAnalyzers;
use mane_core::media::keyframes::KeyframeService;
use mane_core::media::source_catalog::{SourceCatalog, SourceStatus};
use mane_core::media::waveform::WaveformService;
use mane_core::model::{MediaInfo, SourceMedia, Time};
use std::sync::Arc;

struct Analyzers {
    tools: FfmpegTools,
    waveforms: Arc<WaveformService>,
    keyframes: Arc<KeyframeService>,
}

impl MediaAnalyzers for Analyzers {
    fn probe(&self, path: &str) -> Result<MediaInfo, String> {
        ffprobe::probe(&self.tools, path).map_err(|e| e.0)
    }
    fn waveforms(&self) -> Option<Arc<WaveformService>> {
        Some(self.waveforms.clone())
    }
    fn keyframes(&self) -> Option<Arc<KeyframeService>> {
        Some(self.keyframes.clone())
    }
}

fn catalog() -> SourceCatalog {
    let tools = fixture::tools().unwrap();
    let a = Analyzers {
        waveforms: Arc::new(WaveformService::new(tools.clone())),
        keyframes: Arc::new(KeyframeService::new(tools.clone())),
        tools,
    };
    SourceCatalog::new(Arc::new(a), Arc::new(|| {}))
}

fn src(id: &str, path: &str, stream: i32) -> SourceMedia {
    SourceMedia { id: id.into(), path: path.into(), audio_stream_index: stream, ..Default::default() }
}

#[test]
fn a_missing_file_is_reported_and_relinking_analyzes_it() {
    let Some(f) = fixture::get() else { return };
    let mut c = catalog();
    let missing = src("s", &f.directory.join("gone.mp4").to_string_lossy(), 0);
    c.sync(std::slice::from_ref(&missing), &[]);
    let mut changed = c.poll();
    assert_eq!(c.status("s"), SourceStatus::Missing);
    assert!(c.media().is_empty());
    c.sync(&[SourceMedia { path: f.sync30.clone(), ..missing }], &[]);
    wait_until(
        || {
            changed.extend(c.poll());
            c.status("s") == SourceStatus::Ready
        },
        "the relinked file to be analyzed",
    );
    assert_eq!(c.info("s").unwrap().duration.total_seconds().round(), 12.0);
    assert!(c.media().contains_key("s"));
    assert!(changed.iter().all(|id| id == "s"));
}

#[test]
fn choosing_another_audio_stream_loads_its_waveform() {
    let Some(f) = fixture::get() else { return };
    let path = f.directory.join("beep-and-silence.mkv").to_string_lossy().into_owned();
    let ok = std::process::Command::new(&f.tools.ffmpeg)
        .args([
            "-nostdin",
            "-hide_banner",
            "-loglevel",
            "error",
            "-y",
            "-i",
            &f.sync30,
            "-f",
            "lavfi",
            "-t",
            "12",
            "-i",
            "anullsrc=r=48000:cl=stereo",
        ])
        .args(["-map", "0:v", "-map", "0:a", "-map", "1:a", "-c:v", "copy", "-c:a", "flac", &path])
        .status()
        .unwrap()
        .success();
    assert!(ok);
    let mut c = catalog();
    let source = src("s", &path, 0);
    c.sync(std::slice::from_ref(&source), &[]);
    wait_until(
        || {
            c.poll();
            c.waveform(&source).is_some()
        },
        "the first stream's waveform",
    );
    assert!(c.waveform(&source).unwrap().max(Time::from_seconds(2.0), Time::from_seconds(2.1)) > 150);
    let second = SourceMedia { audio_stream_index: 1, ..source };
    c.sync(std::slice::from_ref(&second), &[]);
    assert_eq!(c.audio(&second).unwrap().index, 2);
    wait_until(
        || {
            c.poll();
            c.waveform(&second).is_some()
        },
        "the second stream's waveform",
    );
    assert!(c.waveform(&second).unwrap().max(Time::from_seconds(2.0), Time::from_seconds(2.1)) < 10);
}

#[test]
fn keyframes_are_scanned_when_wanted_or_asked_for() {
    let Some(f) = fixture::get() else { return };
    let mut c = catalog();
    let source = src("s", &f.sync30, 0);
    c.sync(std::slice::from_ref(&source), &[]);
    wait_until(
        || {
            c.poll();
            c.waveform(&source).is_some()
        },
        "the analysis",
    );
    assert!(c.keyframes(&source).is_none());
    c.set_want_keyframes(true);
    wait_until(
        || {
            c.poll();
            c.keyframes(&source).is_some()
        },
        "keyframes once wanted",
    );
    assert_eq!(c.keyframes(&source).unwrap().times.len(), 12);
    let other = src("n", &f.no_audio, 0);
    let mut lazy = catalog();
    lazy.sync(std::slice::from_ref(&other), &[]);
    wait_until(
        || {
            lazy.poll();
            lazy.status("n") == SourceStatus::Ready
        },
        "the second catalog",
    );
    let info = lazy.info("n").unwrap().clone();
    assert!(!lazy.analyzers().keyframes().unwrap().get(&other.path, &info).unwrap().times.is_empty());
    assert!(lazy.keyframes(&other).is_some());
}

#[test]
fn a_removed_source_stays_forgotten_when_its_probe_finishes_late() {
    let Some(f) = fixture::get() else { return };
    let mut c = catalog();
    c.sync(&[src("s", &f.sync25, 0)], &[]);
    c.sync(&[], &[]);
    std::thread::sleep(std::time::Duration::from_millis(1500));
    c.poll();
    assert!(c.media().is_empty());
    assert!(c.info("s").is_none());
}

#[test]
fn an_audio_file_is_ready_with_a_waveform_and_no_keyframes() {
    let Some(f) = fixture::get() else { return };
    let mut c = catalog();
    c.set_want_keyframes(true);
    let tone = src("t", &f.tone, 0);
    c.sync(&[], std::slice::from_ref(&tone));
    wait_until(
        || {
            c.poll();
            c.waveform(&tone).is_some()
        },
        "the audio file's waveform",
    );
    assert_eq!(c.status("t"), SourceStatus::Ready);
    assert!(c.media().contains_key("t"));
    assert!(c.keyframes(&tone).is_none());
    c.sync(&[SourceMedia { id: "v".into(), ..tone.clone() }], std::slice::from_ref(&tone));
    wait_until(
        || {
            c.poll();
            c.status("v") == SourceStatus::Failed
        },
        "the audio file used as a video",
    );
    assert_eq!(c.error("v"), Some("No video stream"));
}
