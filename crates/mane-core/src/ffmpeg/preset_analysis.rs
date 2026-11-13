//! Whether a preset can be used and what it does, worked out once for the export planner and the preset editor.
//! Never fails: problems that stop an export are `errors`, the rest `warnings`.

use super::preset_arguments::{PresetArguments, tokenize};
use crate::model::Preset;

#[derive(Clone, Debug, Default)]
pub struct PresetAnalysis {
    /// The parsed options; None when they can't be read (see `errors`).
    pub arguments: Option<PresetArguments>,
    /// Audio options used instead of "-c:a copy" when audio has to go through filters.
    pub fallback: Vec<String>,
    pub errors: Vec<String>,
    pub warnings: Vec<String>,
}

impl PresetAnalysis {
    pub fn is_valid(&self) -> bool {
        self.errors.is_empty()
    }

    pub fn video_copy(&self) -> bool {
        self.arguments.as_ref().is_some_and(|a| a.video_copy())
    }

    pub fn of(preset: &Preset) -> PresetAnalysis {
        Self::of_text(&preset.arguments, &preset.audio_fallback_arguments)
    }

    pub fn of_text(arguments: &str, audio_fallback: &str) -> PresetAnalysis {
        let mut errors = Vec::new();
        let args = match PresetArguments::parse(arguments) {
            Ok(a) => Some(a),
            Err(e) => {
                errors.push(e);
                None
            }
        };
        let fallback = match tokenize(audio_fallback) {
            Ok(f) => f,
            Err(e) => {
                errors.push(format!("Audio fallback: {e}"));
                Vec::new()
            }
        };
        if let Some(a) = &args {
            if a.no_video {
                errors.push("The preset disables video (-vn).".into());
            }
            if a.video_copy() && a.video_filter.is_some() {
                errors.push("Video filters can't be used while copying video (-c:v copy).".into());
            }
            if a.audio_copy() && a.audio_filter.is_some() {
                errors.push("Audio filters can't be used while copying audio (-c:a copy).".into());
            }
        }
        let warnings = args.as_ref().map(|a| a.warnings.clone()).unwrap_or_default();
        PresetAnalysis { arguments: args, fallback, errors, warnings }
    }

    /// How the export treats video, in the planner's terms.
    pub fn video_summary(&self) -> Option<String> {
        let a = self.arguments.as_ref()?;
        Some(if a.video_copy() {
            "Video: copied without re-encoding. Cuts snap to keyframes; all clips must share one format.".into()
        } else {
            format!("Video: encoded with {}.", a.video_codec.as_deref().unwrap_or("ffmpeg's default for the container"))
        })
    }

    /// How the export treats audio, in the planner's terms.
    pub fn audio_summary(&self, fallback_text: &str) -> Option<String> {
        let a = self.arguments.as_ref()?;
        Some(if a.no_audio {
            "Audio: dropped (-an).".into()
        } else if !a.audio_copy() {
            format!("Audio: encoded with {}.", a.audio_codec.as_deref().unwrap_or("ffmpeg's default for the container"))
        } else if a.video_copy() {
            format!(
                "Audio: copied when all clips' audio shares one format and no clip has an audio offset; otherwise encoded with “{fallback_text}”."
            )
        } else {
            format!("Audio: copied for a single clip, even with an audio offset. Several clips are encoded with “{fallback_text}”.")
        })
    }
}
