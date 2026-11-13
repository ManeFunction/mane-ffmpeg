use super::media_info::MediaInfo;

/// Which of a project's lists a file is in: the videos clips are cut from, or the audio files that replace clips' sound.
#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug, Default)]
pub enum FileKind {
    #[default]
    Video,
    Audio,
}

impl FileKind {
    /// Why a probed file can't be used as this kind (it lacks the stream it needs), or None when it can.
    pub fn missing_stream(self, info: &MediaInfo) -> Option<&'static str> {
        match self {
            FileKind::Audio if info.audio_streams.is_empty() => Some("No audio stream"),
            FileKind::Video if info.video.is_none() => Some("No video stream"),
            _ => None,
        }
    }

    /// Only videos are cut, so only they have keyframes worth scanning.
    pub fn has_keyframes(self) -> bool {
        self == FileKind::Video
    }
}
