use serde::{Deserialize, Serialize};

pub const DEFAULT_AUDIO_FALLBACK: &str = "-c:a aac -b:a 256k";
/// "for storage" arguments before version 2 of the presets file.
pub const FOR_STORAGE_V1_ARGUMENTS: &str = "-c:v libx265 -crf 28 -preset slow -c:a copy";

fn default_fallback() -> String {
    DEFAULT_AUDIO_FALLBACK.to_string()
}

/// Export settings: raw ffmpeg output options plus a few things the editor needs to know.
#[derive(Clone, PartialEq, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Preset {
    #[serde(default)]
    pub id: String,
    #[serde(default)]
    pub name: String,
    /// ffmpeg output options, e.g. "-c:v libx265 -crf 28 -preset slow -c:a copy".
    #[serde(default)]
    pub arguments: String,
    /// Default output extension without the dot; empty means "same as the first source".
    #[serde(default, deserialize_with = "null_as_empty")]
    pub extension: String,
    /// Audio options used instead of "-c:a copy" when the edit forces audio through filters.
    #[serde(default = "default_fallback", deserialize_with = "null_as_empty")]
    pub audio_fallback_arguments: String,
}

fn null_as_empty<'de, D: serde::Deserializer<'de>>(d: D) -> Result<String, D::Error> {
    Ok(Option::<String>::deserialize(d)?.unwrap_or_default())
}

impl Preset {
    fn builtin(id: &str, name: &str, arguments: &str, extension: &str) -> Preset {
        Preset {
            id: id.into(),
            name: name.into(),
            arguments: arguments.into(),
            extension: extension.into(),
            audio_fallback_arguments: DEFAULT_AUDIO_FALLBACK.into(),
        }
    }

    /// Exports exactly as `other` does: the same options, extension and audio fallback.
    pub fn runs_like(&self, other: &Preset) -> bool {
        self.arguments.trim() == other.arguments.trim()
            && self.extension == other.extension
            && self.audio_fallback_arguments.trim() == other.audio_fallback_arguments.trim()
    }

    /// The preset with defaults for fields a hand-written or damaged file left empty.
    pub fn with_defaults(&self) -> Preset {
        let mut p = self.clone();
        if p.audio_fallback_arguments.trim().is_empty() {
            p.audio_fallback_arguments = DEFAULT_AUDIO_FALLBACK.into();
        }
        p
    }

    /// Archival HEVC: CRF 23 in 10-bit with x265 tuned to keep detail (aq-mode 3, psy-rdoq, no SAO), so moving
    /// objects don't smear.
    pub fn for_storage() -> Preset {
        Self::builtin(
            "builtin-for-storage",
            "for storage (balanced)",
            "-c:v libx265 -crf 23 -preset slow -pix_fmt yuv420p10le -x265-params aq-mode=3:psy-rdoq=1.0:no-sao=1 -c:a copy",
            "mp4",
        )
    }

    /// The original, smaller but softer "for storage" settings.
    pub fn harsh() -> Preset {
        Self::builtin("builtin-harsh", "harsh compression", FOR_STORAGE_V1_ARGUMENTS, "mp4")
    }

    /// Everyday video for messengers and social media: H.264 High in 8-bit 4:2:0 and AAC play everywhere, and the
    /// MP4 index goes first so it starts playing before it has fully downloaded.
    pub fn share() -> Preset {
        Self::builtin(
            "builtin-share",
            "for share (compatibility)",
            "-c:v libx264 -crf 20 -preset medium -profile:v high -pix_fmt yuv420p -c:a aac -b:a 192k -movflags +faststart",
            "mp4",
        )
    }

    pub fn plane_copy() -> Preset {
        Self::builtin("builtin-plane-copy", "copy (w/o encode)", "-c:v copy -c:a copy", "")
    }

    /// Stream copy into an MP4, for sources in another container.
    pub fn copy_to_mp4() -> Preset {
        Self::builtin("builtin-copy-mp4", "copy to mp4 (w/o encode)", "-c:v copy -c:a copy", "mp4")
    }

    pub fn built_ins() -> Vec<Preset> {
        vec![Self::for_storage(), Self::share(), Self::harsh(), Self::plane_copy(), Self::copy_to_mp4()]
    }
}
