//! A plain double-precision rectangle and point, so the gesture and geometry logic doesn't depend on egui.

#[derive(Clone, Copy, PartialEq, Debug, Default)]
pub struct Pt {
    pub x: f64,
    pub y: f64,
}

impl Pt {
    pub const fn new(x: f64, y: f64) -> Pt {
        Pt { x, y }
    }
}

#[derive(Clone, Copy, PartialEq, Debug, Default)]
pub struct R {
    pub x: f64,
    pub y: f64,
    pub width: f64,
    pub height: f64,
}

impl R {
    pub const fn new(x: f64, y: f64, width: f64, height: f64) -> R {
        R { x, y, width, height }
    }

    pub fn right(&self) -> f64 {
        self.x + self.width
    }

    pub fn bottom(&self) -> f64 {
        self.y + self.height
    }

    pub fn center(&self) -> Pt {
        Pt::new(self.x + self.width / 2.0, self.y + self.height / 2.0)
    }

    /// Avalonia's Rect.Contains: edges included.
    pub fn contains(&self, p: Pt) -> bool {
        p.x >= self.x && p.x <= self.right() && p.y >= self.y && p.y <= self.bottom()
    }

    /// The overlap of two rectangles (empty at the origin when they don't overlap).
    pub fn intersect(&self, o: &R) -> R {
        let x0 = self.x.max(o.x);
        let y0 = self.y.max(o.y);
        let x1 = self.right().min(o.right());
        let y1 = self.bottom().min(o.bottom());
        if x1 < x0 || y1 < y0 { R::default() } else { R::new(x0, y0, x1 - x0, y1 - y0) }
    }

    pub fn to_egui(self) -> egui::Rect {
        egui::Rect::from_min_size(egui::pos2(self.x as f32, self.y as f32), egui::vec2(self.width as f32, self.height as f32))
    }

    pub fn from_egui(r: egui::Rect) -> R {
        R::new(r.min.x as f64, r.min.y as f64, r.width() as f64, r.height() as f64)
    }
}
