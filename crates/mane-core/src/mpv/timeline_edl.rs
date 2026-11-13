//! Describes the edited timeline to mpv as an EDL with two parallel streams: the video cuts, and the audio cuts
//! slipped by each clip's offset, with silence wherever the slipped audio runs past its file. Tracks are titled so
//! the player can pick video from the first stream and audio from the second.

use crate::media::silence_asset;
use crate::model::time::{Time, seconds};
use crate::model::{MediaInfo, ProjectState};
use std::collections::HashMap;

pub const VIDEO_TITLE: &str = "mane-video";
pub const AUDIO_TITLE: &str = "mane-audio";

const EPSILON: Time = Time::from_ms(1);

/// Returns an "edl://" URL, or None when nothing on the timeline can be played.
pub fn build(state: &ProjectState, media: &HashMap<String, MediaInfo>, silence_path: &str) -> Option<String> {
    let mut video = String::new();
    let mut audio = String::new();
    let mut any_audio = false;
    let mut count = 0;
    for clip in state.timeline.clips() {
        let source = state.source(&clip.source_id)?;
        let info = media.get(&source.id)?;
        info.video.as_ref()?;
        count += 1;
        // EDL segment starts are file timestamps, which begin at the container start time, not at 0.
        segment(&mut video, &source.path, clip.r#in + info.start_time, clip.duration());
        // The heard file: an audio file dropped on the clip, or the clip's own video. One that isn't analyzed yet
        // plays silence until the catalog reports it and the preview reloads.
        let Some(heard) = state.heard_audio(clip, |id| media.get(id)) else {
            silence(&mut audio, silence_path, clip.duration());
            continue;
        };
        any_audio = true;
        for span in heard.layout(clip).spans() {
            match span.read_from {
                Some(read_from) => segment(&mut audio, &heard.file.path, read_from + heard.info.start_time, span.length),
                None => silence(&mut audio, silence_path, span.length),
            }
        }
    }
    if count == 0 {
        return None;
    }
    let mut edl = format!("edl://!new_stream;!track_meta,title={VIDEO_TITLE};{video}");
    if any_audio {
        edl.push_str(&format!("!new_stream;!track_meta,title={AUDIO_TITLE};{audio}"));
    }
    Some(edl.trim_end_matches(';').to_string())
}

fn segment(sb: &mut String, path: &str, start: Time, length: Time) {
    sb.push_str(&format!("%{}%{path},{},{};", path.len(), seconds(start), seconds(length)));
}

/// Pieces of the silence file, kept a minute short of its end.
fn silence_length() -> Time {
    silence_asset::LENGTH - Time::from_secs(60)
}

fn silence(sb: &mut String, silence_path: &str, mut length: Time) {
    while length > EPSILON {
        let piece = length.min(silence_length());
        segment(sb, silence_path, Time::ZERO, piece);
        length -= piece;
    }
}
