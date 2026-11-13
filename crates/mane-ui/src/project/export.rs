//! One project's export: preset, output file, the ffmpeg command preview and the running export.

use super::{ProjectWindow, file_name};
use crate::shared::AppShared;
use mane_core::ffmpeg::export_planner::{self as planner, ExportJob, ExportPlan};
use mane_core::ffmpeg::export_runner::{ExportOutcome, ExportProgress, ExportRunner};
use mane_core::ffmpeg::preset_analysis::PresetAnalysis;
use mane_core::media::keyframes::{KeyframeIndex, KeyframeService};
use mane_core::model::{ExportSettings, MediaInfo, Preset, ProjectState, SourceMedia, ids};
use mane_core::persistence::{app_log, app_paths};
use mane_core::platform::{notifier, sleep_inhibitor::SleepInhibitor};
use std::collections::HashMap;
use std::path::Path;
use std::sync::Arc;
use std::sync::mpsc::{Receiver, channel};
use std::time::{Duration, Instant};

const PLAN_DELAY: Duration = Duration::from_millis(400);

enum RunMsg {
    Preparing(String),
    Progress(ExportProgress),
}

pub struct ExportState {
    pub presets: Vec<Preset>,
    pub selected: Option<Preset>,
    /// Explicit output path; None means "derive from the first video".
    pub output_path: Option<String>,
    pub is_exporting: bool,
    pub is_paused: bool,
    pub progress: f64,
    pub progress_text: Option<String>,
    pub status: Option<String>,
    pub failed: bool,
    pub can_reveal: bool,
    pub log: Option<String>,
    pub command_preview: Option<String>,
    pub show_command: bool,
    plan_at: Option<Instant>,
    plan_generation: u64,
    runner: Option<ExportRunner>,
    keep_awake: Option<SleepInhibitor>,
    keep_awake_reason: Option<String>,
    last_output: Option<String>,
    /// An existing output file the user agreed to replace (in the save dialog or when asked).
    confirmed_output: Option<String>,
    preparing: Option<String>,
    last_progress: Option<ExportProgress>,
    run_rx: Option<Receiver<RunMsg>>,
}

impl ExportState {
    pub fn new(app: &AppShared) -> ExportState {
        let mut e = ExportState {
            presets: Vec::new(),
            selected: None,
            output_path: None,
            is_exporting: false,
            is_paused: false,
            progress: 0.0,
            progress_text: None,
            status: None,
            failed: false,
            can_reveal: false,
            log: None,
            command_preview: None,
            show_command: false,
            plan_at: None,
            plan_generation: 0,
            runner: None,
            keep_awake: None,
            keep_awake_reason: None,
            last_output: None,
            confirmed_output: None,
            preparing: None,
            last_progress: None,
            run_rx: None,
        };
        e.reload_presets(app);
        e
    }

    pub fn reload_presets(&mut self, app: &AppShared) {
        let selected = self.selected.as_ref().map(|p| p.id.clone());
        self.presets = app.presets().presets().to_vec();
        self.selected = selected.and_then(|id| self.presets.iter().find(|p| p.id == id).cloned()).or_else(|| self.default_preset());
    }

    /// "for storage" is the default for new projects.
    fn default_preset(&self) -> Option<Preset> {
        let id = Preset::for_storage().id;
        self.presets.iter().find(|p| p.id == id).or(self.presets.first()).cloned()
    }

    pub fn current_settings(&self) -> ExportSettings {
        ExportSettings {
            preset_id: self.selected.as_ref().map(|p| p.id.clone()),
            preset_snapshot: self.selected.clone(),
            output_path: self.output_path.clone(),
        }
    }

    pub fn apply_settings(&mut self, settings: &ExportSettings, preset: Option<Preset>) {
        self.selected = preset.or_else(|| self.default_preset());
        self.output_path = settings.output_path.clone().filter(|p| Path::new(p).is_absolute());
    }

    pub fn invalidate_plan(&mut self) {
        self.plan_generation += 1;
        if self.show_command {
            self.plan_at = Some(Instant::now() + PLAN_DELAY);
        }
    }

    pub fn cancel(&mut self) {
        if let Some(r) = &self.runner {
            r.cancel();
        }
    }

    pub fn is_idle(&self) -> bool {
        !self.is_exporting
    }

    fn show_progress_text(&mut self) {
        let mut parts = Vec::new();
        if self.is_paused {
            parts.push("Paused".to_string());
        }
        if let Some(p) = &self.last_progress {
            parts.push(format!("{:.0}%", p.fraction * 100.0));
            parts.push(p.step_title.clone());
            if !self.is_paused
                && p.fraction > 0.01
                && let Some(left) = p.remaining
            {
                parts.push(format!("{} left", format_elapsed(Duration::from_secs_f64(left.total_seconds().max(0.0)))));
            }
            if !self.is_paused
                && let Some(speed) = p.speed
            {
                parts.push(format!("{}×", speed_text(speed)));
            }
        } else if let Some(p) = &self.preparing {
            parts.push(p.clone());
        }
        self.progress_text = Some(parts.join(" · "));
    }

    fn show_result(&mut self, message: String, failed: bool) {
        self.status = Some(message);
        self.failed = failed;
        self.progress_text = None;
    }
}

/// .NET's "0.0#": one or two decimals.
fn speed_text(speed: f64) -> String {
    let s = format!("{speed:.2}");
    if s.ends_with('0') { s[..s.len() - 1].to_string() } else { s }
}

pub fn format_elapsed(t: Duration) -> String {
    let s = t.as_secs_f64();
    if s >= 3600.0 {
        format!("{}h {:02}m", (s / 3600.0) as u64, (s / 60.0) as u64 % 60)
    } else if s >= 60.0 {
        format!("{}m {:02}s", (s / 60.0) as u64, s as u64 % 60)
    } else {
        format!("{}s", (s.round() as u64).max(1))
    }
}

/// What planning needs, copied out of the window so it can run on a worker thread.
struct PlanInput {
    state: ProjectState,
    media: HashMap<String, MediaInfo>,
    preset: Option<Preset>,
    /// Sources on the timeline that have been analyzed, for keyframes.
    used: Vec<(SourceMedia, MediaInfo)>,
    keyframes: Option<Arc<KeyframeService>>,
}

impl PlanInput {
    /// Plans, scanning keyframes first when the preset copies video; blocks.
    fn plan(self, output: &str, work_dir: &str, status: &dyn Fn(String)) -> Result<ExportPlan, String> {
        let Some(preset) = self.preset else { return Err("Choose a preset.".into()) };
        let mut keyframes: HashMap<String, KeyframeIndex> = HashMap::new();
        if PresetAnalysis::of(&preset).video_copy()
            && let Some(service) = &self.keyframes
        {
            for (source, info) in &self.used {
                let index = match service.try_get(&source.path, info) {
                    Some(k) => k,
                    None => {
                        status(format!("Finding keyframes in {}…", source.display_name()));
                        service.get(&source.path, info)?
                    }
                };
                keyframes.insert(source.id.clone(), (*index).clone());
            }
        }
        planner::plan(&ExportJob {
            state: self.state,
            media: self.media,
            preset,
            output_path: output.into(),
            work_directory: work_dir.into(),
            keyframes,
        })
        .map_err(|e| e.0)
    }
}

impl ProjectWindow {
    pub fn output_display(&self) -> String {
        match &self.export.output_path {
            Some(p) => p.clone(),
            None => match self.default_output_path() {
                Some(p) => format!("{p}  (automatic)"),
                None => "Choose where to save…".into(),
            },
        }
    }

    fn default_output_path(&self) -> Option<String> {
        let first = self.state().sources.first()?;
        let path = Path::new(&first.path);
        let dir = path.parent()?;
        let base = if self.file_path.is_some() { self.project_name() } else { format!("{}-edit", path.file_stem()?.to_string_lossy()) };
        let ext = match &self.export.selected {
            Some(p) if !p.extension.is_empty() => format!(".{}", p.extension),
            _ => path.extension().map(|e| format!(".{}", e.to_string_lossy())).unwrap_or_default(),
        };
        let mut candidate = dir.join(format!("{base}{ext}")).to_string_lossy().into_owned();
        let mut n = 2;
        while Path::new(&candidate).exists() || self.state().sources.iter().any(|s| s.path == candidate) {
            candidate = dir.join(format!("{base}-{n}{ext}")).to_string_lossy().into_owned();
            n += 1;
        }
        Some(candidate)
    }

    /// Picking a preset turns keyframe snapping on for stream-copy presets (and off for the others).
    pub fn on_preset_selected(&mut self) {
        if let Some(args) = self.export.selected.as_ref().and_then(|p| PresetAnalysis::of(p).arguments) {
            self.set_snap_to_keyframes(args.video_copy());
        }
        self.export.invalidate_plan();
    }

    pub fn select_preset(&mut self, id: &str) {
        if let Some(p) = self.export.presets.iter().find(|p| p.id == id).cloned() {
            self.export.selected = Some(p);
            self.on_preset_selected();
        }
    }

    pub fn set_output_path(&mut self, path: Option<String>) {
        self.export.output_path = path;
        self.export.invalidate_plan();
    }

    pub fn set_show_command(&mut self, show: bool) {
        self.export.show_command = show;
        if show {
            self.export.invalidate_plan();
        }
    }

    fn plan_input(&self) -> PlanInput {
        let state = (**self.history.current()).clone();
        let used = state
            .sources
            .iter()
            .filter(|s| state.timeline.clips().iter().any(|c| c.source_id == s.id))
            .filter_map(|s| self.catalog.info(&s.id).map(|i| (s.clone(), i.clone())))
            .collect();
        PlanInput {
            media: self.catalog.media(),
            preset: self.export.selected.clone(),
            used,
            keyframes: self.app.analyzers.keyframes_service(),
            state,
        }
    }

    /// The project's preset, or one that runs exactly like it. One this machine doesn't have comes from the project
    /// file and its arguments go straight to ffmpeg, so it's only added when the user agrees.
    pub fn resolve_preset(&mut self, settings: ExportSettings, then: impl FnOnce(&mut ProjectWindow, Option<Preset>) + Send + 'static) {
        let known = self.app.presets().find_for(settings.preset_id.as_deref(), settings.preset_snapshot.as_ref()).cloned();
        if let Some(p) = known {
            return then(self, Some(p));
        }
        let Some(snapshot) = settings.preset_snapshot.clone().filter(|s| Some(&s.id) == settings.preset_id.as_ref()) else {
            return then(self, None);
        };
        self.ask(
            format!("Add the preset “{}”?", snapshot.name),
            format!(
                "This project was exported with a preset you don't have:\n\n{}\n\nOnly add presets from projects you trust: ffmpeg runs these options as they are.",
                snapshot.arguments
            ),
            &["Add preset", "Use my default"],
            move |w, answer| {
                if answer != 0 {
                    return then(w, None);
                }
                w.app.presets().save(snapshot.with_defaults());
                let found = w.app.presets().find(Some(&snapshot.id)).cloned();
                w.export.reload_presets(&w.app);
                then(w, found);
            },
        );
    }

    pub(super) fn export_logic(&mut self) {
        // The command preview, a moment after the last change.
        if let Some(at) = self.export.plan_at {
            let now = Instant::now();
            if now >= at {
                self.export.plan_at = None;
                self.update_plan_preview();
            } else {
                self.waker.wake_after(at - now);
            }
        }
        let mut msgs = Vec::new();
        if let Some(rx) = &self.export.run_rx {
            while let Ok(m) = rx.try_recv() {
                msgs.push(m);
            }
        }
        for m in msgs {
            match m {
                RunMsg::Preparing(s) => {
                    self.export.preparing = Some(s);
                    self.export.show_progress_text();
                }
                RunMsg::Progress(p) => {
                    self.export.progress = p.fraction * 100.0;
                    self.export.last_progress = Some(p);
                    self.export.show_progress_text();
                }
            }
        }
    }

    fn update_plan_preview(&mut self) {
        let Some(tools) = self.app.tools() else {
            self.export.command_preview = self.app.tools_error();
            return;
        };
        let input = self.plan_input();
        let output = self.export.output_path.clone().or_else(|| self.default_output_path()).unwrap_or_else(|| "out.mp4".into());
        let work = app_paths::temp_directory().join("preview").to_string_lossy().into_owned();
        let generation = self.export.plan_generation;
        self.spawn(
            move || input.plan(&output, &work, &|_| {}),
            move |w, result| {
                if w.export.plan_generation != generation {
                    return;
                }
                w.export.command_preview = Some(match result {
                    Ok(plan) => {
                        let notes = if plan.notes.is_empty() {
                            String::new()
                        } else {
                            format!("{}\n\n", plan.notes.iter().map(|n| format!("• {n}")).collect::<Vec<_>>().join("\n"))
                        };
                        format!("{notes}{}", plan.command_text(&tools.ffmpeg))
                    }
                    Err(e) => e,
                });
            },
        );
    }

    pub fn choose_output(&mut self) {
        let suggested = self.export.output_path.clone().or_else(|| self.default_output_path()).unwrap_or_else(|| "export.mp4".into());
        let path = Path::new(&suggested);
        let pick = super::tasks::Pick::Save {
            title: "Export to".into(),
            file_name: path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default(),
            folder: path.parent().map(|p| p.to_string_lossy().into_owned()),
            filters: vec![],
        };
        self.pick(pick, |w, paths| {
            let Some(p) = paths.first() else { return };
            let p = p.to_string_lossy().into_owned();
            // The save dialog has already asked about replacing an existing file.
            w.export.confirmed_output = Some(p.clone());
            w.set_output_path(Some(p));
        });
    }

    pub fn export(&mut self) {
        if self.export.is_exporting {
            return;
        }
        let Some(tools) = self.app.tools() else {
            let e = self.app.tools_error().unwrap_or_else(|| "ffmpeg not found".into());
            self.export.show_result(e, true);
            return;
        };
        let Some(output) = self.export.output_path.clone().or_else(|| self.default_output_path()) else {
            self.export.show_result("Add videos to the timeline first.".into(), true);
            return;
        };
        // An output path restored from a project was never confirmed in the save dialog.
        if self.export.confirmed_output.as_deref() != Some(&output) && Path::new(&output).exists() {
            let dir = Path::new(&output).parent().map(|p| p.display().to_string()).unwrap_or_default();
            self.ask(
                format!("Replace “{}”?", file_name(&output)),
                format!("A file with this name already exists in {dir}. Exporting will replace it."),
                &["Replace", "Cancel"],
                move |w, a| {
                    if a == 0 {
                        w.export.confirmed_output = Some(output);
                        w.export();
                    }
                },
            );
            return;
        }
        let e = &mut self.export;
        e.is_exporting = true;
        e.failed = false;
        e.can_reveal = false;
        e.log = None;
        e.status = None;
        e.progress = 0.0;
        e.last_progress = None;
        e.preparing = Some("Preparing…".into());
        e.show_progress_text();
        // Created before planning, so a pause while finding keyframes holds the export too.
        let runner = ExportRunner::new(tools.clone());
        e.runner = Some(runner.clone());
        let work = app_paths::temp_directory().join(format!("export-{}", &ids::new()[..8])).to_string_lossy().into_owned();
        let reason = format!("Exporting {}", file_name(&output));
        e.keep_awake = Some(SleepInhibitor::acquire(&reason));
        e.keep_awake_reason = Some(reason);
        let (tx, rx) = channel();
        e.run_rx = Some(rx);
        let input = self.plan_input();
        let waker = self.waker.clone();
        let out = output.clone();
        self.spawn(
            move || {
                let wake = || waker.wake();
                let status_tx = tx.clone();
                let status = move |s: String| {
                    let _ = status_tx.send(RunMsg::Preparing(s));
                };
                let plan = input.plan(&out, &work, &|s| {
                    status(s);
                    wake();
                })?;
                app_log::write(&format!("Export {:?} → {out}\n{}", plan.mode, plan.command_text(&tools.ffmpeg)));
                let mut last = Instant::now() - Duration::from_secs(1);
                let result = runner.run(&plan, &mut |p| {
                    let _ = tx.send(RunMsg::Progress(p));
                    // At most ~20 repaints a second for progress.
                    if last.elapsed() > Duration::from_millis(50) {
                        last = Instant::now();
                        wake();
                    }
                });
                app_log::write(&format!(
                    "Export {:?}: {} ({:?}){}",
                    result.outcome,
                    result.message,
                    result.elapsed,
                    result.log.as_ref().map(|l| format!("\n{l}")).unwrap_or_default()
                ));
                Ok(result)
            },
            move |w, result| {
                w.export_logic();
                let notify = w.app.settings().current().notifications;
                match result {
                    Err(e) => w.export.show_result(e, true),
                    Ok(r) => {
                        w.export.last_output = Some(output.clone());
                        let name = file_name(&output);
                        match r.outcome {
                            ExportOutcome::Succeeded => {
                                w.export.show_result(format!("✓ {} in {}", r.message, format_elapsed(r.elapsed)), false);
                                w.export.can_reveal = true;
                                if notify {
                                    notifier::notify("Export finished", &format!("{name} is ready ({}).", format_elapsed(r.elapsed)));
                                }
                            }
                            ExportOutcome::Cancelled => w.export.show_result("Export cancelled.".into(), false),
                            ExportOutcome::Failed => {
                                w.export.show_result(format!("Export failed: {}", r.message), true);
                                w.export.log = r.log;
                                if notify {
                                    notifier::notify("Export failed", &format!("{name}: {}", r.message));
                                }
                            }
                        }
                    }
                }
                let e = &mut w.export;
                e.is_exporting = false;
                e.is_paused = false;
                e.runner = None;
                e.keep_awake = None;
                e.run_rx = None;
            },
        );
    }

    pub fn pause_export(&mut self) {
        if !self.export.is_exporting || self.export.is_paused {
            return;
        }
        if let Some(r) = &self.export.runner {
            r.pause();
        }
        self.export.is_paused = true;
        // A paused export doesn't need the computer awake.
        self.export.keep_awake = None;
        self.export.show_progress_text();
    }

    pub fn resume_export(&mut self) {
        if !self.export.is_paused {
            return;
        }
        if self.export.keep_awake.is_none()
            && let Some(reason) = &self.export.keep_awake_reason
        {
            self.export.keep_awake = Some(SleepInhibitor::acquire(reason));
        }
        self.export.is_paused = false;
        if let Some(r) = &self.export.runner {
            r.resume();
        }
        self.export.show_progress_text();
    }

    pub fn cancel_export(&mut self) {
        self.export.cancel();
    }

    pub fn reveal_output(&self) {
        if let Some(p) = self.export.last_output.as_ref().filter(|p| Path::new(p).exists()) {
            self.reveal_in_file_manager(p);
        }
    }
}
