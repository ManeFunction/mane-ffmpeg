//! Pure editing: timeline cuts and trims, project file edits, playhead snapping and undo.

pub mod cut_grid;
pub mod project_editor;
pub mod timeline_editor;
pub mod timeline_snap;
pub mod undo_stack;

pub use cut_grid::CutGrid;
pub use undo_stack::UndoStack;
