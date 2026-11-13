//! Synthetic media for integration tests: black video with a white flash and a 1 kHz beep every 2 seconds, starting
//! at 0, so A/V sync of any export can be measured with blackdetect/silencedetect. Tests return early (pass) when no
//! ffmpeg is installed, like the skipped facts did.
#![allow(dead_code)]

use mane_core::ffmpeg::preset_arguments::tokenize;
use mane_core::ffmpeg::{FfmpegTools, ffprobe};
use mane_core::model::MediaInfo;
use regex::Regex;
use std::path::PathBuf;
use std::process::{Command, Stdio};
use std::sync::OnceLock;
use std::time::{Duration, Instant};

pub struct Fixture {
    pub tools: FfmpegTools,
    pub directory: PathBuf,
    pub sync30: String,
    pub sync25: String,
    pub no_audio: String,
    pub transport_stream: String,
    /// 3 s of audio only (AAC, 44.1 kHz) with one 880 Hz beep at 0.3–0.4 s.
    pub tone: String,
}

pub fn tools() -> Option<FfmpegTools> {
    static TOOLS: OnceLock<Option<FfmpegTools>> = OnceLock::new();
    TOOLS.get_or_init(|| FfmpegTools::locate(None, None)).clone()
}

/// The shared fixture, or None (skip) without ffmpeg.
pub fn get() -> Option<&'static Fixture> {
    static FIXTURE: OnceLock<Option<Fixture>> = OnceLock::new();
    FIXTURE
        .get_or_init(|| {
            let tools = tools()?;
            let dir = tempfile::Builder::new().prefix("mane-ffmpeg-tests").tempdir().unwrap().keep();
            let gen_ = |name: &str, secs: u32, size: &str, rate: &str, codec: &str, audio: bool| {
                let path = dir.join(name);
                let mut args: Vec<String> =
                    ["-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-t"].map(String::from).to_vec();
                args.push(secs.to_string());
                args.push("-i".into());
                args.push(format!("color=c=black:s={size}:r={rate},drawbox=c=white:t=fill:enable='lt(mod(t\\,2)\\,0.1)'"));
                if audio {
                    args.extend(["-f", "lavfi", "-t"].map(String::from));
                    args.push(secs.to_string());
                    args.push("-i".into());
                    args.push("aevalsrc='if(lt(mod(t\\,2)\\,0.1)\\,0.5*sin(2*PI*1000*t)\\,0)':s=48000:c=stereo".into());
                }
                args.extend(tokenize(codec).unwrap());
                args.push(path.to_string_lossy().into_owned());
                let ok = Command::new(&tools.ffmpeg).args(&args).stdin(Stdio::null()).status().unwrap().success();
                assert!(ok, "generating {name}");
                path.to_string_lossy().into_owned()
            };
            let sync30 = gen_(
                "sync30.mp4",
                12,
                "640x360",
                "30",
                "-c:v libx264 -preset veryfast -g 30 -keyint_min 30 -sc_threshold 0 -pix_fmt yuv420p -c:a aac -b:a 128k",
                true,
            );
            let sync25 =
                gen_("sync25.mp4", 8, "1280x720", "25", "-c:v libx264 -preset veryfast -g 25 -pix_fmt yuv420p -c:a aac -b:a 128k", true);
            let no_audio = gen_("noaudio.mp4", 6, "640x360", "30", "-c:v libx264 -preset veryfast -g 30 -pix_fmt yuv420p", false);
            let ts = gen_(
                "sync.ts",
                8,
                "640x360",
                "30",
                "-c:v libx264 -preset veryfast -g 30 -pix_fmt yuv420p -c:a aac -b:a 128k -f mpegts",
                true,
            );
            let tone = dir.join("tone.m4a").to_string_lossy().into_owned();
            let ok = Command::new(&tools.ffmpeg)
                .args(["-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-t", "3", "-i"])
                .arg("aevalsrc='if(between(t\\,0.3\\,0.4)\\,0.5*sin(2*PI*880*t)\\,0)':s=44100:c=stereo")
                .args(["-c:a", "aac", "-b:a", "128k", &tone])
                .stdin(Stdio::null())
                .status()
                .unwrap()
                .success();
            assert!(ok);
            Some(Fixture { tools, directory: dir, sync30, sync25, no_audio, transport_stream: ts, tone })
        })
        .as_ref()
}

impl Fixture {
    pub fn probe(&self, path: &str) -> MediaInfo {
        ffprobe::probe(&self.tools, path).unwrap()
    }

    /// Times where a flash starts and where a beep starts in `path`, and its duration.
    pub fn measure(&self, path: &str) -> (Vec<f64>, Vec<f64>, f64) {
        let out = Command::new(&self.tools.ffmpeg)
            .args(["-nostdin", "-hide_banner", "-i", path, "-vf", "blackdetect=d=0.03:pix_th=0.1", "-af"])
            .arg("aresample=async=1:first_pts=0,silencedetect=n=-30dB:d=0.03")
            .args(["-f", "null", "-"])
            .stdin(Stdio::null())
            .output()
            .unwrap();
        let log = String::from_utf8_lossy(&out.stderr);
        let flashes = pulses(&log, r"black_start:([0-9.]+)", r"black_end:([0-9.]+)");
        let beeps = pulses(&log, r"silence_start: ([0-9.]+)", r"silence_end: ([0-9.]+)");
        (flashes, beeps, self.probe(path).duration.total_seconds())
    }
}

/// A flash is a black region ending and another starting ~0.1 s later; likewise a beep with silence.
fn pulses(log: &str, starts: &str, ends: &str) -> Vec<f64> {
    let s = Regex::new(starts).unwrap();
    let e = Regex::new(ends).unwrap();
    let start_times: Vec<f64> = s.captures_iter(log).map(|c| c[1].parse().unwrap()).collect();
    e.captures_iter(log).map(|c| c[1].parse::<f64>().unwrap()).filter(|e| start_times.iter().any(|st| *st > *e && st - e < 0.2)).collect()
}

/// Polls until `done` holds (up to 30 s).
pub fn wait_until(mut done: impl FnMut() -> bool, what: &str) {
    let until = Instant::now() + Duration::from_secs(30);
    while !done() {
        assert!(Instant::now() < until, "timed out waiting for {what}");
        std::thread::sleep(Duration::from_millis(20));
    }
}
