//! Reads stream and format information with ffprobe.

use super::tools::FfmpegTools;
use crate::model::time::{Time, parse_float};
use crate::model::{AudioStreamInfo, FileStamp, MediaInfo, Rational, VideoStreamInfo};
use serde_json::Value;
use std::process::{Command, Stdio};

#[derive(Debug, Clone, thiserror::Error)]
#[error("{0}")]
pub struct FfprobeError(pub String);

pub fn probe(tools: &FfmpegTools, path: &str) -> Result<MediaInfo, FfprobeError> {
    // Stamped before probing: a file changing meanwhile gets probed again next time.
    let stamp = FileStamp::of(path);
    let out = Command::new(&tools.ffprobe)
        .args(["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path])
        .stdin(Stdio::null())
        .output()
        .map_err(|e| FfprobeError(e.to_string()))?;
    if !out.status.success() {
        let err = String::from_utf8_lossy(&out.stderr);
        return Err(FfprobeError(
            first_line(&err).unwrap_or_else(|| format!("ffprobe exited with code {}", out.status.code().unwrap_or(-1))),
        ));
    }
    let mut info = parse(&String::from_utf8_lossy(&out.stdout))?;
    info.stamp = stamp;
    Ok(info)
}

fn first_line(text: &str) -> Option<String> {
    text.split('\n').map(str::trim).find(|l| !l.is_empty()).map(String::from)
}

fn s(e: &Value, name: &str) -> Option<String> {
    match e.get(name)? {
        Value::String(v) => Some(v.clone()),
        Value::Number(n) => Some(n.to_string()),
        _ => None,
    }
}

fn int(e: &Value, name: &str) -> i32 {
    e.get(name).and_then(Value::as_i64).map(|v| v as i32).unwrap_or(0)
}

fn seconds(text: Option<String>) -> Time {
    match text.as_deref().and_then(parse_float) {
        Some(v) if v > 0.0 => Time::from_seconds(v),
        _ => Time::ZERO,
    }
}

fn signed_seconds(text: Option<String>) -> Time {
    text.as_deref().and_then(parse_float).map(Time::from_seconds).unwrap_or(Time::ZERO)
}

fn is_attached_picture(stream: &Value) -> bool {
    stream.get("disposition").and_then(|d| d.get("attached_pic")).and_then(Value::as_i64) == Some(1)
}

fn rotation(stream: &Value) -> i32 {
    if let Some(list) = stream.get("side_data_list").and_then(Value::as_array) {
        for item in list {
            if let Some(r) = item.get("rotation").and_then(Value::as_f64) {
                return r.round_ties_even() as i32;
            }
        }
    }
    if let Some(tags) = stream.get("tags")
        && let Some(r) = s(tags, "rotate").and_then(|r| r.trim().parse::<i32>().ok())
    {
        return r;
    }
    0
}

/// Parses ffprobe's JSON output (separate for unit tests).
pub fn parse(json: &str) -> Result<MediaInfo, FfprobeError> {
    let root: Value = serde_json::from_str(json).map_err(|e| FfprobeError(e.to_string()))?;
    let format = root.get("format").ok_or_else(|| FfprobeError("Not a media file.".into()))?;
    let mut video: Option<VideoStreamInfo> = None;
    let mut audio = Vec::new();
    let mut stream_duration = Time::ZERO;
    if let Some(streams) = root.get("streams").and_then(Value::as_array) {
        for st in streams {
            let kind = s(st, "codec_type");
            let index = int(st, "index");
            let duration = seconds(s(st, "duration"));
            if duration > stream_duration {
                stream_duration = duration;
            }
            if kind.as_deref() == Some("video") && video.is_none() && !is_attached_picture(st) {
                let r = Rational::parse(s(st, "r_frame_rate").as_deref());
                let avg = Rational::parse(s(st, "avg_frame_rate").as_deref());
                let rate = if r.is_valid() && (1.0..=240.0).contains(&r.value()) {
                    r
                } else if avg.is_valid() {
                    avg
                } else {
                    Rational::new(30, 1)
                };
                let sar = Rational::parse(s(st, "sample_aspect_ratio").as_deref());
                video = Some(VideoStreamInfo {
                    index,
                    codec: s(st, "codec_name").unwrap_or_else(|| "unknown".into()),
                    width: int(st, "width"),
                    height: int(st, "height"),
                    frame_rate: rate,
                    pixel_format: s(st, "pix_fmt").unwrap_or_default(),
                    sample_aspect_ratio: if sar.is_valid() { sar } else { Rational::ONE },
                    rotation: rotation(st),
                    start_offset: signed_seconds(s(st, "start_time")),
                });
            } else if kind.as_deref() == Some("audio") {
                let tags = st.get("tags").filter(|t| t.is_object());
                audio.push(AudioStreamInfo {
                    index,
                    codec: s(st, "codec_name").unwrap_or_else(|| "unknown".into()),
                    sample_rate: s(st, "sample_rate").and_then(|v| v.trim().parse().ok()).unwrap_or(48000),
                    channels: int(st, "channels"),
                    channel_layout: s(st, "channel_layout").unwrap_or_default(),
                    language: tags.and_then(|t| s(t, "language")),
                    title: tags.and_then(|t| s(t, "title")),
                    duration: (duration > Time::ZERO).then_some(duration),
                });
            }
        }
    }
    let format_duration = seconds(s(format, "duration"));
    let start_time = signed_seconds(s(format, "start_time"));
    if let Some(v) = &mut video {
        v.start_offset = (v.start_offset - start_time).max(Time::ZERO);
    }
    Ok(MediaInfo {
        stamp: FileStamp::default(),
        duration: if format_duration > Time::ZERO { format_duration } else { stream_duration },
        start_time,
        format_name: s(format, "format_name").unwrap_or_default(),
        video,
        audio_streams: audio,
    })
}
