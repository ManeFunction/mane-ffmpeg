use super::crop::{CropRect, PixelCrop};
use super::media_info::VideoStreamInfo;

/// The picture an export is made into, which a [`CropRect`] is a part of. It is the first clip's frame as filters
/// see it (upright, pixels not squared), unless clips differ in size or pixel shape: then every clip is scaled and
/// padded to the first one's display size and that is the frame. The preview asks it too, so the size it shows for
/// a crop is the size exported.
#[derive(Clone, Copy, PartialEq, Debug)]
pub struct OutputFrame {
    /// Width in pixels, as the crop filter sees the frame.
    pub width: i32,
    pub height: i32,
    /// Width over height as shown.
    pub aspect: f64,
    /// Clips are scaled and padded to the first one's display size.
    pub normalized: bool,
}

impl OutputFrame {
    /// The frame of a timeline whose clips' videos are `clips`, in timeline order (at least one).
    pub fn of(clips: &[&VideoStreamInfo]) -> OutputFrame {
        let first = clips[0];
        let normalized = Self::size_differs(clips);
        let (width, height) = if normalized { (first.display_width(), first.display_height()) } else { first.upright_size() };
        OutputFrame { width, height, aspect: first.display_aspect(), normalized }
    }

    /// Some clip is shown at another size or pixel shape than the first: all are scaled and padded to it.
    pub fn size_differs(clips: &[&VideoStreamInfo]) -> bool {
        let r = clips[0];
        clips.iter().any(|v| {
            v.display_width() != r.display_width()
                || v.display_height() != r.display_height()
                || !v.sample_aspect_ratio.equivalent(r.sample_aspect_ratio)
        })
    }

    /// What `crop` keeps of this frame, in even pixels.
    pub fn pixels(&self, crop: &CropRect) -> PixelCrop {
        crop.to_pixels(self.width, self.height)
    }

    /// The filters that fit a clip into a normalized frame: scaled as big as it fits, centered and padded, with
    /// square pixels. Empty when the frame isn't normalized.
    pub fn fit_filter(&self) -> String {
        if self.normalized {
            let (w, h) = (self.width, self.height);
            format!("scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2,setsar=1")
        } else {
            String::new()
        }
    }

    /// Where a picture of display aspect `picture_aspect` lies in a frame of display aspect `frame_aspect`, as
    /// fractions of the frame: as big as it fits, centered. The whole frame when either aspect is unknown (0).
    pub fn picture_area(picture_aspect: f64, frame_aspect: f64) -> CropRect {
        if picture_aspect <= 0.0 || frame_aspect <= 0.0 || (picture_aspect - frame_aspect).abs() < 1e-6 {
            return CropRect::FULL;
        }
        let (width, height) =
            if frame_aspect > picture_aspect { (picture_aspect / frame_aspect, 1.0) } else { (1.0, frame_aspect / picture_aspect) };
        CropRect::new((1.0 - width) / 2.0, (1.0 - height) / 2.0, width, height)
    }
}
