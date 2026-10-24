using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.Core.Editing;

/// <summary>
/// Where cuts may land in one source, and how long it is. Every cut lands on a whole frame. With
/// <see cref="Keyframes"/> (snapping for stream copy, which can only start a clip on a keyframe), in points
/// land on the nearest keyframe instead.
/// </summary>
public sealed record CutGrid(TimeSpan Duration, Rational FrameRate = default, TimeSpan FramePhase = default,
    IReadOnlyList<TimeSpan>? Keyframes = null)
{
    public static CutGrid For(MediaInfo media, IReadOnlyList<TimeSpan>? keyframes = null) =>
        new(media.Duration, media.Video?.FrameRate ?? default, media.Video?.StartOffset ?? TimeSpan.Zero, keyframes);

    /// <summary>For a source that hasn't been analyzed: nothing to snap to, and a clip can't grow past its out point.</summary>
    public static CutGrid Unknown(Clip clip) => new(clip.Out);

    public TimeSpan SnapInPoint(TimeSpan time) =>
        Keyframes is { Count: > 0 } keyframes ? keyframes.MinBy(k => Math.Abs((k - time).Ticks)) : SnapOutPoint(time);

    public TimeSpan SnapOutPoint(TimeSpan time) => TimeFormat.SnapToFrame(time, FrameRate, FramePhase);
}
