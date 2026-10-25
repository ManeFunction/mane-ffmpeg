using ManeFfmpeg.App.ViewModels;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.App.Controls;

/// <summary>
/// Where things are on the timeline control, in pixels: the track header, the ruler, V1 and A1, and the
/// mapping between x and timeline time at a zoom (pixels per second) and scroll (seconds at the left edge).
/// </summary>
public static class TimelineLayout
{
    public const double Header = 44;
    public const double RulerHeight = 26;
    // Gap between the ruler and V1, repeated below A1.
    public const double TrackMargin = 8;
    public const double VideoTop = RulerHeight + TrackMargin;
    public const double VideoHeight = 62;
    public const double AudioTop = VideoTop + VideoHeight + 6;
    public const double AudioHeight = VideoHeight / 2;
    public const double Height = AudioTop + AudioHeight + TrackMargin;

    /// <summary>How close to a clip edge a press grabs it for trimming.</summary>
    public const double EdgeGrab = 6;

    public static double X(double zoom, double scroll, TimeSpan time) => Header + (time.TotalSeconds - scroll) * zoom;

    public static TimeSpan Time(double zoom, double scroll, double x) =>
        TimeSpan.FromSeconds(scroll + (x - Header) / Math.Max(zoom, 1e-9));

    public static double RulerCenter => RulerHeight / 2;

    public static double VideoCenter => VideoTop + VideoHeight / 2;

    public static double AudioCenter => AudioTop + AudioHeight / 2;

    public static bool InVideoRow(double y) => y >= VideoTop && y < VideoTop + VideoHeight;

    public static bool InAudioRow(double y) => y >= AudioTop && y < AudioTop + AudioHeight;

    public static TimelineHit HitTest(Timeline timeline, double zoom, double scroll, double x, double y)
    {
        var time = Time(zoom, scroll, x);
        if (y < VideoTop)
            return new TimelineHit(TimelineZone.Ruler, time);
        if (!InVideoRow(y) && !InAudioRow(y))
            return new TimelineHit(TimelineZone.Empty, time);
        var start = TimeSpan.Zero;
        foreach (var clip in timeline.Clips)
        {
            var x0 = X(zoom, scroll, start);
            var x1 = X(zoom, scroll, start + clip.Duration);
            start += clip.Duration;
            if (x < x0 || x > x1)
                continue;
            var zone = InAudioRow(y) ? TimelineZone.Audio
                : x1 - x0 <= EdgeGrab * 3 ? TimelineZone.Clip
                : x - x0 <= EdgeGrab ? TimelineZone.TrimIn
                : x1 - x <= EdgeGrab ? TimelineZone.TrimOut
                : TimelineZone.Clip;
            return new TimelineHit(zone, time, clip.Id);
        }
        return new TimelineHit(TimelineZone.Empty, time);
    }
}
