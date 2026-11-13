//! Development aid: when MANE_FFMPEG_SCRIPT points at a text file, runs its commands against the first window (add,
//! seek, split, export, snapshot…) and prints state, so the real UI can be checked end to end. Debug builds only.

use crate::project::ProjectWindow;

pub struct Automation {
    lines: Vec<String>,
    next: usize,
    wait_until: Option<std::time::Instant>,
    waiting_for: Option<String>,
    wait_started: Option<std::time::Instant>,
}

pub fn start_if_requested(w: &mut ProjectWindow) {
    let Ok(script) = std::env::var("MANE_FFMPEG_SCRIPT") else { return };
    let Ok(text) = std::fs::read_to_string(&script) else { return };
    w.debug = Some(Automation {
        lines: text.lines().map(String::from).collect(),
        next: 0,
        wait_until: None,
        waiting_for: None,
        wait_started: None,
    });
    w.wake();
}

impl Automation {
    pub fn step(&mut self, w: &mut ProjectWindow, ctx: &egui::Context) {
        crate::debug_automation_impl::step(self, w, ctx);
    }
}

pub(crate) struct Cursor<'a> {
    pub lines: &'a [String],
    pub next: &'a mut usize,
    pub wait_until: &'a mut Option<std::time::Instant>,
    pub waiting_for: &'a mut Option<String>,
    pub wait_started: &'a mut Option<std::time::Instant>,
}

impl Automation {
    pub(crate) fn cursor(&mut self) -> Cursor<'_> {
        Cursor {
            lines: &self.lines,
            next: &mut self.next,
            wait_until: &mut self.wait_until,
            waiting_for: &mut self.waiting_for,
            wait_started: &mut self.wait_started,
        }
    }
}
