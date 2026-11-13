//! Mane FFmpeg's core: the project model, editing, ffmpeg/ffprobe, libmpv, persistence and platform services.
//! Nothing here depends on the UI.

pub mod editing;
pub mod executables;
pub mod ffmpeg;
pub mod media;
pub mod model;
pub mod mpv;
pub mod persistence;
pub mod platform;
pub mod updates;
pub mod version;
