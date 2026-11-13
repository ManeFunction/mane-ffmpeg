//! Wakes one window for another frame, from any thread. The window it wakes can change: when the first window
//! closes, the next takes over the app's root window, and every waker it handed out follows it there.

use std::sync::{Arc, Mutex};
use std::time::Duration;

#[derive(Clone)]
pub struct Waker {
    ctx: egui::Context,
    slot: Arc<Mutex<egui::ViewportId>>,
}

impl Waker {
    pub fn new(ctx: egui::Context, viewport: egui::ViewportId) -> Waker {
        Waker { ctx, slot: Arc::new(Mutex::new(viewport)) }
    }

    pub fn viewport(&self) -> egui::ViewportId {
        *self.slot.lock().unwrap()
    }

    pub fn set_viewport(&self, viewport: egui::ViewportId) {
        *self.slot.lock().unwrap() = viewport;
    }

    pub fn wake(&self) {
        self.ctx.request_repaint_of(self.viewport());
    }

    pub fn wake_after(&self, delay: Duration) {
        self.ctx.request_repaint_after_for(delay, self.viewport());
    }

    pub fn as_fn(&self) -> Arc<dyn Fn() + Send + Sync> {
        let w = self.clone();
        Arc::new(move || w.wake())
    }
}
