//! The project model: sources, clips, the timeline, crops, presets and the time arithmetic they share.

pub mod arithmetic;
pub mod audio;
pub mod crop;
pub mod datetime;
pub mod file_kind;
pub mod ids;
pub mod media_info;
pub mod output_frame;
pub mod preset;
pub mod project;
pub mod rational;
pub mod time;
pub mod timeline;

pub use audio::{AudioLayout, AudioLoop, AudioPlacement, AudioSpan, HeardAudio};
pub use crop::{CropRect, PixelCrop};
pub use datetime::UtcTime;
pub use file_kind::FileKind;
pub use media_info::{AudioStreamInfo, FileStamp, MediaInfo, VideoStreamInfo};
pub use output_frame::OutputFrame;
pub use preset::Preset;
pub use project::{ExportSettings, Project, ProjectLayout, WindowBounds};
pub use rational::Rational;
pub use time::Time;
pub use timeline::{AudioReplacement, Clip, ProjectState, SourceMedia, Timeline};
