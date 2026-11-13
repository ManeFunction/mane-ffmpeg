//! A row in the Files panel: a video on the Video tab, or an audio file on the Audio tab.

use egui::Color32;
use mane_core::media::source_catalog::{SourceCatalog, SourceStatus};
use mane_core::model::time::{display, trim_number};
use mane_core::model::{FileKind, MediaInfo, SourceMedia};

const fn rgb(v: u32) -> Color32 {
    Color32::from_rgb((v >> 16) as u8, (v >> 8) as u8, v as u8)
}

pub const PALETTE: [Color32; 8] =
    [rgb(0x4F8EF7), rgb(0x43B581), rgb(0xF5A623), rgb(0xB46EF5), rgb(0xF25F5C), rgb(0x2EC4B6), rgb(0xE84E9D), rgb(0x9CCC65)];

/// Audio files' colors: softer, and starting on a hue the first video doesn't use.
pub const AUDIO_PALETTE: [Color32; 8] =
    [rgb(0xD9C2FF), rgb(0xFFD6A0), rgb(0xA8E6C1), rgb(0xFFB3B0), rgb(0xA9C7FF), rgb(0xF9B8DA), rgb(0x9FE5DD), rgb(0xDDEBA6)];

pub fn audio_color(index: usize) -> Color32 {
    AUDIO_PALETTE[index % AUDIO_PALETTE.len()]
}

pub fn video_color(index: usize) -> Color32 {
    PALETTE[index % PALETTE.len()]
}

#[derive(Clone, Debug)]
pub struct FileItem {
    pub kind: FileKind,
    pub source: SourceMedia,
    pub color_index: usize,
    pub info: Option<MediaInfo>,
    pub status: SourceStatus,
    pub error: Option<String>,
}

impl FileItem {
    pub fn id(&self) -> &str {
        &self.source.id
    }

    pub fn name(&self) -> String {
        self.source.display_name()
    }

    pub fn color(&self) -> Color32 {
        if self.kind == FileKind::Audio { audio_color(self.color_index) } else { video_color(self.color_index) }
    }

    pub fn is_problem(&self) -> bool {
        matches!(self.status, SourceStatus::Missing | SourceStatus::Failed)
    }

    pub fn refresh(&mut self, catalog: &SourceCatalog) {
        self.info = catalog.info(&self.source.id).cloned();
        self.status = catalog.status(&self.source.id);
        self.error = catalog.error(&self.source.id).map(String::from);
    }

    pub fn status_text(&self) -> String {
        match self.status {
            SourceStatus::Analyzing => "Analyzing…".into(),
            SourceStatus::Missing => "File not found — use Relink".into(),
            SourceStatus::Failed => self.error.clone().unwrap_or_else(|| "Can't read this file".into()),
            SourceStatus::Ready => String::new(),
        }
    }

    pub fn details(&self) -> String {
        let Some(info) = &self.info else { return String::new() };
        let mut parts = vec![display(info.duration, false)];
        if self.kind == FileKind::Audio {
            parts.push(info.audio(self.source.audio_stream_index).map(|s| s.describe()).unwrap_or_else(|| "no audio".into()));
            return parts.join(" · ");
        }
        if let Some(v) = &info.video {
            parts.push(format!("{}×{}", v.display_width(), v.display_height()));
            parts.push(format!("{} fps", trim_number(v.frame_rate.value(), 2)));
            parts.push(v.codec.clone());
        }
        parts.push(info.audio_streams.first().map(|a| a.codec.clone()).unwrap_or_else(|| "no audio".into()));
        parts.join(" · ")
    }

    pub fn audio_choices(&self) -> Vec<String> {
        self.info
            .as_ref()
            .map(|i| i.audio_streams.iter().enumerate().map(|(n, a)| format!("#{} {}", n + 1, a.describe())).collect())
            .unwrap_or_default()
    }

    pub fn has_audio_choice(&self) -> bool {
        self.info.as_ref().is_some_and(|i| i.audio_streams.len() > 1)
    }
}

/// The first color no listed file uses: the one the next new file gets.
pub fn free_color_index(items: &[FileItem]) -> usize {
    (0..).find(|c| items.iter().all(|s| s.color_index != *c)).unwrap()
}

/// Brings a list in line with the project's files of one kind, keeping each file's color.
pub fn sync_list(items: &mut Vec<FileItem>, sources: &[SourceMedia], kind: FileKind) {
    items.retain(|i| sources.iter().any(|s| s.id == i.source.id));
    for source in sources {
        match items.iter_mut().find(|i| i.source.id == source.id) {
            Some(item) => item.source = source.clone(),
            None => {
                let color_index = free_color_index(items);
                items.push(FileItem {
                    kind,
                    source: source.clone(),
                    color_index,
                    info: None,
                    status: SourceStatus::Analyzing,
                    error: None,
                });
            }
        }
    }
    // Keep the project's order.
    items.sort_by_key(|i| sources.iter().position(|s| s.id == i.source.id));
}
