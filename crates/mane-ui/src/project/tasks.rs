//! Work a window waits for without blocking its frames: background jobs, file pickers and message boxes. Each holds
//! a continuation that runs on the window, in its frame, once the result is in.

use super::ProjectWindow;
use std::path::PathBuf;
use std::sync::mpsc::{Receiver, Sender, channel};

pub type Then<T> = Box<dyn FnOnce(&mut ProjectWindow, T) + Send>;

/// A finished job: its continuation, ready to run.
pub type Done = Box<dyn FnOnce(&mut ProjectWindow) + Send>;

pub struct Tasks {
    tx: Sender<Done>,
    rx: Receiver<Done>,
    /// Jobs under way (pickers included).
    pub running: usize,
}

impl Default for Tasks {
    fn default() -> Self {
        let (tx, rx) = channel();
        Tasks { tx, rx, running: 0 }
    }
}

impl Tasks {
    pub fn sender(&self) -> Sender<Done> {
        self.tx.clone()
    }

    pub fn take_done(&mut self) -> Vec<Done> {
        let mut out = Vec::new();
        while let Ok(d) = self.rx.try_recv() {
            out.push(d);
        }
        self.running = self.running.saturating_sub(out.len());
        out
    }
}

/// A message box: a title, a message and buttons; `then` gets the index of the pressed button, or -1 when closed
/// (Escape), and the checkbox's state.
pub struct Ask {
    pub title: String,
    pub message: String,
    pub buttons: Vec<String>,
    pub checkbox: Option<(String, bool)>,
    pub link: Option<(String, String)>,
    pub then: Then<(i32, bool)>,
}

/// File dialogs, run on their own thread so the window keeps drawing.
#[derive(Clone, Debug)]
pub enum Pick {
    Open { title: String, filters: Vec<(String, Vec<String>)>, multiple: bool, folder: Option<String> },
    Save { title: String, file_name: String, folder: Option<String>, filters: Vec<(String, Vec<String>)> },
}

impl Pick {
    /// Shows the dialog; blocks until it closes.
    pub fn run(self) -> Vec<PathBuf> {
        match self {
            Pick::Open { title, filters, multiple, folder } => {
                let mut d = rfd::FileDialog::new().set_title(title);
                for (name, ext) in &filters {
                    d = d.add_filter(name, ext);
                }
                if let Some(f) = folder.filter(|f| std::path::Path::new(f).is_dir()) {
                    d = d.set_directory(f);
                }
                if multiple { d.pick_files().unwrap_or_default() } else { d.pick_file().into_iter().collect() }
            }
            Pick::Save { title, file_name, folder, filters } => {
                let mut d = rfd::FileDialog::new().set_title(title).set_file_name(file_name);
                for (name, ext) in &filters {
                    d = d.add_filter(name, ext);
                }
                if let Some(f) = folder.filter(|f| std::path::Path::new(f).is_dir()) {
                    d = d.set_directory(f);
                }
                d.save_file().into_iter().collect()
            }
        }
    }
}

impl ProjectWindow {
    /// Runs `work` on a background thread and `then` on the window with its result.
    pub fn spawn<T: Send + 'static>(
        &mut self,
        work: impl FnOnce() -> T + Send + 'static,
        then: impl FnOnce(&mut ProjectWindow, T) + Send + 'static,
    ) {
        let tx = self.tasks.sender();
        let waker = self.waker.clone();
        self.tasks.running += 1;
        std::thread::spawn(move || {
            let result = work();
            let _ = tx.send(Box::new(move |w: &mut ProjectWindow| then(w, result)));
            waker.wake();
        });
    }

    /// Shows a file dialog; `then` gets the chosen paths (empty when cancelled).
    pub fn pick(&mut self, pick: Pick, then: impl FnOnce(&mut ProjectWindow, Vec<PathBuf>) + Send + 'static) {
        // macOS wants its panels on the main thread; elsewhere the dialog runs beside the window so it keeps drawing.
        if cfg!(target_os = "macos") {
            let paths = pick.run();
            then(self, paths);
        } else {
            self.spawn(move || pick.run(), then);
        }
    }

    /// Asks the user; `then` gets the pressed button's index (-1 for Escape).
    pub fn ask(
        &mut self,
        title: impl Into<String>,
        message: impl Into<String>,
        buttons: &[&str],
        then: impl FnOnce(&mut ProjectWindow, i32) + Send + 'static,
    ) {
        self.dialogs.push(Ask {
            title: title.into(),
            message: message.into(),
            buttons: buttons.iter().map(|b| b.to_string()).collect(),
            checkbox: None,
            link: None,
            then: Box::new(move |w, (i, _)| then(w, i)),
        });
        self.waker.wake();
    }

    /// A message with an OK button.
    pub fn info(&mut self, title: impl Into<String>, message: impl Into<String>) {
        self.ask(title, message, &["OK"], |_, _| {});
    }
}
