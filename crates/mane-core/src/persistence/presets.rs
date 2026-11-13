//! The user's export presets, shared by all windows and saved to presets.json.

use super::{app_log, app_paths, atomic_file};
use crate::model::Preset;
use crate::model::preset::FOR_STORAGE_V1_ARGUMENTS;
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

const CURRENT_VERSION: i32 = 4;

#[derive(Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase", default)]
struct PresetFile {
    version: i32,
    presets: Vec<Preset>,
}

pub struct PresetStore {
    path: PathBuf,
    presets: Vec<Preset>,
    /// Why the last save failed (full disk, read-only folder…), or None. Changes stay in memory either way.
    pub save_error: Option<String>,
    /// Bumped on every change.
    pub generation: u64,
}

impl PresetStore {
    pub fn new(path: Option<PathBuf>) -> PresetStore {
        let path = path.unwrap_or_else(|| app_paths::config_directory().join("presets.json"));
        let (loaded, version) = load(&path);
        let mut store = PresetStore { path, presets: loaded.clone().unwrap_or_else(Preset::built_ins), save_error: None, generation: 0 };
        if loaded.is_some() && version < CURRENT_VERSION {
            migrate(&mut store.presets, version);
            store.persist();
        }
        store
    }

    pub fn presets(&self) -> &[Preset] {
        &self.presets
    }

    pub fn find(&self, id: Option<&str>) -> Option<&Preset> {
        let id = id?;
        self.presets.iter().find(|p| p.id == id)
    }

    /// The preset a project names: the one with its id, or else one that runs exactly like the project's copy of it
    /// (a preset that got another id). Picking it trusts no new options. None when there is none.
    pub fn find_for(&self, id: Option<&str>, snapshot: Option<&Preset>) -> Option<&Preset> {
        self.find(id).or_else(|| {
            let s = snapshot?.with_defaults();
            self.presets.iter().find(|p| p.runs_like(&s))
        })
    }

    pub fn save(&mut self, preset: Preset) {
        match self.presets.iter().position(|p| p.id == preset.id) {
            Some(i) => self.presets[i] = preset,
            None => self.presets.push(preset),
        }
        self.persist();
    }

    /// Moves a preset to another place in the list (and in the export bar's menu).
    pub fn move_to(&mut self, id: &str, index: usize) {
        let Some(from) = self.presets.iter().position(|p| p.id == id) else { return };
        let index = index.min(self.presets.len().saturating_sub(1));
        if from == index {
            return;
        }
        let p = self.presets.remove(from);
        self.presets.insert(index, p);
        self.persist();
    }

    pub fn delete(&mut self, id: &str) {
        let before = self.presets.len();
        self.presets.retain(|p| p.id != id);
        if self.presets.len() != before {
            self.persist();
        }
    }

    /// Puts the built-in presets back (replacing edited copies with the same id).
    pub fn restore_built_ins(&mut self) {
        for b in Preset::built_ins().into_iter().rev() {
            match self.presets.iter().position(|p| p.id == b.id) {
                Some(i) => self.presets[i] = b,
                None => self.presets.insert(0, b),
            }
        }
        self.persist();
    }

    fn persist(&mut self) {
        let file = PresetFile { version: CURRENT_VERSION, presets: self.presets.clone() };
        let json = serde_json::to_string_pretty(&file).unwrap();
        match atomic_file::write_all_text(&self.path, &json) {
            Ok(()) => self.save_error = None,
            Err(e) => {
                let msg = format!("Couldn't save the presets: {e}");
                app_log::write(&msg);
                self.save_error = Some(msg);
            }
        }
        self.generation += 1;
    }
}

fn load(path: &Path) -> (Option<Vec<Preset>>, i32) {
    let Ok(text) = std::fs::read_to_string(path) else { return (None, CURRENT_VERSION) };
    match serde_json::from_str::<PresetFile>(&text) {
        Ok(f) => (Some(f.presets.into_iter().filter(|p| !p.id.trim().is_empty()).map(|p| p.with_defaults()).collect()), f.version),
        Err(_) => (None, CURRENT_VERSION),
    }
}

/// Version 2 made "for storage" keep more detail and kept the old settings as "harsh": an unedited "for storage" gets
/// the new arguments, and "harsh" is added next to it. Version 3 added "share". Version 4 renamed the built-ins (a
/// name the user edited is kept), moved "share" to second place and added "copy to mp4", taking over the user's own
/// "plane copy but mp4" if they made one.
fn migrate(presets: &mut Vec<Preset>, version: i32) {
    let pos = |p: &[Preset], id: &str| p.iter().position(|x| x.id == id);
    if version < 2 {
        let storage = pos(presets, &Preset::for_storage().id);
        if let Some(i) = storage
            && presets[i].arguments.trim() == FOR_STORAGE_V1_ARGUMENTS
        {
            presets[i].arguments = Preset::for_storage().arguments;
        }
        if pos(presets, &Preset::harsh().id).is_none() {
            presets.insert(storage.map(|i| i + 1).unwrap_or(0), Preset::harsh());
        }
    }
    if version < 3 && pos(presets, &Preset::share().id).is_none() {
        let harsh = pos(presets, &Preset::harsh().id);
        let at = harsh.map(|i| i + 1).unwrap_or(presets.len());
        presets.insert(at, Preset::share());
    }
    if version < 4 {
        migrate_to_4(presets);
    }
}

/// What users called "copy to mp4" before it was built in; such a preset becomes the built-in.
const OWN_COPY_TO_MP4_NAME: &str = "plane copy but mp4";

fn migrate_to_4(presets: &mut Vec<Preset>) {
    // Built-ins still called by their version 3 name get the new one; a name the user chose is kept.
    let v3_names = [
        (Preset::for_storage().id, "for storage"),
        (Preset::harsh().id, "harsh"),
        (Preset::share().id, "share"),
        (Preset::plane_copy().id, "plane copy"),
    ];
    let built_ins = Preset::built_ins();
    for p in presets.iter_mut() {
        if let Some((_, old)) = v3_names.iter().find(|(id, _)| *id == p.id)
            && p.name == *old
        {
            p.name = built_ins.iter().find(|b| b.id == p.id).unwrap().name.clone();
        }
    }
    // Moves "share" up to index 1; one already there or above stays.
    if let Some(from) = presets.iter().position(|p| p.id == Preset::share().id)
        && from > 1
    {
        let p = presets.remove(from);
        presets.insert(1, p);
    }
    // The user's own "copy to mp4" becomes the built-in (keeping its options); without one, the built-in is added
    // after "copy".
    let c = Preset::copy_to_mp4();
    let own = presets.iter().position(|p| p.id != c.id && (p.name == OWN_COPY_TO_MP4_NAME || p.name == c.name));
    if let Some(i) = own {
        presets[i].id = c.id.clone();
        presets[i].name = c.name.clone();
    } else if presets.iter().all(|p| p.id != c.id) {
        let copy = presets.iter().position(|p| p.id == Preset::plane_copy().id);
        let at = copy.map(|i| i + 1).unwrap_or(presets.len());
        presets.insert(at, c);
    }
}
