/// A crop rectangle in pixels of a frame, with even position and size so 4:2:0 video encodes.
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub struct PixelCrop {
    pub x: i32,
    pub y: i32,
    pub width: i32,
    pub height: i32,
}

impl PixelCrop {
    /// The ffmpeg filter that cuts it out.
    pub fn filter(&self) -> String {
        format!("crop={}:{}:{}:{}", self.width, self.height, self.x, self.y)
    }

    /// Its size as the editor shows it, e.g. "1280 × 720".
    pub fn size_text(&self) -> String {
        format!("{} × {}", self.width, self.height)
    }
}

/// The part of the output frame that is kept, as fractions of its width and height (0–1), so it stays put when the
/// frame size changes. Exports convert it to even pixels with [`CropRect::to_pixels`].
#[derive(Clone, Copy, PartialEq, Debug)]
pub struct CropRect {
    pub x: f64,
    pub y: f64,
    pub width: f64,
    pub height: f64,
}

impl CropRect {
    /// The whole frame.
    pub const FULL: CropRect = CropRect { x: 0.0, y: 0.0, width: 1.0, height: 1.0 };

    /// The smallest side, as a fraction of the frame, a crop may have.
    pub const MIN_SIZE: f64 = 0.02;

    pub const fn new(x: f64, y: f64, width: f64, height: f64) -> CropRect {
        CropRect { x, y, width, height }
    }

    pub fn right(&self) -> f64 {
        self.x + self.width
    }

    pub fn bottom(&self) -> f64 {
        self.y + self.height
    }

    /// Keeps (almost) the whole frame: nothing is cut off.
    pub fn is_full(&self) -> bool {
        self.x <= 1e-4 && self.y <= 1e-4 && self.right() >= 1.0 - 1e-4 && self.bottom() >= 1.0 - 1e-4
    }

    /// The rectangle moved and shrunk to lie inside the frame, at least `MIN_SIZE` on each side; None when its
    /// numbers can't be used (not finite).
    pub fn clamped(&self) -> Option<CropRect> {
        if !(self.x.is_finite() && self.y.is_finite() && self.width.is_finite() && self.height.is_finite()) {
            return None;
        }
        let width = self.width.clamp(Self::MIN_SIZE, 1.0);
        let height = self.height.clamp(Self::MIN_SIZE, 1.0);
        Some(CropRect::new(self.x.clamp(0.0, 1.0 - width), self.y.clamp(0.0, 1.0 - height), width, height))
    }

    /// The crop in pixels of a `frame_width`×`frame_height` frame: every number even (rounded to the nearest), at
    /// least 2, and inside the frame.
    pub fn to_pixels(&self, frame_width: i32, frame_height: i32) -> PixelCrop {
        let (x, width) = axis(self.x, self.width, frame_width);
        let (y, height) = axis(self.y, self.height, frame_height);
        PixelCrop { x, y, width, height }
    }

    /// Width over height of this crop as shown, in a frame of display aspect `frame_aspect`.
    pub fn aspect_in(&self, frame_aspect: f64) -> f64 {
        self.width / self.height * frame_aspect
    }

    /// This crop reshaped to display aspect `aspect` in a frame of display aspect `frame_aspect`: about as much of
    /// the picture, around the same center, made smaller where it would not fit the frame.
    pub fn with_aspect(&self, aspect: f64, frame_aspect: f64) -> CropRect {
        if aspect <= 0.0 || frame_aspect <= 0.0 {
            return *self;
        }
        let ratio = aspect / frame_aspect;
        let mut height = (self.width * self.height / ratio).sqrt();
        let mut width = height * ratio;
        let shrink = 1f64.min((1.0 / width).min(1.0 / height));
        width *= shrink;
        height *= shrink;
        let (cx, cy) = (self.x + self.width / 2.0, self.y + self.height / 2.0);
        // Max after min: a side of 1 plus rounding still starts at 0.
        CropRect::new(0f64.max((cx - width / 2.0).min(1.0 - width)), 0f64.max((cy - height / 2.0).min(1.0 - height)), width, height)
    }
}

fn axis(start: f64, length: f64, frame: i32) -> (i32, i32) {
    let even_frame = (frame / 2 * 2).max(2);
    let from = even(start * frame as f64).clamp(0, even_frame - 2);
    let to = even((start + length) * frame as f64).clamp(from + 2, even_frame);
    (from, to - from)
}

fn even(pixels: f64) -> i32 {
    (pixels / 2.0).round() as i32 * 2
}
