//! Snapshot-based undo history. `current` is always the live state. Snapshots are shared (`Arc`), so a checkpoint
//! is recognized by identity, like the C# reference comparison.

use std::collections::VecDeque;
use std::sync::Arc;

pub struct UndoStack<T: PartialEq> {
    undo: VecDeque<Arc<T>>,
    redo: Vec<Arc<T>>,
    current: Arc<T>,
    gesture_base: Option<Arc<T>>,
    capacity: usize,
    /// Bumped on every change.
    pub generation: u64,
}

impl<T: PartialEq> UndoStack<T> {
    pub fn new(initial: T) -> Self {
        Self::with_capacity(initial, 300)
    }

    pub fn with_capacity(initial: T, capacity: usize) -> Self {
        UndoStack { undo: VecDeque::new(), redo: Vec::new(), current: Arc::new(initial), gesture_base: None, capacity, generation: 0 }
    }

    pub fn current(&self) -> &Arc<T> {
        &self.current
    }

    pub fn can_undo(&self) -> bool {
        !self.undo.is_empty()
    }

    pub fn can_redo(&self) -> bool {
        !self.redo.is_empty()
    }

    fn changed(&mut self) {
        self.generation += 1;
    }

    /// Makes `next` current and records the previous state. No-op if equal.
    pub fn commit(&mut self, next: T) -> bool {
        self.end_gesture();
        if next == *self.current {
            return false;
        }
        let prev = std::mem::replace(&mut self.current, Arc::new(next));
        self.record(prev);
        self.changed();
        true
    }

    pub fn in_gesture(&self) -> bool {
        self.gesture_base.is_some()
    }

    /// Starts a drag: later `replace_current` calls collapse into one undo step.
    pub fn begin_gesture(&mut self) {
        if self.gesture_base.is_none() {
            self.gesture_base = Some(self.current.clone());
        }
    }

    /// Replaces the current state without recording history (live drag previews).
    pub fn replace_current(&mut self, state: T) {
        if state == *self.current {
            return;
        }
        self.current = Arc::new(state);
        self.changed();
    }

    /// Finishes a drag, recording one undo step if anything changed.
    pub fn end_gesture(&mut self) {
        let Some(start) = self.gesture_base.take() else { return };
        if *start == *self.current {
            return;
        }
        self.record(start);
        self.changed();
    }

    fn record(&mut self, previous: Arc<T>) {
        self.undo.push_back(previous);
        if self.undo.len() > self.capacity {
            self.undo.pop_front();
        }
        self.redo.clear();
    }

    /// Abandons a drag and restores the state it started from.
    pub fn cancel_gesture(&mut self) {
        let Some(start) = self.gesture_base.take() else { return };
        self.current = start;
        self.changed();
    }

    /// Goes back to `checkpoint`, an earlier `current`, dropping the steps made since as if they had never been made:
    /// they can't be redone, and the history before the checkpoint is kept. False, changing nothing, when the
    /// checkpoint isn't in the history to undo (undone past, trimmed or reset).
    pub fn rewind_to(&mut self, checkpoint: &Arc<T>) -> bool {
        self.end_gesture();
        if Arc::ptr_eq(checkpoint, &self.current) {
            return true;
        }
        let Some(pos) = self.undo.iter().rposition(|s| Arc::ptr_eq(s, checkpoint)) else {
            return false;
        };
        self.undo.truncate(pos);
        self.redo.clear();
        self.current = checkpoint.clone();
        self.changed();
        true
    }

    pub fn undo(&mut self) -> bool {
        self.end_gesture();
        let Some(last) = self.undo.pop_back() else { return false };
        let cur = std::mem::replace(&mut self.current, last);
        self.redo.push(cur);
        self.changed();
        true
    }

    pub fn redo(&mut self) -> bool {
        self.end_gesture();
        let Some(next) = self.redo.pop() else { return false };
        let cur = std::mem::replace(&mut self.current, next);
        self.undo.push_back(cur);
        self.changed();
        true
    }

    pub fn reset(&mut self, state: T) {
        self.gesture_base = None;
        self.undo.clear();
        self.redo.clear();
        self.current = Arc::new(state);
        self.changed();
    }
}
