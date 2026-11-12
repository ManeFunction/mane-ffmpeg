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
}
