//! App-wide settings in settings.json.

use super::{app_log, app_paths, atomic_file};
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
pub enum ThemeChoice {
    System,
    Light,
    #[default]
    Dark,
}

impl ThemeChoice {
    pub const ALL: [ThemeChoice; 3] = [ThemeChoice::System, ThemeChoice::Light, ThemeChoice::Dark];

    pub fn name(self) -> &'static str {
        match self {
            ThemeChoice::System => "System",
            ThemeChoice::Light => "Light",
            ThemeChoice::Dark => "Dark",
        }
    }
}

/// Missing keys in an older settings.json come back as these defaults.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct AppSettings {
    pub ffmpeg_path: Option<String>,
    pub ffprobe_path: Option<String>,
    pub lib_mpv_path: Option<String>,
    pub hardware_decoding: bool,
    pub notifications: bool,
    pub theme: ThemeChoice,
    /// Offer to install missing ffmpeg/libmpv with Homebrew when the app starts (macOS).
    pub ask_to_install_at_startup: bool,
    /// Offer to make this app open .mfvp projects on double-click when the app starts.
    pub ask_to_register_project_files: bool,
    /// Look for a newer release on GitHub when the app starts.
    pub check_for_updates: bool,
    /// A release the user asked not to be reminded about ("1.2.0"); a newer one is offered again.
    pub skipped_update_version: Option<String>,
    pub recent_projects: Vec<String>,
    pub last_media_folder: Option<String>,
    pub last_project_folder: Option<String>,
}

impl Default for AppSettings {
    fn default() -> Self {
        AppSettings {
            ffmpeg_path: None,
            ffprobe_path: None,
            lib_mpv_path: None,
            hardware_decoding: true,
            notifications: true,
            theme: ThemeChoice::Dark,
            ask_to_install_at_startup: true,
            ask_to_register_project_files: true,
            check_for_updates: true,
            skipped_update_version: None,
            recent_projects: Vec::new(),
            last_media_folder: None,
            last_project_folder: None,
        }
    }
}

const MAX_RECENT: usize = 10;

pub struct SettingsStore {
    path: PathBuf,
    current: AppSettings,
    /// Bumped on every change, so views can tell they're stale.
    pub generation: u64,
}

impl SettingsStore {
    pub fn new(path: Option<PathBuf>) -> SettingsStore {
        let path = path.unwrap_or_else(|| app_paths::config_directory().join("settings.json"));
        let current = load(&path).unwrap_or_default();
        SettingsStore { path, current, generation: 0 }
    }

    pub fn current(&self) -> &AppSettings {
        &self.current
    }

    pub fn update(&mut self, change: impl FnOnce(&mut AppSettings)) {
        change(&mut self.current);
        let json = serde_json::to_string_pretty(&self.current).unwrap_or_default();
        if let Err(e) = atomic_file::write_all_text(&self.path, &json) {
            // The settings still apply for this session.
            app_log::write(&format!("Couldn't save the settings: {e}"));
        }
        self.generation += 1;
    }

    pub fn add_recent_project(&mut self, path: &str) {
        self.update(|s| {
            let mut recent = vec![path.to_string()];
            recent.extend(s.recent_projects.iter().filter(|p| *p != path).take(MAX_RECENT - 1).cloned());
            s.recent_projects = recent;
            s.last_project_folder = Path::new(path).parent().map(|p| p.to_string_lossy().into_owned());
        });
    }
}

fn load(path: &Path) -> Option<AppSettings> {
    let text = std::fs::read_to_string(path).ok()?;
    serde_json::from_str(&text).ok()
}
