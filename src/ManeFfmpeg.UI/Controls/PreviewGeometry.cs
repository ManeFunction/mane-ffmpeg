using Avalonia;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.UI.Controls;

/// <summary>
/// Where things sit in the preview. The video view and the crop overlay share it, so the overlay's rectangle lies
/// exactly on the picture. Aspects are width over height as shown; 0 means unknown.
/// </summary>
public static class PreviewGeometry
{
    /// <summary>The largest rectangle of <paramref name="aspect"/> that fits <paramref name="area"/>, centered.</summary>
    public static Rect Fit(double aspect, Rect area)
    {
        if (aspect <= 0 || area.Width <= 0 || area.Height <= 0)
            return area;
        var width = Math.Min(area.Width, area.Height * aspect);
        var height = width / aspect;
        return new Rect(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height);
    }

    /// <summary>
    /// The output frame around a picture: the picture lies in it where an export puts a clip of that shape
    /// (<see cref="OutputFrame.PictureArea"/>).
    /// </summary>
    public static Rect FrameAround(Rect picture, double pictureAspect, double frameAspect)
    {
        var area = OutputFrame.PictureArea(pictureAspect, frameAspect);
        var (width, height) = (picture.Width / area.Width, picture.Height / area.Height);
        return new Rect(picture.X - area.X * width, picture.Y - area.Y * height, width, height);
    }

    /// <summary>A crop's rectangle inside <paramref name="frame"/>.</summary>
    public static Rect Inside(Rect frame, CropRect crop) =>
        new(frame.X + crop.X * frame.Width, frame.Y + crop.Y * frame.Height, crop.Width * frame.Width, crop.Height * frame.Height);

    /// <summary>A rectangle inside <paramref name="frame"/> as fractions of it.</summary>
    public static CropRect ToCrop(Rect frame, Rect rect) =>
        new((rect.X - frame.X) / frame.Width, (rect.Y - frame.Y) / frame.Height, rect.Width / frame.Width, rect.Height / frame.Height);

    /// <summary>
    /// What to draw of a rendered frame to show <paramref name="crop"/> of the output frame in <paramref name="bounds"/>.
    /// mpv draws the picture fitted into the whole bitmap (<paramref name="bitmapSize"/>), so the output frame is
    /// worked out around it; parts of the frame outside the bitmap (padding) stay black, so the source is clipped to
    /// the bitmap and the destination by the same amount.
    /// </summary>
    public static (Rect Source, Rect Destination) Placement(Size bitmapSize, Rect bounds, double pictureAspect, double frameAspect, CropRect crop)
    {
        var bitmap = new Rect(bitmapSize);
        if (pictureAspect <= 0)
            return (bitmap, bounds);
        var (source, destination) = Unclipped(bitmap, bounds, pictureAspect, frameAspect, crop);
        if (source.Width <= 0 || source.Height <= 0)
            return (default, default);
        var clipped = source.Intersect(bitmap);
        if (clipped.Width <= 0 || clipped.Height <= 0)
            return (default, default);
        var (sx, sy) = (destination.Width / source.Width, destination.Height / source.Height);
        var shown = new Rect(
            destination.X + (clipped.X - source.X) * sx, destination.Y + (clipped.Y - source.Y) * sy,
            clipped.Width * sx, clipped.Height * sy);
        return (clipped, shown);
    }

    /// <summary>
    /// How much bigger than the control mpv should render so the cropped part comes out at about the size it is
    /// shown (at least 1: never smaller than the control).
    /// </summary>
    public static double RenderScale(Rect bounds, double pictureAspect, double frameAspect, CropRect? crop)
    {
        if (crop is null || pictureAspect <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
            return 1;
        var (cropped, shown) = Unclipped(bounds, bounds, pictureAspect, frameAspect, crop);
        return cropped.Width > 0 ? Math.Max(1, shown.Width / cropped.Width) : 1;
    }

    /// <summary>
    /// The crop's part of the frame around a picture fitted into <paramref name="area"/> (frame parts outside it
    /// included), and where that part is shown, as big as fits, in <paramref name="bounds"/>.
    /// </summary>
    private static (Rect Cropped, Rect Shown) Unclipped(Rect area, Rect bounds, double pictureAspect, double frameAspect, CropRect crop)
    {
        frameAspect = frameAspect > 0 ? frameAspect : pictureAspect;
        var frame = FrameAround(Fit(pictureAspect, area), pictureAspect, frameAspect);
        return (Inside(frame, crop), Fit(crop.AspectIn(frameAspect), bounds));
    }
}
