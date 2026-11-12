namespace ManeFfmpeg.Core.Model;

/// <summary>
/// The picture an export is made into, which a <see cref="CropRect"/> is a part of. It is the first clip's frame as
/// filters see it (upright, pixels not squared), unless clips differ in size or pixel shape: then every clip is scaled
/// and padded to the first one's display size and that is the frame. The preview asks it too, so the size it shows
/// for a crop is the size exported.
/// </summary>
/// <param name="Width">Width in pixels, as the crop filter sees the frame.</param>
/// <param name="Height">Height in pixels, as the crop filter sees the frame.</param>
/// <param name="Aspect">Width over height as shown.</param>
/// <param name="Normalized">Clips are scaled and padded to the first one's display size.</param>
public sealed record OutputFrame(int Width, int Height, double Aspect, bool Normalized)
{
    /// <summary>The frame of a timeline whose clips' videos are <paramref name="clips"/>, in timeline order.</summary>
    public static OutputFrame Of(IReadOnlyList<VideoStreamInfo> clips)
    {
        if (clips.Count == 0)
            throw new ArgumentException("A frame needs at least one clip.", nameof(clips));
        var first = clips[0];
        var normalized = SizeDiffers(clips);
        var (width, height) = normalized ? (first.DisplayWidth, first.DisplayHeight) : first.UprightSize;
        return new OutputFrame(width, height, first.DisplayAspect, normalized);
    }

    /// <summary>Some clip is shown at another size or pixel shape than the first: all are scaled and padded to it.</summary>
    public static bool SizeDiffers(IReadOnlyList<VideoStreamInfo> clips)
    {
        var r = clips[0];
        return clips.Any(v => v.DisplayWidth != r.DisplayWidth || v.DisplayHeight != r.DisplayHeight
                              || !v.SampleAspectRatio.Equivalent(r.SampleAspectRatio));
    }

    /// <summary>What <paramref name="crop"/> keeps of this frame, in even pixels.</summary>
    public PixelCrop Pixels(CropRect crop) => crop.ToPixels(Width, Height);

    /// <summary>
    /// The filters that fit a clip into a normalized frame, the way <see cref="PictureArea"/> places it: scaled as big
    /// as it fits, centered and padded, with square pixels. Empty when the frame isn't normalized.
    /// </summary>
    public string FitFilter => Normalized
        ? $"scale={Width}:{Height}:force_original_aspect_ratio=decrease,pad={Width}:{Height}:(ow-iw)/2:(oh-ih)/2,setsar=1"
        : "";

    /// <summary>
    /// Where a picture of display aspect <paramref name="pictureAspect"/> lies in a frame of display aspect
    /// <paramref name="frameAspect"/>, as fractions of the frame: as big as it fits, centered (what
    /// <see cref="FitFilter"/> does to a clip). The whole frame when either aspect is unknown (0).
    /// </summary>
    public static CropRect PictureArea(double pictureAspect, double frameAspect)
    {
        if (pictureAspect <= 0 || frameAspect <= 0 || Math.Abs(pictureAspect - frameAspect) < 1e-6)
            return CropRect.Full;
        var (width, height) = frameAspect > pictureAspect ? (pictureAspect / frameAspect, 1.0) : (1.0, frameAspect / pictureAspect);
        return new CropRect((1 - width) / 2, (1 - height) / 2, width, height);
    }
}
