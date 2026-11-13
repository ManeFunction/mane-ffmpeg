//! Dragging a file from the Audio tab onto a clip to replace its sound. An in-app pointer drag rather than the
//! platform's drag and drop: it can't get stuck in a native drag session, draws its own feedback on the timeline and
//! can be scripted. The window only turns pointer positions into a distance moved and the clip under the pointer.

use super::timeline_gestures::DRAG_THRESHOLD;

/// The file pressed: its id, name and color.
#[derive(Clone, Debug, PartialEq)]
pub struct DraggedFile {
    pub id: String,
    pub name: String,
    pub color: egui::Color32,
}

/// An audio file dragged over the timeline: its name and color, and the clip it would replace the sound of.
#[derive(Clone, Debug, PartialEq)]
pub struct AudioDropTarget {
    pub name: String,
    pub color: egui::Color32,
    pub clip_id: Option<String>,
}

/// What the drag changes: feedback, the drop and the status line.
#[derive(Clone, Debug, PartialEq)]
pub enum AudioDragEffect {
    ShowDrop(Option<AudioDropTarget>),
    Status(Option<String>),
    Replace { clip_id: String, file_id: String },
}

#[derive(Default, Debug)]
pub struct AudioDragGesture {
    pressed: Option<DraggedFile>,
    /// The pointer moved far enough with a file pressed: the drag is on.
    pub is_dragging: bool,
    /// The clip the file would be dropped on.
    pub target_clip: Option<String>,
}

impl AudioDragGesture {
    /// The pointer went down on a row (None when not on a usable file).
    pub fn press(&mut self, item: Option<DraggedFile>) -> Vec<AudioDragEffect> {
        let effects = self.end(false);
        self.pressed = item;
        effects
    }

    /// The pointer moved `distance` pixels from the press and is over `clip_id` (or no clip). Returns whether the
    /// drag is on, and what it changes.
    pub fn move_to(&mut self, distance: f64, clip_id: Option<String>) -> (bool, Vec<AudioDragEffect>) {
        let Some(item) = self.pressed.clone() else { return (false, vec![]) };
        let mut effects = Vec::new();
        if !self.is_dragging {
            if distance <= DRAG_THRESHOLD {
                return (false, effects);
            }
            self.is_dragging = true;
            effects.push(AudioDragEffect::Status(Some(format!("Drop {} on a clip to replace its sound.", item.name))));
        }
        self.target_clip = clip_id.clone();
        effects.push(AudioDragEffect::ShowDrop(Some(AudioDropTarget { name: item.name, color: item.color, clip_id })));
        (true, effects)
    }

    /// The pointer went up: drops the file on the target clip, if any.
    pub fn release(&mut self) -> Vec<AudioDragEffect> {
        self.end(true)
    }

    /// Escape, or the pointer went away: the drag ends without dropping.
    pub fn cancel(&mut self) -> Vec<AudioDragEffect> {
        self.end(false)
    }

    fn end(&mut self, drop: bool) -> Vec<AudioDragEffect> {
        let (item, clip, dragging) = (self.pressed.take(), self.target_clip.take(), self.is_dragging);
        self.is_dragging = false;
        if !dragging {
            return vec![];
        }
        let mut effects = vec![AudioDragEffect::ShowDrop(None), AudioDragEffect::Status(None)];
        if let (true, Some(item), Some(clip)) = (drop, item, clip) {
            effects.push(AudioDragEffect::Replace { clip_id: clip, file_id: item.id });
        }
        effects
    }
}
