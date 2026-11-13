//! Where things sit in the preview. The video view and the crop overlay share it, so the overlay's rectangle lies
//! exactly on the picture. Aspects are width over height as shown; 0 means unknown.

use super::rect::R;
use mane_core::model::{CropRect, OutputFrame};

/// The largest rectangle of `aspect` that fits `area`, centered.
pub fn fit(aspect: f64, area: R) -> R {
    if aspect <= 0.0 || area.width <= 0.0 || area.height <= 0.0 {
        return area;
    }
    let width = area.width.min(area.height * aspect);
    let height = width / aspect;
    R::new(area.x + (area.width - width) / 2.0, area.y + (area.height - height) / 2.0, width, height)
}

/// The output frame around a picture: the picture lies in it where an export puts a clip of that shape.
pub fn frame_around(picture: R, picture_aspect: f64, frame_aspect: f64) -> R {
    let area = OutputFrame::picture_area(picture_aspect, frame_aspect);
    let (w, h) = (picture.width / area.width, picture.height / area.height);
    R::new(picture.x - area.x * w, picture.y - area.y * h, w, h)
}

/// A crop's rectangle inside `frame`.
pub fn inside(frame: R, crop: &CropRect) -> R {
    R::new(frame.x + crop.x * frame.width, frame.y + crop.y * frame.height, crop.width * frame.width, crop.height * frame.height)
}

/// A rectangle inside `frame` as fractions of it.
pub fn to_crop(frame: R, rect: R) -> CropRect {
    CropRect::new((rect.x - frame.x) / frame.width, (rect.y - frame.y) / frame.height, rect.width / frame.width, rect.height / frame.height)
}

/// What to draw of a rendered frame to show `crop` of the output frame in `bounds`. mpv draws the picture fitted into
/// the whole bitmap, so the output frame is worked out around it; parts of the frame outside the bitmap (padding)
/// stay black, so the source is clipped to the bitmap and the destination by the same amount.
pub fn placement(bitmap_w: f64, bitmap_h: f64, bounds: R, picture_aspect: f64, frame_aspect: f64, crop: &CropRect) -> (R, R) {
    let bitmap = R::new(0.0, 0.0, bitmap_w, bitmap_h);
    if picture_aspect <= 0.0 {
        return (bitmap, bounds);
    }
    let (source, destination) = unclipped(bitmap, bounds, picture_aspect, frame_aspect, crop);
    if source.width <= 0.0 || source.height <= 0.0 {
        return (R::default(), R::default());
    }
    let clipped = source.intersect(&bitmap);
    if clipped.width <= 0.0 || clipped.height <= 0.0 {
        return (R::default(), R::default());
    }
    let (sx, sy) = (destination.width / source.width, destination.height / source.height);
    let shown = R::new(
        destination.x + (clipped.x - source.x) * sx,
        destination.y + (clipped.y - source.y) * sy,
        clipped.width * sx,
        clipped.height * sy,
    );
    (clipped, shown)
}

/// How much bigger than the view mpv should render so the cropped part comes out at about the size it is shown (at
/// least 1).
pub fn render_scale(bounds: R, picture_aspect: f64, frame_aspect: f64, crop: Option<&CropRect>) -> f64 {
    let Some(crop) = crop else { return 1.0 };
    if picture_aspect <= 0.0 || bounds.width <= 0.0 || bounds.height <= 0.0 {
        return 1.0;
    }
    let (cropped, shown) = unclipped(bounds, bounds, picture_aspect, frame_aspect, crop);
    if cropped.width > 0.0 { (shown.width / cropped.width).max(1.0) } else { 1.0 }
}

/// The crop's part of the frame around a picture fitted into `area` (frame parts outside it included), and where
/// that part is shown, as big as fits, in `bounds`.
fn unclipped(area: R, bounds: R, picture_aspect: f64, frame_aspect: f64, crop: &CropRect) -> (R, R) {
    let frame_aspect = if frame_aspect > 0.0 { frame_aspect } else { picture_aspect };
    let frame = frame_around(fit(picture_aspect, area), picture_aspect, frame_aspect);
    (inside(frame, crop), fit(crop.aspect_in(frame_aspect), bounds))
}
