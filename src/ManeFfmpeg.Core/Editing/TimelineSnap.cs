using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.Core.Editing;

/// <summary>
/// Where the playhead snaps while it is dragged with Shift held: clip edges, and the edges of each clip's audio
/// inside it (where shifted or short audio starts or runs out, and where looped audio starts again). These are
/// the marks the timeline draws (hatching and loop seams), worked out through the same placement math.
/// </summary>
public static class TimelineSnap
{
    /// <summary>The snap point nearest to <paramref name="time"/>, or null when none is within <paramref name="radius"/>.</summary>
    /// <param name="audioEnd">Where the clip's heard audio stream ends; null when it has none or isn't analyzed yet.</param>
    public static TimeSpan? Nearest(Timeline timeline, TimeSpan time, TimeSpan radius, Func<Clip, TimeSpan?> audioEnd)
    {
        TimeSpan? best = null;
        void Consider(TimeSpan point)
        {
            var distance = (point - time).Duration();
            if (distance <= radius && (best is not { } b || distance < (b - time).Duration()))
                best = point;
        }

        var bounds = timeline.Bounds;
        for (var i = 0; i < timeline.Clips.Count; i++)
        {
            var start = bounds[i];
            var end = bounds[i + 1];
            if (time < start - radius || time > end + radius)
                continue;
            Consider(start);
            Consider(end);
            foreach (var point in AudioEdges(timeline.Clips[i], time - start, audioEnd))
                Consider(start + point);
        }
        return best;
    }

    /// <summary>The clip's audio edges strictly inside it, relative to its start: those near <paramref name="local"/> for looped audio.</summary>
    private static IEnumerable<TimeSpan> AudioEdges(Clip clip, TimeSpan local, Func<Clip, TimeSpan?> audioEnd)
    {
        if (audioEnd(clip) is not { } end)
            yield break;
        var duration = clip.Duration;
        if (clip.LoopsAudio && AudioLoop.CanLoop(end))
        {
            // The file starts again at Period − Phase into the clip, then every period: only the two seams around
            // the pointer can be the nearest.
            var loop = AudioLoop.For(clip.In, duration, clip.AudioSlip, end);
            var first = loop.Period - loop.Phase;
            var k = Math.Max(0, (long)Math.Floor((local - first) / loop.Period));
            for (var seam = first + loop.Period * k; seam <= first + loop.Period * (k + 1); seam += loop.Period)
                if (seam > TimeSpan.Zero && seam < duration)
                    yield return seam;
            yield break;
        }
        var placement = clip.AudioPlacement(end);
        if (placement.Lead > TimeSpan.Zero && placement.Lead < duration)
            yield return placement.Lead;
        if (placement.ReadLength > TimeSpan.Zero && placement.Tail > TimeSpan.Zero)
            yield return placement.Lead + placement.ReadLength;
    }
}
