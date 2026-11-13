use super::preset::Preset;
use super::time::Time;
use super::timeline::ProjectState;
use serde::{Deserialize, Serialize};

/// Everything stored in a .mfvp file.
#[derive(Clone, PartialEq, Debug, Default)]
pub struct Project {
    pub state: ProjectState,
    pub export: ExportSettings,
    pub layout: ProjectLayout,
}

#[derive(Clone, PartialEq, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ExportSettings {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub preset_id: Option<String>,
    /// Copy of the preset at save time, so the project still exports on a machine without it.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub preset_snapshot: Option<Preset>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub output_path: Option<String>,
}

/// Window and editor layout restored with the project.
#[derive(Clone, PartialEq, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ProjectLayout {
    #[serde(skip_serializing_if = "Option::is_none")]
    pub window: Option<WindowBounds>,
    pub left_pane_width: f64,
    /// Timeline zoom in pixels per second; 0 means "fit".
    pub timeline_zoom: f64,
    pub timeline_scroll: f64,
    pub playhead: Time,
    pub volume: f64,
    /// Compact mode: video preview hidden. `window` keeps the full-size bounds.
    pub compact: bool,
}

impl Default for ProjectLayout {
    fn default() -> Self {
        ProjectLayout {
            window: None,
            left_pane_width: 300.0,
            timeline_zoom: 0.0,
            timeline_scroll: 0.0,
            playhead: Time::ZERO,
            volume: 100.0,
            compact: false,
        }
    }
}

#[derive(Clone, Copy, PartialEq, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct WindowBounds {
    pub x: i32,
    pub y: i32,
    pub width: f64,
    pub height: f64,
    pub maximized: bool,
}
