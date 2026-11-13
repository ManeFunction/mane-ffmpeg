//! Dragging the crop rectangle on the preview: moving it (drag inside), resizing it (drag an edge or corner) or
//! drawing a new one (drag outside it), kept inside the frame and at least `CropRect::MIN_SIZE`. Works in screen
//! coordinates of the overlay; the overlay only forwards pointer events and draws `live`.

use super::preview_geometry;
use super::rect::{Pt, R};
use mane_core::model::CropRect;

/// How far outside or inside an edge, in pixels, still grabs it.
pub const GRAB: f64 = 9.0;

pub const LEFT: u8 = 1;
pub const TOP: u8 = 2;
pub const RIGHT: u8 = 4;
pub const BOTTOM: u8 = 8;

#[derive(Clone, Copy, PartialEq, Eq, Debug, Default)]
enum Kind {
    #[default]
    None,
    Move,
    Resize,
}

#[derive(Default, Debug)]
pub struct CropDragGesture {
    kind: Kind,
    edges: u8,
    frame: R,
    press_point: Pt,
    press_rect: R,
    press_aspect: f64,
    aspect_lock: f64,
    /// The rectangle while it is dragged; None when no drag is on.
    pub live: Option<R>,
}

fn within(v: f64, min: f64, max: f64) -> f64 {
    min.max(v.min(max))
}

fn clamp_point(p: Pt, area: R) -> Pt {
    Pt::new(within(p.x, area.x, area.right()), within(p.y, area.y, area.bottom()))
}

impl CropDragGesture {
    pub fn is_dragging(&self) -> bool {
        self.kind != Kind::None
    }

    /// The button went down at `point`, with the output frame at `frame` and the crop shown at `shown`.
    /// `aspect_lock` is the width over height to keep, or 0.
    pub fn press(&mut self, point: Pt, frame: R, shown: R, aspect_lock: f64) {
        self.frame = frame;
        self.aspect_lock = aspect_lock;
        self.press_point = point;
        self.press_rect = shown;
        self.press_aspect = if shown.height > 0.0 { shown.width / shown.height } else { 1.0 };
        self.edges = hit_edges(shown, point);
        if self.edges != 0 {
            self.kind = Kind::Resize;
        } else if shown.contains(point) {
            self.kind = Kind::Move;
        } else {
            // Outside the rectangle: draw a new one from here, as if dragging the corner of an empty one.
            let start = clamp_point(point, frame);
            self.press_rect = R::new(start.x, start.y, 0.0, 0.0);
            self.edges = RIGHT | BOTTOM;
            self.kind = Kind::Resize;
            if aspect_lock > 0.0 {
                self.press_aspect = aspect_lock;
            }
        }
        self.live = Some(shown);
    }

    /// The pointer moved; `keep_shape` (Shift) holds the shape the rectangle had.
    pub fn move_to(&mut self, point: Pt, keep_shape: bool) {
        if self.kind == Kind::None {
            return;
        }
        let aspect = if self.aspect_lock > 0.0 {
            self.aspect_lock
        } else if keep_shape {
            self.press_aspect
        } else {
            0.0
        };
        self.live = Some(if self.kind == Kind::Move { self.moved(point) } else { self.resized(point, aspect) });
    }

    /// The button went up: the crop made, as fractions of the frame, or None when nothing was dragged (a click
    /// doesn't collapse the crop to a dot).
    pub fn release(&mut self, point: Pt) -> Option<CropRect> {
        let (live, dragging) = (self.live, self.is_dragging());
        self.cancel();
        let moved = (point.x - self.press_point.x).abs() + (point.y - self.press_point.y).abs() > 1.0;
        if dragging && moved { live.map(|r| preview_geometry::to_crop(self.frame, r)) } else { None }
    }

    /// The pointer capture was lost: the drag ends without a crop.
    pub fn cancel(&mut self) {
        self.kind = Kind::None;
        self.live = None;
    }

    fn moved(&self, point: Pt) -> R {
        let r = self.press_rect;
        let x = within(r.x + point.x - self.press_point.x, self.frame.x, self.frame.right() - r.width);
        let y = within(r.y + point.y - self.press_point.y, self.frame.y, self.frame.bottom() - r.height);
        R::new(x, y, r.width, r.height)
    }

    /// The pressed rectangle with the dragged edges moved to `point` and the others held, kept inside the frame and
    /// at least the minimum size; with an `aspect`, of that shape.
    fn resized(&self, point: Pt, aspect: f64) -> R {
        let frame = self.frame;
        let p = clamp_point(point, frame);
        let r = self.press_rect;
        let min_w = CropRect::MIN_SIZE * frame.width;
        let min_h = CropRect::MIN_SIZE * frame.height;
        let horizontal = self.edges & (LEFT | RIGHT) != 0;
        let vertical = self.edges & (TOP | BOTTOM) != 0;
        // The fixed side on each dragged axis, and which way the dragged side lies from it.
        let anchor_x = if self.edges & LEFT != 0 { r.right() } else { r.x };
        let anchor_y = if self.edges & TOP != 0 { r.bottom() } else { r.y };
        let dir_x: i32 = if horizontal { if p.x >= anchor_x { 1 } else { -1 } } else { 0 };
        let dir_y: i32 = if vertical { if p.y >= anchor_y { 1 } else { -1 } } else { 0 };
        let mut width = if horizontal { (p.x - anchor_x).abs() } else { r.width };
        let mut height = if vertical { (p.y - anchor_y).abs() } else { r.height };
        // Room on each axis: from the anchor to the frame edge it grows towards, or around the centre it keeps.
        let c = r.center();
        let room_x = match dir_x {
            1 => frame.right() - anchor_x,
            -1 => anchor_x - frame.x,
            _ => 2.0 * (c.x - frame.x).min(frame.right() - c.x),
        };
        let room_y = match dir_y {
            1 => frame.bottom() - anchor_y,
            -1 => anchor_y - frame.y,
            _ => 2.0 * (c.y - frame.y).min(frame.bottom() - c.y),
        };
        if aspect > 0.0 {
            // Follow whichever dragged side went furthest; an edge drag sizes the other axis from it.
            if horizontal && vertical {
                if width < height * aspect {
                    width = height * aspect;
                } else {
                    height = width / aspect;
                }
            } else if horizontal {
                height = width / aspect;
            } else {
                width = height * aspect;
            }
            let fit = 1f64.min((room_x / width.max(1e-9)).min(room_y / height.max(1e-9)));
            width *= fit;
            height *= fit;
            let grow = 1f64.max((min_w / width.max(1e-9)).max(min_h / height.max(1e-9)));
            width *= grow;
            height *= grow;
        }
        width = within(width, min_w.min(room_x), room_x.max(min_w));
        height = within(height, min_h.min(room_y), room_y.max(min_h));
        let x = match dir_x {
            1 => anchor_x,
            -1 => anchor_x - width,
            _ => c.x - width / 2.0,
        };
        let y = match dir_y {
            1 => anchor_y,
            -1 => anchor_y - height,
            _ => c.y - height / 2.0,
        };
        R::new(within(x, frame.x, frame.right() - width), within(y, frame.y, frame.bottom() - height), width, height)
    }
}

/// Which edges a point grabs: near a corner both, near an edge one, else none.
pub fn hit_edges(rect: R, p: Pt) -> u8 {
    let in_x = p.x >= rect.x - GRAB && p.x <= rect.right() + GRAB;
    let in_y = p.y >= rect.y - GRAB && p.y <= rect.bottom() + GRAB;
    if !in_x || !in_y {
        return 0;
    }
    // A small rectangle still leaves room in the middle to move it.
    let grab_x = GRAB.min(rect.width / 4.0);
    let grab_y = GRAB.min(rect.height / 4.0);
    let mut edges = 0;
    if (p.x - rect.x).abs() <= grab_x || (p.x < rect.x && p.x >= rect.x - GRAB) {
        edges |= LEFT;
    } else if (p.x - rect.right()).abs() <= grab_x || (p.x > rect.right() && p.x <= rect.right() + GRAB) {
        edges |= RIGHT;
    }
    if (p.y - rect.y).abs() <= grab_y || (p.y < rect.y && p.y >= rect.y - GRAB) {
        edges |= TOP;
    } else if (p.y - rect.bottom()).abs() <= grab_y || (p.y > rect.bottom() && p.y <= rect.bottom() + GRAB) {
        edges |= BOTTOM;
    }
    edges
}
