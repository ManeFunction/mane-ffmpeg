using ManeFfmpeg.UI.ViewModels;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.UI.Controls;

/// <summary>
/// Where things are on the timeline control, in pixels: the track header, the ruler, V1, A1 and A2, and the
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
    public const double TrackTop = AudioTop + AudioHeight + 6;
    public const double TrackHeight = AudioHeight;
    public const double Height = TrackTop + TrackHeight + TrackMargin;

    /// <summary>How close to a clip edge a press grabs it for trimming.</summary>
    public const double EdgeGrab = 6;

    public static double X(double zoom, double scroll, TimeSpan time) => Header + (time.TotalSeconds - scroll) * zoom;

    public static TimeSpan Time(double zoom, double scroll, double x) =>
        TimeSpan.FromSeconds(scroll + (x - Header) / Math.Max(zoom, 1e-9));

    /// <summary>
    /// The scroll that keeps a playing playhead in view: unchanged while it is on screen, paged so it sits near the
    /// left edge once it leaves. Paging forward stops where the timeline's end reaches the right edge, so the view
    /// never moves past the clips into empty space; with the end already on screen it does not page at all.
    /// </summary>
    public static double FollowScroll(double scroll, double visibleSeconds, TimeSpan playhead, TimeSpan duration)
    {
        var t = playhead.TotalSeconds;
        if (t < scroll)
            return Math.Max(0, t - visibleSeconds * 0.1);
        if (t <= scroll + visibleSeconds * 0.95)
            return scroll;
        var lastPage = Math.Max(0, duration.TotalSeconds - visibleSeconds);
        return scroll >= lastPage ? scroll : Math.Min(t - visibleSeconds * 0.1, lastPage);
    }

    /// <summary>
    /// The zoom (pixels per second) and scroll (seconds at the left edge) that show <paramref name="start"/> to
    /// <paramref name="end"/> across a track area <paramref name="viewportWidth"/> pixels wide, with
    /// <see cref="FitPadding"/> pixels to spare split around it. Centered when the zoom limits keep it from filling the
    /// width; never scrolled before the timeline's start.
    /// </summary>
    public static (double Zoom, double Scroll) FitRange(double viewportWidth, TimeSpan start, TimeSpan end, double minZoom, double maxZoom)
    {
        var seconds = Math.Max((end - start).TotalSeconds, 1e-3);
        var zoom = Math.Clamp((viewportWidth - FitPadding) / seconds, minZoom, maxZoom);
        var center = (start.TotalSeconds + end.TotalSeconds) / 2;
        return (zoom, Math.Max(0, center - viewportWidth / zoom / 2));
    }

    /// <summary>Pixels left free around what a fit shows.</summary>
    public const double FitPadding = 24;

    public static double RulerCenter => RulerHeight / 2;

    public static double VideoCenter => VideoTop + VideoHeight / 2;

    public static double AudioCenter => AudioTop + AudioHeight / 2;

    public static bool InVideoRow(double y) => y >= VideoTop && y < VideoTop + VideoHeight;

    public static bool InAudioRow(double y) => y >= AudioTop && y < AudioTop + AudioHeight;

    public static double TrackCenter => TrackTop + TrackHeight / 2;

    /// <summary>On A2 (its whole row, clips or not).</summary>
    public static bool InTrackRow(double y) => y >= TrackTop && y < TrackTop + TrackHeight;

    public static TimelineHit HitTest(ProjectState state, double zoom, double scroll, double x, double y)
    {
        var time = Time(zoom, scroll, x);
        if (y < VideoTop)
            return new TimelineHit(TimelineZone.Ruler, time);
        if (InTrackRow(y))
            return HitTrack(state.AudioTrack, zoom, scroll, x, time);
        var timeline = state.Timeline;
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

    private static TimelineHit HitTrack(IReadOnlyList<TrackClip> track, double zoom, double scroll, double x, TimeSpan time)
    {
        foreach (var clip in track)
        {
            var x0 = X(zoom, scroll, clip.Start);
            var x1 = X(zoom, scroll, clip.End);
            if (x < x0 || x > x1)
                continue;
            var zone = x1 - x0 <= EdgeGrab * 3 ? TimelineZone.TrackClip
                : x - x0 <= EdgeGrab ? TimelineZone.TrackTrimIn
                : x1 - x <= EdgeGrab ? TimelineZone.TrackTrimOut
                : TimelineZone.TrackClip;
            return new TimelineHit(zone, time, clip.Id);
        }
        return new TimelineHit(TimelineZone.Empty, time);
    }
}
