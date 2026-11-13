//! Runs one export plan with ffmpeg, reporting progress and cleaning up after itself. The export can be paused:
//! ffmpeg is frozen with SIGSTOP and continues with SIGCONT. Runs on the calling (worker) thread; `pause`, `resume`
//! and `cancel` come from any other thread.

use super::export_planner::ExportPlan;
use super::progress::{FfmpegProgress, ProgressParser};
use super::tools::FfmpegTools;
use crate::model::time::{Time, parse_float, seconds};
use crate::persistence::app_log;
use std::collections::VecDeque;
use std::io::{BufRead, BufReader, Write};
use std::path::Path;
use std::process::{Child, Command, Stdio};
use std::sync::{Arc, Condvar, Mutex};
use std::time::{Duration, Instant};

const LOG_LINES: usize = 40;

#[derive(Clone, Debug, PartialEq)]
pub struct ExportProgress {
    pub fraction: f64,
    pub done: Time,
    pub total: Time,
    pub speed: Option<f64>,
    pub remaining: Option<Time>,
    pub step_title: String,
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum ExportOutcome {
    Succeeded,
    Cancelled,
    Failed,
}

#[derive(Clone, Debug)]
pub struct ExportResult {
    pub outcome: ExportOutcome,
    pub message: String,
    pub elapsed: Duration,
    pub log: Option<String>,
}

/// A stopwatch that leaves out the time it was stopped.
#[derive(Default)]
struct Clock {
    accumulated: Duration,
    started: Option<Instant>,
}

impl Clock {
    fn elapsed(&self) -> Duration {
        self.accumulated + self.started.map(|s| s.elapsed()).unwrap_or_default()
    }
    fn start(&mut self) {
        if self.started.is_none() {
            self.started = Some(Instant::now());
        }
    }
    fn stop(&mut self) {
        if let Some(s) = self.started.take() {
            self.accumulated += s.elapsed();
        }
    }
    fn restart(&mut self) {
        self.accumulated = Duration::ZERO;
        self.started = Some(Instant::now());
    }
    fn reset(&mut self) {
        self.accumulated = Duration::ZERO;
        self.started = None;
    }
}

#[derive(Default)]
struct State {
    paused: bool,
    cancelled: bool,
    clock: Clock,
    /// The running ffmpeg, which a pause has to stop.
    process_id: Option<u32>,
    /// While ffmpeg is stopped: kills it if this process dies, since nothing else would ever continue it.
    guard: Option<Child>,
}

#[derive(Clone)]
pub struct ExportRunner {
    tools: FfmpegTools,
    state: Arc<(Mutex<State>, Condvar)>,
}

// kill(2) signal numbers come from libc for the platform (macOS uses the BSD ones).
const STOP: i32 = libc::SIGSTOP;
const CONT: i32 = libc::SIGCONT;

impl ExportRunner {
    pub fn new(tools: FfmpegTools) -> ExportRunner {
        ExportRunner { tools, state: Arc::new((Mutex::new(State::default()), Condvar::new())) }
    }

    /// Freezes ffmpeg where it is and holds the next step until `resume`. Works before the run starts too.
    pub fn pause(&self) {
        let mut s = self.state.0.lock().unwrap();
        if s.paused {
            return;
        }
        s.paused = true;
        s.clock.stop();
        if let Some(pid) = s.process_id {
            freeze(&mut s, pid);
        }
    }

    pub fn resume(&self) {
        let mut s = self.state.0.lock().unwrap();
        if !s.paused {
            return;
        }
        if let Some(pid) = s.process_id {
            signal(pid, CONT);
        }
        release_guard(&mut s);
        s.clock.start();
        s.paused = false;
        self.state.1.notify_all();
    }

    pub fn is_paused(&self) -> bool {
        self.state.0.lock().unwrap().paused
    }

    /// Stops the export: ffmpeg is killed and the partial output deleted.
    pub fn cancel(&self) {
        let mut s = self.state.0.lock().unwrap();
        s.cancelled = true;
        if let Some(pid) = s.process_id {
            signal(pid, libc::SIGKILL);
        }
        release_guard(&mut s);
        self.state.1.notify_all();
    }

    fn running_time(&self) -> Duration {
        self.state.0.lock().unwrap().clock.elapsed()
    }

    fn cancelled(&self) -> bool {
        self.state.0.lock().unwrap().cancelled
    }

    /// Waits while paused; false when cancelled.
    fn while_paused(&self) -> bool {
        let mut s = self.state.0.lock().unwrap();
        while s.paused && !s.cancelled {
            s = self.state.1.wait(s).unwrap();
        }
        !s.cancelled
    }

    pub fn run(&self, plan: &ExportPlan, progress: &mut dyn FnMut(ExportProgress)) -> ExportResult {
        {
            // Planning may have been paused and resumed already; only the run counts.
            let mut s = self.state.0.lock().unwrap();
            if s.paused { s.clock.reset() } else { s.clock.restart() }
        }
        let result = self.run_steps(plan, progress);
        let _ = std::fs::remove_dir_all(&plan.work_directory);
        result
    }

    fn run_steps(&self, plan: &ExportPlan, progress: &mut dyn FnMut(ExportProgress)) -> ExportResult {
        let output = &plan.output_path;
        let total: Time = plan.steps.iter().map(|s| s.duration).sum();
        let mut done_before = Time::ZERO;
        let mut output_touched = false;
        let log = Arc::new(Mutex::new(VecDeque::<String>::new()));
        let fail = |msg: String, touched: bool, log: Option<String>| {
            delete_output(output, touched);
            ExportResult { outcome: ExportOutcome::Failed, message: msg, elapsed: self.running_time(), log }
        };
        let cancelled = |touched: bool| {
            delete_output(output, touched);
            ExportResult { outcome: ExportOutcome::Cancelled, message: "Export cancelled".into(), elapsed: self.running_time(), log: None }
        };
        if let Err(e) = std::fs::create_dir_all(&plan.work_directory) {
            return fail(e.to_string(), false, None);
        }
        for f in &plan.files {
            if let Err(e) = std::fs::write(&f.path, &f.content) {
                return fail(e.to_string(), false, None);
            }
        }
        for step in &plan.steps {
            if !self.while_paused() {
                return cancelled(output_touched);
            }
            output_touched |= step.writes_output;
            let fraction = |done: Time| if total > Time::ZERO { (done / total).clamp(0.0, 1.0) } else { 0.0 };
            progress(ExportProgress {
                fraction: fraction(done_before),
                done: done_before,
                total,
                speed: None,
                remaining: None,
                step_title: step.title.clone(),
            });
            let arguments = match &step.shift {
                Some(shift) => step.arguments_for(&seconds(self.concat_shift(&shift.list_path))),
                None => step.arguments.clone(),
            };
            let mut child = match Command::new(&self.tools.ffmpeg)
                .args(&arguments)
                .stdin(Stdio::null())
                .stdout(Stdio::piped())
                .stderr(Stdio::piped())
                .spawn()
            {
                Ok(c) => c,
                Err(e) => return fail(format!("Can't start ffmpeg: {e}"), output_touched, None),
            };
            {
                let mut s = self.state.0.lock().unwrap();
                if s.cancelled {
                    let _ = child.kill();
                } else {
                    s.process_id = Some(child.id());
                    // Paused while ffmpeg was starting.
                    if s.paused {
                        freeze(&mut s, child.id());
                    }
                }
            }
            let stderr = child.stderr.take().unwrap();
            let log_writer = log.clone();
            let err_thread = std::thread::spawn(move || {
                for line in BufReader::new(stderr).lines().map_while(Result::ok) {
                    let mut l = log_writer.lock().unwrap();
                    l.push_back(line);
                    if l.len() > LOG_LINES {
                        l.pop_front();
                    }
                }
            });
            let mut parser = ProgressParser::default();
            let step_started = Instant::now();
            let running_before = self.running_time();
            for line in BufReader::new(child.stdout.take().unwrap()).lines().map_while(Result::ok) {
                if let Some(p) = parser.feed(&line) {
                    let position = p.position.min(step.duration);
                    let done = done_before + position;
                    let paused = step_started.elapsed().saturating_sub(self.running_time().saturating_sub(running_before));
                    let speed = unpaused_speed(&p, paused);
                    let remaining = speed.filter(|s| *s > 0.0).map(|s| Time::from_seconds((total - done).total_seconds() / s));
                    progress(ExportProgress { fraction: fraction(done), done, total, speed, remaining, step_title: step.title.clone() });
                }
            }
            let status = child.wait();
            let _ = err_thread.join();
            {
                let mut s = self.state.0.lock().unwrap();
                s.process_id = None;
                release_guard(&mut s);
            }
            if self.cancelled() {
                return cancelled(output_touched);
            }
            let code = status.as_ref().ok().and_then(|s| s.code());
            if code != Some(0) {
                let lines = log.lock().unwrap();
                let text = lines.iter().cloned().collect::<Vec<_>>().join("\n");
                let first = lines
                    .iter()
                    .rev()
                    .find(|l| !l.trim().is_empty())
                    .cloned()
                    .unwrap_or_else(|| format!("ffmpeg exited with code {}", code.unwrap_or(-1)));
                return fail(first, output_touched, Some(text));
            }
            done_before += step.duration;
        }
        progress(ExportProgress { fraction: 1.0, done: total, total, speed: None, remaining: Some(Time::ZERO), step_title: "Done".into() });
        let name = Path::new(output).file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
        ExportResult { outcome: ExportOutcome::Succeeded, message: format!("Saved {name}"), elapsed: self.running_time(), log: None }
    }

    /// How far ffmpeg will shift a concat input: minus its start time.
    fn concat_shift(&self, list: &str) -> Time {
        let out = Command::new(&self.tools.ffprobe)
            .args(["-v", "error", "-f", "concat", "-safe", "0", "-i", list, "-show_entries", "format=start_time", "-of", "csv=p=0"])
            .stdin(Stdio::null())
            .output();
        out.ok()
            .and_then(|o| parse_float(String::from_utf8_lossy(&o.stdout).trim()))
            .map(|start| Time::from_seconds(-start))
            .unwrap_or(Time::ZERO)
    }
}

/// ffmpeg's speed is its position over the wall time since it started, so time spent paused drags it down until
/// the end. Take that time back out.
fn unpaused_speed(p: &FfmpegProgress, paused: Duration) -> Option<f64> {
    let speed = p.speed?;
    let ran = p.position.total_seconds() / speed - paused.as_secs_f64();
    Some(if ran > 0.0 { p.position.total_seconds() / ran } else { speed })
}

fn freeze(s: &mut State, pid: u32) {
    signal(pid, STOP);
    s.guard = start_guard(pid);
}

/// A stopped ffmpeg outlives a crash or a force quit and never wakes up. The guard reads a pipe from this process:
/// a line means "resumed", the end of the file means this process is gone, and then it kills ffmpeg.
fn start_guard(pid: u32) -> Option<Child> {
    match Command::new("/bin/sh").args(["-c", "read -r line || kill -KILL \"$0\"", &pid.to_string()]).stdin(Stdio::piped()).spawn() {
        Ok(c) => Some(c),
        Err(e) => {
            app_log::write(&format!("Can't watch the paused ffmpeg: {e}"));
            None
        }
    }
}

fn release_guard(s: &mut State) {
    if let Some(mut g) = s.guard.take() {
        if let Some(mut stdin) = g.stdin.take() {
            let _ = writeln!(stdin, "resumed");
        }
        let _ = g.wait();
    }
}

fn signal(pid: u32, sig: i32) {
    // SAFETY: kill(2) with a pid of our own child.
    if unsafe { libc::kill(pid as i32, sig) } != 0 {
        app_log::write(&format!("Can't send signal {sig} to ffmpeg (pid {pid}): {}", std::io::Error::last_os_error()));
    }
}

fn delete_output(path: &str, touched: bool) {
    if touched && Path::new(path).is_file() {
        let _ = std::fs::remove_file(path);
    }
}
