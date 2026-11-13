//! Reads and writes .mfvp project files: indented JSON with edits and layout only, no media. Source paths are stored
//! absolute and relative to the project, so a folder can be moved as a whole.

use super::atomic_file;
use crate::editing::project_editor;
use crate::model::{
    AudioReplacement, Clip, CropRect, ExportSettings, Project, ProjectLayout, ProjectState, SourceMedia, Time, Timeline, UtcTime,
};
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

pub const EXTENSION: &str = "mfvp";
pub const FORMAT_NAME: &str = "mane-ffmpeg-project";
/// Version 2 adds audio files and replaced clip audio, version 3 the output crop. A project is written with the
/// lowest version that holds it, so one without audio files or a crop is still 1.
pub const CURRENT_VERSION: i32 = 3;

#[derive(Debug, Clone, PartialEq, thiserror::Error)]
#[error("{0}")]
pub struct ProjectFileError(pub String);

fn null_as_empty<'de, D: serde::Deserializer<'de>>(d: D) -> Result<String, D::Error> {
    Ok(Option::<String>::deserialize(d)?.unwrap_or_default())
}

#[derive(Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase", default)]
struct ProjectDto {
    #[serde(deserialize_with = "null_as_empty")]
    format: String,
    version: i32,
    sources: Option<Vec<Option<SourceDto>>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    audio_files: Option<Vec<Option<SourceDto>>>,
    clips: Option<Vec<Option<ClipDto>>>,
    #[serde(skip_serializing_if = "Option::is_none")]
    crop: Option<CropDto>,
    #[serde(skip_serializing_if = "Option::is_none")]
    export: Option<ExportSettings>,
    #[serde(skip_serializing_if = "Option::is_none")]
    layout: Option<ProjectLayout>,
}

#[derive(Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase", default)]
struct SourceDto {
    #[serde(deserialize_with = "null_as_empty")]
    id: String,
    #[serde(deserialize_with = "null_as_empty")]
    path: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    relative_path: Option<String>,
    file_size: i64,
    last_write_utc: UtcTime,
    audio_stream: i32,
}

#[derive(Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase", default)]
struct ClipDto {
    #[serde(deserialize_with = "null_as_empty")]
    id: String,
    #[serde(deserialize_with = "null_as_empty")]
    source: String,
    r#in: Time,
    out: Time,
    audio_offset: Time,
    #[serde(skip_serializing_if = "Option::is_none")]
    replaced_audio: Option<ReplacementDto>,
}

/// The kept part of the output frame, as fractions of its width and height.
#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
struct CropDto {
    x: f64,
    y: f64,
    width: f64,
    height: f64,
}

impl Default for CropDto {
    fn default() -> Self {
        CropDto { x: 0.0, y: 0.0, width: 1.0, height: 1.0 }
    }
}

#[derive(Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase", default)]
struct ReplacementDto {
    #[serde(skip_serializing_if = "Option::is_none")]
    file: Option<String>,
    anchor: Time,
    offset: Time,
    #[serde(rename = "loop")]
    looped: bool,
}

pub fn save(project: &Project, path: &Path) -> Result<(), ProjectFileError> {
    atomic_file::write_all_text(path, &serialize(project, path)).map_err(|e| ProjectFileError(e.to_string()))
}

pub fn load(path: &Path) -> Result<Project, ProjectFileError> {
    let json = std::fs::read_to_string(path).map_err(|e| ProjectFileError(e.to_string()))?;
    deserialize(&json, path)
}

fn project_dir(project_path: &Path) -> PathBuf {
    std::path::absolute(project_path).ok().and_then(|p| p.parent().map(Path::to_path_buf)).unwrap_or_default()
}

/// The path of `target` relative to `base` (both absolute), like .NET's Path.GetRelativePath.
pub fn relative_path(base: &Path, target: &Path) -> String {
    let b: Vec<_> = base.components().collect();
    let t: Vec<_> = target.components().collect();
    let common = b.iter().zip(&t).take_while(|(x, y)| x == y).count();
    if common == 0 {
        return target.to_string_lossy().into_owned();
    }
    let mut parts: Vec<String> = std::iter::repeat_n("..".to_string(), b.len() - common).collect();
    parts.extend(t[common..].iter().map(|c| c.as_os_str().to_string_lossy().into_owned()));
    if parts.is_empty() { ".".into() } else { parts.join("/") }
}

pub fn serialize(project: &Project, project_path: &Path) -> String {
    let dir = project_dir(project_path);
    let source = |s: &SourceMedia| {
        Some(SourceDto {
            id: s.id.clone(),
            path: s.path.clone(),
            relative_path: Some(relative_path(&dir, Path::new(&s.path))),
            file_size: s.file_size,
            last_write_utc: s.last_write_utc,
            audio_stream: s.audio_stream_index,
        })
    };
    let state = &project.state;
    let has_audio_files = !state.audio_files.is_empty();
    let crop = state.crop();
    let dto = ProjectDto {
        format: FORMAT_NAME.into(),
        // An older app would silently drop replaced audio or the crop, so it is told this file is newer.
        version: if crop.is_some() {
            3
        } else if has_audio_files {
            2
        } else {
            1
        },
        sources: Some(state.sources.iter().map(source).collect()),
        audio_files: has_audio_files.then(|| state.audio_files.iter().map(source).collect()),
        clips: Some(
            state
                .timeline
                .clips()
                .iter()
                .map(|c| {
                    Some(ClipDto {
                        id: c.id.clone(),
                        source: c.source_id.clone(),
                        r#in: c.r#in,
                        out: c.out,
                        audio_offset: c.audio_offset,
                        replaced_audio: c.replaced_audio.as_ref().map(|r| ReplacementDto {
                            file: Some(r.file_id.clone()),
                            anchor: r.anchor,
                            offset: r.offset,
                            looped: r.looped,
                        }),
                    })
                })
                .collect(),
        ),
        crop: crop.map(|c| CropDto { x: c.x, y: c.y, width: c.width, height: c.height }),
        export: Some(project.export.clone()),
        layout: Some(project.layout.clone()),
    };
    // serde_json keeps quotes and non-ASCII (e.g. Cyrillic paths) readable, like the relaxed encoder did.
    let mut out = Vec::new();
    let mut ser = serde_json::Serializer::with_formatter(&mut out, serde_json::ser::PrettyFormatter::with_indent(b"  "));
    dto.serialize(&mut ser).unwrap();
    String::from_utf8(out).unwrap()
}

/// Reads a project; anything wrong with the file is a [`ProjectFileError`].
pub fn deserialize(json: &str, project_path: &Path) -> Result<Project, ProjectFileError> {
    let dto: ProjectDto = serde_json::from_str(json).map_err(|e| ProjectFileError(format!("The project file is damaged: {e}")))?;
    if dto.format != FORMAT_NAME {
        return Err(ProjectFileError("This isn't a Mane FFmpeg project file.".into()));
    }
    if dto.version > CURRENT_VERSION {
        return Err(ProjectFileError(format!(
            "This project was saved by a newer version (format {}). Please update the app.",
            dto.version
        )));
    }
    let dir = project_dir(project_path);
    let all_sources = dto.sources.iter().chain(dto.audio_files.iter()).flatten().flatten();
    if all_sources.into_iter().any(|s| s.path.contains('\0') || s.relative_path.as_deref().is_some_and(|r| r.contains('\0'))) {
        return Err(ProjectFileError("The project file is damaged: a path holds a null character.".into()));
    }
    // Hand-edited or damaged files may leave out lists or entries; skip what can't be used.
    let sources = |list: Option<Vec<Option<SourceDto>>>| -> Vec<SourceMedia> {
        list.unwrap_or_default()
            .into_iter()
            .flatten()
            .filter(|s| !s.id.is_empty() && !s.path.is_empty())
            .map(|s| SourceMedia {
                path: resolve_path(&s, &dir),
                id: s.id,
                file_size: s.file_size,
                last_write_utc: s.last_write_utc,
                audio_stream_index: s.audio_stream.max(0),
            })
            .collect()
    };
    let srcs = sources(dto.sources);
    let audio = sources(dto.audio_files);
    let clips = dto
        .clips
        .unwrap_or_default()
        .into_iter()
        .flatten()
        .filter(|c| !c.id.is_empty() && !c.source.is_empty() && c.out > c.r#in)
        .map(|c| Clip {
            id: c.id,
            source_id: c.source,
            r#in: c.r#in,
            out: c.out,
            audio_offset: c.audio_offset,
            replaced_audio: c
                .replaced_audio
                .and_then(|r| r.file.map(|file| AudioReplacement { file_id: file, anchor: r.anchor, offset: r.offset, looped: r.looped })),
        })
        .collect();
    // Clips of a video that isn't listed are dropped; a replacement whose audio file is gone leaves the clip with
    // its own audio.
    let state = project_editor::drop_orphans(&ProjectState::new(srcs, Timeline::new(clips), audio))
        .with_crop(dto.crop.map(|c| CropRect::new(c.x, c.y, c.width, c.height)));
    Ok(Project { state, export: dto.export.unwrap_or_default(), layout: dto.layout.unwrap_or_default() })
}

/// The stored absolute path if it still exists, else the project-relative one if that exists.
fn resolve_path(source: &SourceDto, project_dir: &Path) -> String {
    if Path::new(&source.path).is_file() {
        return source.path.clone();
    }
    if let Some(rel) = source.relative_path.as_deref().filter(|r| !r.is_empty()) {
        let joined = project_dir.join(rel);
        let normalized = normalize(&joined);
        if normalized.is_file() {
            return normalized.to_string_lossy().into_owned();
        }
    }
    source.path.clone()
}

/// Resolves "." and ".." without touching the file system (like Path.GetFullPath).
pub fn normalize(path: &Path) -> PathBuf {
    let mut out = PathBuf::new();
    for c in path.components() {
        match c {
            std::path::Component::ParentDir => {
                out.pop();
            }
            std::path::Component::CurDir => {}
            other => out.push(other),
        }
    }
    out
}
