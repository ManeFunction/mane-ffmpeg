using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.Core.Editing;

/// <summary>
/// Where the playhead snaps while it is dragged with Shift held: clip edges, and the edges of each clip's audio
/// inside it (where shifted or short audio starts or runs out, and where looped audio starts again). These are
/// the marks the timeline draws (hatching and loop seams), the edges of the clip's <see cref="AudioLayout"/>.
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
            var clip = timeline.Clips[i];
            if (audioEnd(clip) is not { } heardEnd)
                continue;
            var local = time - start;
            foreach (var point in AudioLayout.For(clip, clip.In, clip.Duration, heardEnd).Edges(local - radius, local + radius))
                Consider(start + point);
        }
        return best;
    }
}
