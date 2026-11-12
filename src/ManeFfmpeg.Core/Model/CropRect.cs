namespace ManeFfmpeg.Core.Model;

/// <summary>A crop rectangle in pixels of a frame, with even position and size so 4:2:0 video encodes.</summary>
public readonly record struct PixelCrop(int X, int Y, int Width, int Height)
{
    /// <summary>The ffmpeg filter that cuts it out.</summary>
    public string Filter => $"crop={Width}:{Height}:{X}:{Y}";

    /// <summary>Its size as the editor shows it, e.g. "1280 × 720".</summary>
    public string SizeText => $"{Width} × {Height}";
}

/// <summary>
/// The part of the output frame that is kept, as fractions of its width and height (0–1), so it stays put when the
/// frame size changes. Exports convert it to even pixels with <see cref="ToPixels"/>.
/// </summary>
public sealed record CropRect(double X, double Y, double Width, double Height)
{
    /// <summary>The whole frame.</summary>
    public static readonly CropRect Full = new(0, 0, 1, 1);

    /// <summary>The smallest side, as a fraction of the frame, a crop may have.</summary>
    public const double MinSize = 0.02;

    public double Right => X + Width;

    public double Bottom => Y + Height;

    /// <summary>Keeps (almost) the whole frame: nothing is cut off.</summary>
    public bool IsFull => X <= 1e-4 && Y <= 1e-4 && Right >= 1 - 1e-4 && Bottom >= 1 - 1e-4;

    /// <summary>
    /// The rectangle moved and shrunk to lie inside the frame, at least <see cref="MinSize"/> on each side; null when its
    /// numbers can't be used (not finite).
    /// </summary>
    public CropRect? Clamped()
    {
        if (!double.IsFinite(X) || !double.IsFinite(Y) || !double.IsFinite(Width) || !double.IsFinite(Height))
            return null;
        var width = Math.Clamp(Width, MinSize, 1);
        var height = Math.Clamp(Height, MinSize, 1);
        return new CropRect(Math.Clamp(X, 0, 1 - width), Math.Clamp(Y, 0, 1 - height), width, height);
    }

    /// <summary>
    /// The crop in pixels of a <paramref name="frameWidth"/>×<paramref name="frameHeight"/> frame: every number even
    /// (rounded to the nearest), at least 2, and inside the frame.
    /// </summary>
    public PixelCrop ToPixels(int frameWidth, int frameHeight)
    {
        var (x, width) = Axis(X, Width, frameWidth);
        var (y, height) = Axis(Y, Height, frameHeight);
        return new PixelCrop(x, y, width, height);
    }

    private static (int Start, int Length) Axis(double start, double length, int frame)
    {
        var even = Math.Max(2, frame / 2 * 2);
        var from = Even(start * frame);
        var to = Even((start + length) * frame);
        from = Math.Clamp(from, 0, even - 2);
        to = Math.Clamp(to, from + 2, even);
        return (from, to - from);
    }

    private static int Even(double pixels) => (int)Math.Round(pixels / 2, MidpointRounding.AwayFromZero) * 2;

    /// <summary>Width over height of this crop as shown, in a frame of display aspect <paramref name="frameAspect"/>.</summary>
    public double AspectIn(double frameAspect) => Width / Height * frameAspect;

    /// <summary>
    /// This crop reshaped to display aspect <paramref name="aspect"/> (width / height as shown) in a frame of display
    /// aspect <paramref name="frameAspect"/>: about as much of the picture, around the same center, made smaller
    /// where it would not fit the frame. The whole frame becomes the largest crop of that shape.
    /// </summary>
    public CropRect WithAspect(double aspect, double frameAspect)
    {
        if (aspect <= 0 || frameAspect <= 0)
            return this;
        // As fractions of the frame, width / height = aspect / frameAspect; keep the area.
        var ratio = aspect / frameAspect;
        var height = Math.Sqrt(Width * Height / ratio);
        var width = height * ratio;
        var shrink = Math.Min(1, Math.Min(1 / width, 1 / height));
        width *= shrink;
        height *= shrink;
        var center = (X: X + Width / 2, Y: Y + Height / 2);
        // Max after Min: a side of 1 plus rounding still starts at 0.
        return new CropRect(
            Math.Max(0, Math.Min(center.X - width / 2, 1 - width)), Math.Max(0, Math.Min(center.Y - height / 2, 1 - height)), width, height);
    }
}
