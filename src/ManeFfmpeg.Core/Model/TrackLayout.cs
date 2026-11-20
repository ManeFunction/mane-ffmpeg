namespace ManeFfmpeg.Core.Model;

/// <summary>
/// <see cref="Length"/> of the A2 track: <see cref="Clip"/>'s file read from <see cref="ReadFrom"/>, or silence when
/// <see cref="Clip"/> is null.
/// </summary>
public readonly record struct TrackSpan(TimeSpan Length, TrackClip? Clip, TimeSpan ReadFrom)
{
    public bool IsSilence => Clip is null;
}

/// <summary>
/// What the A2 track plays over a stretch of the timeline. The preview's EDL and every export render this, so they
/// agree on where each clip's sound falls.
/// </summary>
public static class TrackLayout
{
    /// <summary>
    /// The track over timeline time <c>[from, to)</c> as file reads and silences in order; <paramref name="from"/> may be
    /// negative (silence before the timeline starts). <paramref name="audioEnd"/> says where a clip's file runs out
    /// (null when unknown), so a file that got shorter plays silence rather than shifting what follows.
    /// </summary>
    public static IEnumerable<TrackSpan> Spans(IReadOnlyList<TrackClip> track, TimeSpan from, TimeSpan to, Func<TrackClip, TimeSpan?> audioEnd)
    {
        var at = from;
        foreach (var clip in track)
        {
            if (at >= to)
                yield break;
            if (clip.End <= at)
                continue;
            if (clip.Start >= to)
                break;
            if (clip.Start > at)
            {
                yield return new TrackSpan(clip.Start - at, null, TimeSpan.Zero);
                at = clip.Start;
            }
            var end = clip.End < to ? clip.End : to;
            var readFrom = clip.In + (at - clip.Start);
            var length = end - at;
            var readable = audioEnd(clip) is { } fileEnd && readFrom + length > fileEnd
                ? (fileEnd > readFrom ? fileEnd - readFrom : TimeSpan.Zero)
                : length;
            if (readable > TimeSpan.Zero)
                yield return new TrackSpan(readable, clip, readFrom);
            if (length - readable > TimeSpan.Zero)
                yield return new TrackSpan(length - readable, null, TimeSpan.Zero);
            at = end;
        }
        if (at < to)
            yield return new TrackSpan(to - at, null, TimeSpan.Zero);
    }
}
