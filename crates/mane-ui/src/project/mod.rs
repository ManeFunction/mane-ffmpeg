//! One open project: its edit history, sources and clip inspector, with the timeline, preview and export as parts.
//! The port of the C# MainWindowViewModel; derived values are worked out when drawn instead of raised as events.

pub mod export;
pub mod files;
pub mod preview;
pub mod tasks;
pub mod timeline;

use crate::logic::audio_drag::AudioDragGesture;
use crate::shared::{AppCommand, AppShared};
use export::ExportState;
use files::FileItem;
use mane_core::editing::project_editor::{self as pe, AddRouting, AddedFiles, ProbedFile};
use mane_core::editing::timeline_editor as te;
use mane_core::editing::{CutGrid, UndoStack};
use mane_core::media::analyzers::MediaAnalyzers;
use mane_core::media::source_catalog::SourceCatalog;
use mane_core::model::time::{self, Time};
use mane_core::model::*;
use mane_core::persistence::{app_log, project_file};
use preview::PreviewState;
use std::collections::HashSet;
use std::path::Path;
use std::sync::Arc;
use std::sync::atomic::Ordering;
use tasks::{Ask, Pick, Tasks};
use timeline::TimelineState;

pub const VIDEO_EXTENSIONS: &[&str] =
    &["mp4", "mov", "m4v", "mkv", "webm", "avi", "mts", "m2ts", "ts", "mpg", "mpeg", "wmv", "flv", "3gp", "mxf", "ogv"];
pub const AUDIO_EXTENSIONS: &[&str] =
    &["mp3", "m4a", "aac", "wav", "aif", "aiff", "flac", "ogg", "oga", "opus", "wma", "mka", "ac3", "eac3", "dts", "caf"];

/// Text typed into an inspector field: kept while it has focus, applied on Enter or when focus leaves.
#[derive(Default)]
pub struct FieldEdit {
    pub text: String,
    pub editing: bool,
}

/// Dialog windows a project window can have open.
#[derive(Default)]
pub struct OpenDialogs {
    pub presets: Option<crate::dialogs::presets::PresetsDialog>,
    pub settings: Option<crate::dialogs::settings::SettingsDialog>,
    pub setup: Option<crate::dialogs::setup::SetupDialog>,
    pub update: Option<crate::dialogs::update::UpdateDialog>,
}

pub struct ProjectWindow {
    pub id: u64,
    pub app: Arc<AppShared>,
    pub waker: crate::waker::Waker,
    pub history: UndoStack<ProjectState>,
    seen_generation: u64,
    seen_tools: u64,
    seen_presets: u64,
    pub catalog: SourceCatalog,
    saved_state: Arc<ProjectState>,
    saved_export: ExportSettings,
    pub files_tab: FileKind,
    pub sources: Vec<FileItem>,
    pub audio_files: Vec<FileItem>,
    pub file_path: Option<String>,
    pub status_message: Option<String>,
    pub snap_to_keyframes: bool,
    /// Layout read from the project file, applied by the window after loading.
    pub layout: ProjectLayout,
    pub layout_pending: bool,
    pub timeline: TimelineState,
    pub preview: PreviewState,
    pub export: ExportState,
    pub audio_drag: AudioDragGesture,
    pub tasks: Tasks,
    pub dialogs: Vec<Ask>,
    pub open: OpenDialogs,
    pub clip_in: FieldEdit,
    pub clip_out: FieldEdit,
    pub offset: FieldEdit,
    /// Closing was confirmed (or forced): the next frame closes the window.
    pub close_confirmed: bool,
    pub closing: bool,
    /// The answer to a close the app asked about (updates, Quit).
    pub close_answer: Option<bool>,
    pub left_pane_width: f32,
    /// The window's outer rectangle, in points, as last seen.
    pub outer_rect: Option<egui::Rect>,
    pub maximized: bool,
    pub compact_applied: Option<bool>,
    pub expanded_height: Option<f32>,
    /// Keys handled as shortcuts while held (auto-repeat of Space doesn't toggle again).
    pub keys_down: HashSet<egui::Key>,
    /// Files dropped from outside, waiting for a pointer position to know where they landed.
    pub pending_drop: Option<(Vec<String>, std::time::Instant)>,
    /// Shift during a timeline drag, as last seen (pressing or releasing it snaps at once).
    pub last_shift: Option<bool>,
    /// The timeline's rectangle on screen, for drops and the audio drag.
    pub timeline_rect: Option<egui::Rect>,
    /// The Files panel's rectangle on screen, for drops on the Audio tab.
    pub files_rect: Option<egui::Rect>,
    pub debug: Option<crate::debug_automation::Automation>,
    /// A screenshot debug automation asked of a dialog window.
    pub dialog_shot: Option<(String, egui::ScreenshotCallback)>,
    /// Debug automation: a dialog drawn in the window instead of the project.
    pub inline_dialog: Option<String>,
}

impl ProjectWindow {
    pub fn new(id: u64, app: Arc<AppShared>, ctx: egui::Context, viewport: egui::ViewportId) -> ProjectWindow {
        let waker = crate::waker::Waker::new(ctx.clone(), viewport);
        let catalog = SourceCatalog::new(app.analyzers.clone() as Arc<dyn MediaAnalyzers>, waker.as_fn());
        let export = ExportState::new(&app);
        let mut w = ProjectWindow {
            id,
            app: app.clone(),
            waker,
            history: UndoStack::new(ProjectState::empty()),
            seen_generation: 0,
            seen_tools: app.tools_generation.load(Ordering::Acquire),
            seen_presets: app.presets().generation,
            catalog,
            saved_state: Arc::new(ProjectState::empty()),
            saved_export: ExportSettings::default(),
            files_tab: FileKind::Video,
            sources: Vec::new(),
            audio_files: Vec::new(),
            file_path: None,
            status_message: None,
            snap_to_keyframes: false,
            layout: ProjectLayout::default(),
            layout_pending: false,
            timeline: TimelineState::default(),
            preview: PreviewState::default(),
            export,
            audio_drag: AudioDragGesture::default(),
            tasks: Tasks::default(),
            dialogs: Vec::new(),
            open: OpenDialogs::default(),
            clip_in: FieldEdit::default(),
            clip_out: FieldEdit::default(),
            offset: FieldEdit::default(),
            close_confirmed: false,
            closing: false,
            close_answer: None,
            left_pane_width: 300.0,
            outer_rect: None,
            maximized: false,
            compact_applied: None,
            expanded_height: None,
            keys_down: HashSet::new(),
            pending_drop: None,
            last_shift: None,
            timeline_rect: None,
            files_rect: None,
            debug: None,
            dialog_shot: None,
            inline_dialog: None,
        };
        w.saved_export = w.export.current_settings();
        w.on_preset_selected();
        w.start_player();
        w
    }

    pub fn state(&self) -> &ProjectState {
        self.history.current()
    }

    pub fn wake(&self) {
        self.waker.wake();
    }

    // ---- Per-frame bookkeeping (the C# events) ------------------------------------------------------

    /// Applies what changed since the last frame: finished jobs, the history, analyses, the player, the export.
    pub fn logic(&mut self) {
        for done in self.tasks.take_done() {
            done(self);
        }
        let tools = self.app.tools_generation.load(Ordering::Acquire);
        if tools != self.seen_tools {
            self.seen_tools = tools;
            // libmpv may have just been installed.
            self.catalog.reanalyze();
            self.start_player();
            self.export.invalidate_plan();
        }
        let presets = self.app.presets().generation;
        if presets != self.seen_presets {
            self.seen_presets = presets;
            self.export.reload_presets(&self.app);
            self.on_preset_selected();
        }
        if self.history.generation != self.seen_generation {
            self.seen_generation = self.history.generation;
            self.on_history_changed();
        }
        let changed = self.catalog.poll();
        if !changed.is_empty() {
            for id in &changed {
                self.on_source_analyzed(id);
            }
            self.preview_on_project_changed();
            self.export.invalidate_plan();
        }
        self.preview_logic();
        self.export_logic();
    }

    fn on_history_changed(&mut self) {
        self.sync_sources();
        self.timeline.prune_selection(self.history.current());
        let duration = self.state().timeline.duration();
        if self.timeline.playhead > duration {
            self.timeline.playhead = duration;
        }
        self.reset_fields();
        self.preview_on_project_changed();
        self.export.invalidate_plan();
    }

    pub fn reset_fields(&mut self) {
        self.clip_in.editing = false;
        self.clip_out.editing = false;
        self.offset.editing = false;
    }

    pub fn commit(&mut self, next: ProjectState) {
        self.history.commit(next);
        self.after_edit();
    }

    /// The edit is seen at once (not on the next frame), so the code right after it reads a fresh view.
    fn after_edit(&mut self) {
        if self.history.generation != self.seen_generation {
            self.seen_generation = self.history.generation;
            self.on_history_changed();
        }
    }

    /// Commits an edit of the timeline alone.
    pub fn commit_timeline(&mut self, edit: impl FnOnce(&Timeline) -> Timeline) {
        let next = self.state().with_timeline(edit(&self.state().timeline));
        self.commit(next);
    }

    /// Commits, re-fitting the zoom if the whole timeline was visible before (so appended clips show up).
    pub fn commit_keeping_fit(&mut self, next: ProjectState) {
        let t = &self.timeline;
        let was_fit = t.zoom <= 0.0 || self.state().timeline.duration().total_seconds() * t.zoom <= t.viewport_width + 1.0;
        self.commit(next);
        if was_fit && self.has_clips() {
            let duration = self.state().timeline.duration();
            self.timeline.fit(duration);
        }
    }

    pub fn has_clips(&self) -> bool {
        !self.state().timeline.clips().is_empty()
    }

    pub fn is_dirty(&self) -> bool {
        **self.history.current() != *self.saved_state || self.export.current_settings() != self.saved_export
    }

    pub fn project_name(&self) -> String {
        match &self.file_path {
            Some(p) => Path::new(p).file_stem().map(|s| s.to_string_lossy().into_owned()).unwrap_or_default(),
            None => "Untitled".into(),
        }
    }

    pub fn title(&self) -> String {
        format!("{}{} — Mane FFmpeg", if self.is_dirty() { "• " } else { "" }, self.project_name())
    }

    pub fn is_empty(&self) -> bool {
        self.file_path.is_none() && self.state().sources.is_empty() && self.state().audio_files.is_empty()
    }

    pub fn timeline_duration_text(&self) -> String {
        time::display(self.state().timeline.duration(), false)
    }

    // ---- Sources ------------------------------------------------------------------------------------

    /// Keeps the Files panel and the catalog in step with the project state (after edits, undo, load).
    fn sync_sources(&mut self) {
        let state = self.history.current().clone();
        files::sync_list(&mut self.sources, &state.sources, FileKind::Video);
        files::sync_list(&mut self.audio_files, &state.audio_files, FileKind::Audio);
        for item in self.sources.iter_mut().chain(self.audio_files.iter_mut()) {
            item.refresh(&self.catalog);
        }
        self.catalog.sync(&state.sources, &state.audio_files);
        for item in self.sources.iter_mut().chain(self.audio_files.iter_mut()) {
            item.refresh(&self.catalog);
        }
    }

    fn on_source_analyzed(&mut self, id: &str) {
        let catalog = &self.catalog;
        if let Some(item) = self.sources.iter_mut().chain(self.audio_files.iter_mut()).find(|i| i.id() == id) {
            item.refresh(catalog);
        }
    }

    /// A video or audio file in the Files panel, by id.
    pub fn file_item(&self, id: &str) -> Option<&FileItem> {
        self.sources.iter().chain(self.audio_files.iter()).find(|i| i.id() == id)
    }

    /// The color `path` has on the Audio tab, or will get once it is added.
    pub fn audio_file_color(&self, path: &str) -> egui::Color32 {
        self.audio_files
            .iter()
            .find(|f| f.source.path == path)
            .map(|f| f.color())
            .unwrap_or_else(|| files::audio_color(files::free_color_index(&self.audio_files)))
    }

    pub fn choose_audio_stream(&mut self, kind: FileKind, id: &str, index: i32) {
        let Some(old) = self.state().files(kind).iter().find(|f| f.id == id).cloned() else { return };
        if old.audio_stream_index == index || index < 0 {
            return;
        }
        let updated = SourceMedia { audio_stream_index: index, ..old.clone() };
        let next = self.state().replace_file(kind, &old, updated);
        self.commit(next);
    }

    /// Adds files by what they hold: videos as sources, each appended as a whole clip; files with only sound to the
    /// Audio tab.
    pub fn add_media(&mut self, paths: Vec<String>) {
        self.add_probed(paths, AddRouting::ByContent, |w, added| {
            w.commit_keeping_fit(added.state.clone());
            w.show_added(&added, None);
        });
    }

    /// Adds every file that has sound to the Audio tab, videos too (their sound can replace a clip's).
    pub fn add_audio(&mut self, paths: Vec<String>) {
        self.add_probed(paths, AddRouting::AllAsAudio, |w, added| {
            w.commit(added.state.clone());
            w.show_added(&added, None);
        });
    }

    /// Files dropped on a clip: added like `add_media`, and the first audio file replaces the clip's sound in the
    /// same undo step.
    pub fn replace_clip_audio_from_files(&mut self, paths: Vec<String>, clip_id: String) {
        self.add_probed(paths, AddRouting::ByContent, move |w, added| {
            let state = match added.audio_files.first() {
                Some(f) => pe::replace_clip_audio(&added.state, &clip_id, f),
                None => added.state.clone(),
            };
            let replaced = state != added.state;
            w.commit_keeping_fit(state);
            w.show_added(&added, replaced.then_some(clip_id));
        });
    }

    /// Probes the files that exist (once ffmpeg is known) on a background thread, then adds them as `routing` says.
    fn add_probed(&mut self, paths: Vec<String>, routing: AddRouting, then: impl FnOnce(&mut ProjectWindow, AddedFiles) + Send + 'static) {
        let mut files: Vec<String> = Vec::new();
        for p in paths {
            let full = std::path::absolute(&p).map(|p| p.to_string_lossy().into_owned()).unwrap_or(p);
            if Path::new(&full).is_file() && !files.contains(&full) {
                files.push(full);
            }
        }
        if files.is_empty() {
            return;
        }
        self.status_message =
            Some(if files.len() == 1 { format!("Adding {}…", file_name(&files[0])) } else { format!("Adding {} files…", files.len()) });
        let app = self.app.clone();
        self.spawn(
            move || {
                // Files opened right at launch wait for ffmpeg to be found.
                while !app.is_tools_located() {
                    std::thread::sleep(std::time::Duration::from_millis(50));
                }
                if app.tools().is_none() {
                    return Err(app.tools_error().unwrap_or_default());
                }
                let probes: Vec<_> = files
                    .into_iter()
                    .map(|f| {
                        let a = app.clone();
                        std::thread::spawn(move || match a.analyzers.probe(&f) {
                            Ok(info) => ProbedFile { path: f, info: Some(info), error: None },
                            Err(e) => ProbedFile { path: f, info: None, error: Some(e) },
                        })
                    })
                    .collect();
                Ok(probes.into_iter().map(|h| h.join().unwrap()).collect::<Vec<_>>())
            },
            move |w, result: Result<Vec<ProbedFile>, String>| match result {
                Err(e) => w.status_message = Some(e),
                Ok(probed) => {
                    let added = pe::add_probed(w.state(), &probed, routing, &new_source);
                    then(w, added);
                }
            },
        );
    }

    /// Shows the Audio tab when audio files were added, and says what happened.
    fn show_added(&mut self, added: &AddedFiles, replaced_clip: Option<String>) {
        if !added.audio_files.is_empty() {
            self.files_tab = FileKind::Audio;
        }
        self.status_message = if !added.skipped.is_empty() {
            Some(format!("Skipped: {}", added.skipped.join("; ")))
        } else if replaced_clip.is_some() {
            Some(self.replaced_message(&added.audio_files[0]))
        } else if !added.audio_files.is_empty() {
            let n = added.audio_files.len();
            Some(format!(
                "{}: drag one onto a clip to replace its sound.",
                if n == 1 { "Audio file added".to_string() } else { format!("{n} audio files added") }
            ))
        } else {
            None
        };
        if let Some(c) = replaced_clip {
            self.timeline.select_only(&c);
            self.reset_fields();
        }
    }

    pub fn pick_media(&mut self) {
        let folder = self.app.settings().current().last_media_folder.clone();
        let pick = Pick::Open {
            title: "Add videos".into(),
            filters: vec![("Videos".into(), exts(VIDEO_EXTENSIONS)), ("All files".into(), vec!["*".into()])],
            multiple: true,
            folder,
        };
        self.pick(pick, |w, paths| {
            remember_media_folder(w, &paths);
            w.add_media(paths.iter().map(|p| p.to_string_lossy().into_owned()).collect());
        });
    }

    pub fn pick_audio(&mut self) {
        let folder = self.app.settings().current().last_media_folder.clone();
        let pick = Pick::Open {
            title: "Add audio".into(),
            filters: vec![
                ("Audio".into(), exts(AUDIO_EXTENSIONS)),
                ("Videos (use their sound)".into(), exts(VIDEO_EXTENSIONS)),
                ("All files".into(), vec!["*".into()]),
            ],
            multiple: true,
            folder,
        };
        self.pick(pick, |w, paths| {
            remember_media_folder(w, &paths);
            w.add_audio(paths.iter().map(|p| p.to_string_lossy().into_owned()).collect());
        });
    }

    pub fn append_source(&mut self, id: &str) {
        let Some(info) = self.catalog.info(id).filter(|i| i.video.is_some()).cloned() else { return };
        let next = pe::append_clip(self.state(), id, Time::ZERO, info.duration);
        self.commit_keeping_fit(next);
    }

    pub fn remove_source(&mut self, kind: FileKind, id: &str) {
        let used = pe::clips_using(self.state(), kind, id);
        let id = id.to_string();
        let name = self.file_item(&id).map(|i| i.name()).unwrap_or_default();
        let rid = id.clone();
        let remove = move |w: &mut ProjectWindow| {
            let id = rid;
            let next = pe::remove_file(w.state(), kind, &id);
            w.commit(next);
        };
        if used == 0 {
            remove(self);
            return;
        }
        let (title, message) = if kind == FileKind::Audio {
            ("Remove audio file?", format!("\"{name}\" plays on {used} clip(s). They will go back to their own audio (you can undo)."))
        } else {
            ("Remove video?", format!("\"{name}\" is used by {used} clip(s) on the timeline. They will be removed too (you can undo)."))
        };
        self.ask(title, message, &["Remove", "Cancel"], move |w, answer| {
            if answer == 0 {
                remove(w);
            }
        });
    }

    pub fn relink(&mut self, kind: FileKind, id: &str) {
        let Some(old) = self.state().files(kind).iter().find(|f| f.id == id).cloned() else { return };
        let pick = Pick::Open { title: format!("Locate \"{}\"", old.display_name()), filters: vec![], multiple: false, folder: None };
        self.pick(pick, move |w, paths| {
            let Some(path) = paths.first() else { return };
            let updated = new_source_with_id(&path.to_string_lossy(), &old.id, old.audio_stream_index);
            let next = w.state().replace_file(kind, &old, updated);
            w.commit(next);
        });
    }

    // ---- Files ----------------------------------------------------------------------------------------

    /// Loads a project into this window (it must be empty, or the user agreed to replace it).
    pub fn load_project(&mut self, path: &str, then: impl FnOnce(&mut ProjectWindow, Result<(), String>) + Send + 'static) {
        let project = match project_file::load(Path::new(path)) {
            Ok(p) => p,
            Err(e) => return then(self, Err(e.0)),
        };
        let path = path.to_string();
        self.resolve_preset(project.export.clone(), move |w, preset| {
            w.sources.clear();
            w.audio_files.clear();
            w.files_tab = FileKind::Video;
            w.file_path = Some(path);
            w.layout = project.layout.clone();
            w.layout_pending = true;
            w.export.apply_settings(&project.export, preset);
            w.on_preset_selected();
            w.saved_export = w.export.current_settings();
            w.history.reset(project.state.clone());
            w.saved_state = w.history.current().clone();
            w.timeline.clear_selection();
            w.timeline.zoom = project.layout.timeline_zoom;
            w.timeline.scroll = project.layout.timeline_scroll;
            w.timeline.playhead = project.layout.playhead;
            w.set_volume(project.layout.volume);
            w.after_edit();
            then(w, Ok(()));
        });
    }

    pub fn open_project_dialog(&mut self) {
        let folder = self.app.settings().current().last_project_folder.clone();
        let pick = Pick::Open {
            title: "Open project".into(),
            filters: vec![("Mane FFmpeg project".into(), vec![project_file::EXTENSION.into()])],
            multiple: false,
            folder,
        };
        let id = self.id;
        self.pick(pick, move |w, paths| {
            if let Some(p) = paths.first() {
                w.app.send(AppCommand::OpenProject { path: p.to_string_lossy().into_owned(), requester: Some(id) });
            }
        });
    }

    /// Saves (asking where for a new project, or for "Save As"); `then` learns whether it was saved.
    pub fn save(&mut self, save_as: bool, then: impl FnOnce(&mut ProjectWindow, bool) + Send + 'static) {
        if let (Some(path), false) = (self.file_path.clone(), save_as) {
            let ok = self.save_to(&path);
            return then(self, ok);
        }
        let suggested = if self.project_name() == "Untitled" && !self.sources.is_empty() {
            Path::new(&self.sources[0].source.path).file_stem().map(|s| s.to_string_lossy().into_owned()).unwrap_or_default()
        } else {
            self.project_name()
        };
        let folder = self
            .file_path
            .as_ref()
            .and_then(|p| Path::new(p).parent().map(|d| d.to_string_lossy().into_owned()))
            .or_else(|| self.app.settings().current().last_project_folder.clone())
            .or_else(|| self.sources.first().and_then(|s| Path::new(&s.source.path).parent().map(|d| d.to_string_lossy().into_owned())));
        let pick = Pick::Save {
            title: "Save project".into(),
            file_name: format!("{suggested}.{}", project_file::EXTENSION),
            folder,
            filters: vec![("Mane FFmpeg project".into(), vec![project_file::EXTENSION.into()])],
        };
        self.pick(pick, move |w, paths| {
            let Some(p) = paths.first() else { return then(w, false) };
            let mut path = p.to_string_lossy().into_owned();
            if !path.to_lowercase().ends_with(&format!(".{}", project_file::EXTENSION)) {
                path.push('.');
                path.push_str(project_file::EXTENSION);
            }
            let ok = w.save_to(&path);
            then(w, ok);
        });
    }

    fn save_to(&mut self, path: &str) -> bool {
        let export = self.export.current_settings();
        let project = Project { state: (**self.history.current()).clone(), export: export.clone(), layout: self.capture_layout() };
        match project_file::save(&project, Path::new(path)) {
            Ok(()) => {
                self.file_path = Some(path.to_string());
                self.saved_state = self.history.current().clone();
                self.saved_export = export;
                self.app.settings().add_recent_project(path);
                self.status_message = Some(format!("Saved {}", file_name(path)));
                self.export.invalidate_plan();
                true
            }
            Err(e) => {
                app_log::write(&format!("Save {path} failed: {}", e.0));
                self.info("Couldn't save the project", e.0);
                false
            }
        }
    }

    pub fn undo(&mut self) {
        self.history.undo();
        self.after_edit();
    }

    pub fn redo(&mut self) {
        self.history.redo();
        self.after_edit();
    }

    // ---- Inspector ------------------------------------------------------------------------------------

    pub fn primary_clip(&self) -> Option<&Clip> {
        self.state().timeline.clips().iter().find(|c| self.timeline.selection.contains(&c.id))
    }

    /// The selected clips, or the clip under the playhead.
    fn target_clips(&self) -> HashSet<String> {
        if !self.timeline.selection.is_empty() {
            return self.timeline.selection.clone();
        }
        match self.state().timeline.locate(self.timeline.playhead) {
            Some((i, _)) => [self.state().timeline.clips()[i].id.clone()].into(),
            None => HashSet::new(),
        }
    }

    pub fn has_selection(&self) -> bool {
        !self.timeline.selection.is_empty()
    }

    pub fn selection_title(&self) -> String {
        match self.timeline.selection.len() {
            0 => "No clip selected".into(),
            1 => self
                .primary_clip()
                .and_then(|c| self.state().source(&c.source_id))
                .map(|s| s.display_name())
                .unwrap_or_else(|| "Clip".into()),
            n => format!("{n} clips selected"),
        }
    }

    pub fn selection_details(&self) -> String {
        let Some(clip) = self.primary_clip() else {
            return "Click a clip on the timeline to edit it. Space plays, S splits at the playhead.".into();
        };
        if self.timeline.selection.len() > 1 {
            let total: Time =
                self.state().timeline.clips().iter().filter(|c| self.timeline.selection.contains(&c.id)).map(|c| c.duration()).sum();
            return format!("Total {}", time::display(total, false));
        }
        format!("Duration {}", time::display(clip.duration(), false))
    }

    pub fn clip_in_text(&self) -> String {
        self.primary_clip().map(|c| time::display(c.r#in, false)).unwrap_or_default()
    }

    pub fn clip_out_text(&self) -> String {
        self.primary_clip().map(|c| time::display(c.out, false)).unwrap_or_default()
    }

    pub fn set_clip_in(&mut self, text: &str) {
        let (Some(c), Some(t)) = (self.primary_clip().cloned(), time::try_parse(text)) else { return };
        let grids = self.grids();
        self.commit_timeline(|tl| te::set_range(tl, &c.id, t, c.out, &|id| grids(id)));
    }

    pub fn set_clip_out(&mut self, text: &str) {
        let (Some(c), Some(t)) = (self.primary_clip().cloned(), time::try_parse(text)) else { return };
        let grids = self.grids();
        self.commit_timeline(|tl| te::set_range(tl, &c.id, c.r#in, t, &|id| grids(id)));
    }

    /// Audio offset of the selected clip(s), in milliseconds; positive = audio later.
    pub fn audio_offset_text(&self) -> String {
        self.primary_clip()
            .map(|c| time::trim_number((c.active_audio_offset().total_ms() * 10.0).round_ties_even() / 10.0, 1))
            .unwrap_or_default()
    }

    /// Takes simple arithmetic too, so "0-700" sets −700 ms.
    pub fn set_audio_offset_text(&mut self, text: &str) {
        let Some(primary) = self.primary_clip().cloned() else { return };
        if self.timeline.selection.is_empty() {
            return;
        }
        let Some(ms) = mane_core::model::arithmetic::try_evaluate(text) else { return };
        let max = te::max_audio_offset_for(&primary).total_ms();
        let offset = Time::from_ms_f(ms.clamp(-max, max));
        let sel = self.timeline.selection.clone();
        self.commit_timeline(|tl| te::set_audio_offset(tl, &sel, offset));
    }

    pub fn audio_offset_hint(&self) -> String {
        let Some(c) = self.primary_clip() else { return String::new() };
        let secs = |t: Time| format!("{} s", time::trim_number(t.total_seconds(), 3));
        if let Some(r) = &c.replaced_audio {
            return if r.offset == Time::ZERO {
                "Starts where it was dropped.".into()
            } else if r.offset > Time::ZERO {
                format!("Starts {} later.", secs(r.offset))
            } else {
                format!("Starts {} into the file.", secs(-r.offset))
            };
        }
        let Some(frame) = self.frame_duration(c).filter(|f| *f > Time::ZERO) else { return String::new() };
        if c.audio_offset == Time::ZERO {
            return "Audio in sync with its video.".into();
        }
        let frames = c.audio_offset.total_ms() / frame.total_ms();
        let sign = if frames >= 0.0 { "+" } else { "−" };
        format!("≈ {sign}{:.1} frames · audio plays {}", frames.abs(), if c.audio_offset > Time::ZERO { "later" } else { "earlier" })
    }

    pub fn has_replaced_audio(&self) -> bool {
        self.primary_clip().is_some_and(|c| c.replaced_audio.is_some())
    }

    pub fn replaced_audio_name(&self) -> String {
        match self.primary_clip().and_then(|c| c.replaced_audio.as_ref()) {
            Some(r) => {
                format!("Audio: {}", self.state().audio_file(&r.file_id).map(|f| f.display_name()).unwrap_or_else(|| "missing file".into()))
            }
            None => String::new(),
        }
    }

    pub fn replaced_audio_color(&self) -> Option<egui::Color32> {
        let r = self.primary_clip()?.replaced_audio.as_ref()?;
        self.file_item(&r.file_id).map(|i| i.color())
    }

    pub fn loop_audio(&self) -> bool {
        self.primary_clip().is_some_and(|c| c.loops_audio())
    }

    pub fn set_loop_audio(&mut self, value: bool) {
        if value != self.loop_audio() {
            let sel = self.timeline.selection.clone();
            self.commit_timeline(|t| te::set_audio_loop(t, &sel, value));
        }
    }

    pub fn apply_offset_text(&self) -> &'static str {
        if self.has_replaced_audio() {
            "Use this offset for all clips with this audio"
        } else {
            "Use this offset for all clips of this video"
        }
    }

    /// Plays audio file `file_id` instead of the clip's sound (an audio file dropped on it).
    pub fn replace_clip_audio(&mut self, clip_id: &str, file_id: &str) {
        let next = pe::replace_clip_audio(self.state(), clip_id, file_id);
        if next == **self.history.current() {
            return;
        }
        self.commit(next);
        self.timeline.select_only(clip_id);
        self.status_message = Some(self.replaced_message(file_id));
    }

    fn replaced_message(&self, file_id: &str) -> String {
        format!(
            "{} replaces the clip's sound. Drag its audio row to move it; Selection has Loop audio.",
            self.state().audio_file(file_id).map(|f| f.display_name()).unwrap_or_default()
        )
    }

    pub fn restore_original_audio(&mut self) {
        let targets = self.target_clips();
        self.commit_timeline(|t| te::restore_original_audio(t, &targets));
    }

    /// Where cuts may land in a source: on frames, and in points on keyframes while snapping to them.
    pub fn grid_of(&self, source_id: &str) -> Option<CutGrid> {
        let source = self.state().source(source_id)?;
        let info = self.catalog.info(source_id)?;
        let keyframes = if self.snap_to_keyframes { self.catalog.keyframes(source).map(|k| k.times.clone()) } else { None };
        Some(CutGrid::for_media(info, keyframes))
    }

    /// The grids of every source, gathered for an edit that needs them.
    pub fn grids(&self) -> impl Fn(&str) -> Option<CutGrid> + use<> {
        let all: std::collections::HashMap<String, CutGrid> =
            self.state().sources.iter().filter_map(|s| self.grid_of(&s.id).map(|g| (s.id.clone(), g))).collect();
        move |id: &str| all.get(id).cloned()
    }

    fn frame_duration(&self, clip: &Clip) -> Option<Time> {
        self.catalog.info(&clip.source_id)?.video.as_ref().map(|v| time::frame_duration(v.frame_rate))
    }

    pub fn set_snap_to_keyframes(&mut self, value: bool) {
        self.snap_to_keyframes = value;
        self.catalog.set_want_keyframes(value);
    }

    // ---- Edits ----------------------------------------------------------------------------------------

    pub fn split(&mut self) {
        if !self.has_clips() {
            return;
        }
        let grids = self.grids();
        let (next, right) = te::split(&self.state().timeline, self.timeline.playhead, &|id| grids(id));
        let Some(right) = right else {
            self.status_message = Some("Move the playhead inside a clip (away from its edges) to split.".into());
            return;
        };
        let state = self.state().with_timeline(next.clone());
        self.commit(state);
        // The left half, the part already played, keeps the clip's id.
        let left = next.clips()[next.index_of(&right).unwrap() - 1].id.clone();
        self.timeline.select_only(&left);
        self.status_message = None;
    }

    pub fn delete_selection(&mut self) {
        if self.timeline.selection.is_empty() {
            self.status_message = Some("Select clips to delete.".into());
            return;
        }
        // Clips deleted before the playhead pull what's under it earlier; the playhead follows it.
        let playhead = self.timeline.playhead;
        let sel = self.timeline.selection.clone();
        let moved = te::time_after_delete(&self.state().timeline, &sel, playhead);
        self.commit_timeline(|t| te::delete(t, &sel));
        if moved != playhead {
            self.move_playhead_after_edit(moved);
        }
    }

    pub fn select_all(&mut self) {
        let t = self.state().timeline.clone();
        self.timeline.select_all(&t);
        self.reset_fields();
    }

    /// Slips the audio of the selected clips (or the clip under the playhead) by `delta`.
    pub fn nudge_audio(&mut self, delta: Time) {
        let targets = self.target_clips();
        self.commit_timeline(|t| te::nudge_audio_offset(t, &targets, delta));
    }

    pub fn nudge_audio_frames(&mut self, frames: i64) {
        let targets = self.target_clips();
        let Some(clip) = self.state().timeline.clips().iter().find(|c| targets.contains(&c.id)).cloned() else { return };
        let Some(frame) = self.frame_duration(&clip) else { return };
        self.commit_timeline(|t| te::nudge_audio_offset(t, &targets, frame * frames));
    }

    pub fn reset_audio_offset(&mut self) {
        let targets = self.target_clips();
        self.commit_timeline(|t| te::set_audio_offset(t, &targets, Time::ZERO));
    }

    pub fn apply_offset_to_source(&mut self) {
        let Some(clip) = self.primary_clip().cloned() else { return };
        if let Some(r) = &clip.replaced_audio {
            self.commit_timeline(|t| te::set_replaced_audio_offset_for_file(t, &r.file_id, r.offset));
            self.status_message = Some(format!(
                "Audio offset {} applied to every clip playing {}.",
                time::offset(r.offset),
                self.state().audio_file(&r.file_id).map(|f| f.display_name()).unwrap_or_default()
            ));
            return;
        }
        self.commit_timeline(|t| te::set_audio_offset_for_source(t, &clip.source_id, clip.audio_offset));
        self.status_message = Some(format!(
            "Audio offset {} applied to every clip of {}.",
            time::offset(clip.audio_offset),
            self.state().source(&clip.source_id).map(|f| f.display_name()).unwrap_or_default()
        ));
    }

    pub fn zoom_in(&mut self) {
        let d = self.state().timeline.duration();
        self.timeline.zoom_around_playhead(1.5, d);
    }

    pub fn zoom_out(&mut self) {
        let d = self.state().timeline.duration();
        self.timeline.zoom_around_playhead(1.0 / 1.5, d);
    }

    pub fn zoom_fit(&mut self) {
        let d = self.state().timeline.duration();
        self.timeline.fit(d);
    }

    // ---- Layout ---------------------------------------------------------------------------------------

    pub fn capture_layout(&self) -> ProjectLayout {
        let compact = self.preview.is_compact;
        let rect = self.outer_rect.unwrap_or(egui::Rect::from_min_size(egui::Pos2::ZERO, egui::vec2(1400.0, 900.0)));
        // A compact window is saved with its full-size bounds, which reach up from its bottom edge.
        let height = if compact { self.expanded_height.unwrap_or(rect.height()) } else { rect.height() };
        let top = if compact && !self.maximized { rect.max.y - height } else { rect.min.y };
        ProjectLayout {
            window: Some(WindowBounds {
                x: rect.min.x.round() as i32,
                y: top.round() as i32,
                width: rect.width() as f64,
                height: height as f64,
                maximized: self.maximized,
            }),
            left_pane_width: self.left_pane_width as f64,
            timeline_zoom: self.timeline.zoom,
            timeline_scroll: self.timeline.scroll,
            playhead: self.timeline.playhead,
            volume: self.preview.volume,
            compact,
        }
    }

    // ---- Closing --------------------------------------------------------------------------------------

    /// Asks before a running export or unsaved edits would be lost (stopping or saving them as the user chooses);
    /// `then` learns whether the window may close. `stop_export` stops an export the user agreed to stop right away.
    pub fn confirm_close(&mut self, stop_export: bool, then: impl FnOnce(&mut ProjectWindow, bool) + Send + 'static) {
        if self.export.is_exporting {
            self.ask(
                "An export is still running",
                "Stop the export and close this window?",
                &["Stop and close", "Keep working"],
                move |w, a| {
                    if a != 0 {
                        return then(w, false);
                    }
                    if stop_export {
                        w.export.cancel();
                    }
                    w.confirm_unsaved(then);
                },
            );
            return;
        }
        self.confirm_unsaved(then);
    }

    fn confirm_unsaved(&mut self, then: impl FnOnce(&mut ProjectWindow, bool) + Send + 'static) {
        if !(self.is_dirty() && self.has_clips()) {
            return then(self, true);
        }
        let name = self.project_name();
        self.ask(
            format!("Save changes to “{name}”?"),
            "Your edits will be lost if you don't save them.",
            &["Save", "Don't save", "Cancel"],
            move |w, a| match a {
                0 => w.save(false, then),
                1 => then(w, true),
                _ => then(w, false),
            },
        );
    }

    pub fn reveal_in_file_manager(&self, path: &str) {
        let result = if cfg!(target_os = "macos") {
            std::process::Command::new("/usr/bin/open").args(["-R", path]).spawn()
        } else {
            let dir = Path::new(path).parent().map(|p| p.to_string_lossy().into_owned()).unwrap_or_default();
            std::process::Command::new("xdg-open").arg(dir).spawn()
        };
        // Nothing to do if no file manager is available.
        let _ = result;
    }
}

pub fn file_name(path: &str) -> String {
    Path::new(path).file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_else(|| path.to_string())
}

fn exts(list: &[&str]) -> Vec<String> {
    list.iter().map(|e| e.to_string()).collect()
}

fn remember_media_folder(w: &mut ProjectWindow, paths: &[std::path::PathBuf]) {
    if let Some(dir) = paths.first().and_then(|p| p.parent()) {
        let dir = dir.to_string_lossy().into_owned();
        w.app.settings().update(|s| s.last_media_folder = Some(dir));
    }
}

/// A new source entry for a file not listed yet.
pub fn new_source(path: &str) -> SourceMedia {
    new_source_with_id(path, &ids::new(), 0)
}

fn new_source_with_id(path: &str, id: &str, stream: i32) -> SourceMedia {
    let full = std::path::absolute(path).map(|p| p.to_string_lossy().into_owned()).unwrap_or_else(|_| path.to_string());
    let stamp = FileStamp::of(&full);
    SourceMedia { id: id.to_string(), path: full, file_size: stamp.size, last_write_utc: stamp.last_write_utc, audio_stream_index: stream }
}

pub fn is_project(path: &str) -> bool {
    Path::new(path).extension().is_some_and(|e| e.eq_ignore_ascii_case(project_file::EXTENSION))
}

pub fn looks_like_audio(path: &str) -> bool {
    Path::new(path).extension().is_some_and(|e| AUDIO_EXTENSIONS.iter().any(|a| e.eq_ignore_ascii_case(a)))
}
