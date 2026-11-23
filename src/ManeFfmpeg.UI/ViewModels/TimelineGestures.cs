using System.Collections.Immutable;
using ManeFfmpeg.Core.Editing;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.UI.ViewModels;

/// <summary>What a point on the timeline is over.</summary>
public enum TimelineZone
{
    /// <summary>The ruler (and the gap under it): the only place a click seeks and a drag scrubs.</summary>
    Ruler,

    /// <summary>Track rows, but no clip there (or between rows): clicks clear the selection and leave the playhead.</summary>
    Empty,

    /// <summary>A clip's left edge in V1.</summary>
    TrimIn,

    /// <summary>A clip's right edge in V1.</summary>
    TrimOut,

    /// <summary>A clip's body in V1: select, or drag to reorder.</summary>
    Clip,

    /// <summary>A clip in A1: select, or drag to slip its audio.</summary>
    Audio,

    /// <summary>A clip's body in A2: select, or drag to move it along the timeline.</summary>
    TrackClip,

    /// <summary>An A2 clip's left edge.</summary>
    TrackTrimIn,

    /// <summary>An A2 clip's right edge.</summary>
    TrackTrimOut,
}

/// <param name="Time">Timeline time under the point.</param>
/// <param name="ClipId">The clip under the point; null for <see cref="TimelineZone.Ruler"/> and <see cref="TimelineZone.Empty"/>.</param>
public readonly record struct TimelineHit(TimelineZone Zone, TimeSpan Time, string? ClipId = null);

/// <summary>What timeline gestures act on: the timeline view model, or a fake in tests.</summary>
public interface ITimelineGestureHost
{
    Timeline Timeline { get; }
    IReadOnlySet<string> Selection { get; }

    /// <summary>Pixels per second, to tell a click from a drag.</summary>
    double Zoom { get; }

    void Select(string clipId, bool toggle, bool range);
    void ClearSelection();
    void Seek(TimeSpan time, bool scrubbing);
    void EndScrub();
    void BeginGesture();
    void PreviewTimeline(Timeline timeline);
    void EndGesture();
    void MoveSelection(int insertIndex);
    CutGrid? GridOf(string sourceId);

    /// <summary>Live audio feedback while an offset is being dragged.</summary>
    void PreviewAudioDrag(TimeSpan delta);

    /// <summary>Where the clip's heard audio stream ends; null when it has none or isn't analyzed yet.</summary>
    TimeSpan? AudioEnd(Clip clip);

    /// <summary>The A2 track.</summary>
    ImmutableList<TrackClip> Track { get; }

    /// <summary>Shows a dragged A2 track (inside a gesture, like <see cref="PreviewTimeline"/>).</summary>
    void PreviewTrack(ImmutableList<TrackClip> track);

    /// <summary>Where an A2 clip's file runs out; null when it isn't analyzed yet.</summary>
    TimeSpan? FileEnd(TrackClip clip);
}

/// <summary>
/// The timeline's pointer gestures in timeline terms: a press on a zone, moves to a time, a release. Only the
/// ruler moves the playhead: a click there seeks, a drag scrubs. On the track rows a click selects (or clears the
/// selection) and a drag reorders clips, trims edges or slips audio, or moves and trims A2 clips, each drag one undo
/// step. Scrubbing with Shift held snaps the playhead to clip edges and audio edges; with Shift, a moved A2 clip or a
/// dragged A2 edge snaps to the same points. Slipping A1 audio with Shift held snaps where the audio starts or runs out
/// to the clip's start or end; with Alt (Option) held the slip goes at a tenth of the pointer's speed.
/// </summary>
public sealed class TimelineGestures(ITimelineGestureHost host)
{
    /// <summary>How far (pixels) the pointer must move before a press on a clip becomes a drag.</summary>
    public const double DragThreshold = 4;

    /// <summary>How close (pixels) a Shift-scrub (or a Shift-slipped audio edge) must come to an edge to snap to it.</summary>
    public const double SnapRadius = 8;

    private enum Drag { None, Idle, Scrub, PendingClip, Reorder, TrimIn, TrimOut, PendingSlip, Slip, PendingTrack, TrackMove, TrackTrimIn, TrackTrimOut }

    private Drag _drag;
    private TimeSpan _pressTime;
    private TimeSpan _lastTime;
    private Clip? _clip;
    private Timeline? _gestureStart;
    private TrackClip? _trackClip;
    private ImmutableList<TrackClip>? _trackStart;
    private bool _command;
    private bool _shift;
    private bool _alt;

    // A slip is _slipBase plus the pointer's travel since _slipFrom (a tenth of it with Alt). Pressing or releasing Alt
    // mid-slip moves both to where the audio is now, so the speed changes from there instead of from the press.
    private TimeSpan _slipBase;
    private TimeSpan _slipFrom;

    public bool IsActive => _drag != Drag.None;

    public bool IsReordering => _drag == Drag.Reorder;

    /// <summary>Where the dragged clips would land (an index into the clips), or -1 when not reordering.</summary>
    public int InsertIndex { get; private set; } = -1;

    /// <param name="command">Cmd/Ctrl was held: a click toggles the clip in the selection.</param>
    /// <param name="shift">Shift was held: a click extends the selection to the clip, a scrub snaps to edges.</param>
    public void Press(TimelineHit hit, bool command, bool shift)
    {
        Reset();
        _pressTime = _lastTime = _slipFrom = hit.Time;
        _command = command;
        _shift = shift;
        var index = hit.ClipId is { } id ? host.Timeline.IndexOf(id) : -1;
        if (hit.Zone == TimelineZone.Ruler)
        {
            _drag = Drag.Scrub;
            host.Seek(shift ? Snap(hit.Time) : hit.Time, scrubbing: true);
            return;
        }
        if (hit.Zone is TimelineZone.TrackClip or TimelineZone.TrackTrimIn or TimelineZone.TrackTrimOut
            && host.Track.Find(c => c.Id == hit.ClipId) is { } trackClip)
        {
            _trackClip = trackClip;
            _trackStart = host.Track;
            _drag = hit.Zone switch
            {
                TimelineZone.TrackTrimIn => Drag.TrackTrimIn,
                TimelineZone.TrackTrimOut => Drag.TrackTrimOut,
                _ => Drag.PendingTrack,
            };
            if (_drag != Drag.PendingTrack)
            {
                SelectPressedTrackClip();
                host.BeginGesture();
            }
            return;
        }
        if (index < 0)
        {
            // Holds the press until release, so the track rows don't start a scrub mid-drag.
            _drag = Drag.Idle;
            host.ClearSelection();
            return;
        }
        _clip = host.Timeline.Clips[index];
        _gestureStart = host.Timeline;
        _drag = hit.Zone switch
        {
            TimelineZone.TrimIn => Drag.TrimIn,
            TimelineZone.TrimOut => Drag.TrimOut,
            TimelineZone.Audio => Drag.PendingSlip,
            _ => Drag.PendingClip,
        };
        if (_drag is Drag.TrimIn or Drag.TrimOut)
            host.BeginGesture();
    }

    /// <param name="time">Timeline time now under the pointer.</param>
    /// <param name="shift">Shift is held: a scrub snaps to clip and audio edges, a slip snaps the audio's edges to the clip's sides.</param>
    /// <param name="alt">Alt (Option) is held: a slip goes at a tenth of the pointer's speed.</param>
    public void Move(TimeSpan time, bool shift, bool alt = false)
    {
        if (_drag == Drag.Slip)
            RebaseSlip(alt);
        _lastTime = time;
        _alt = alt;
        var delta = time - _pressTime;
        var dragged = Math.Abs(delta.TotalSeconds * host.Zoom) > DragThreshold;
        switch (_drag)
        {
            case Drag.Scrub:
                host.Seek(shift ? Snap(time) : time, scrubbing: true);
                break;
            case Drag.PendingClip when dragged:
                SelectPressedClip();
                _drag = Drag.Reorder;
                InsertIndex = NearestCut(time);
                break;
            case Drag.Reorder:
                InsertIndex = NearestCut(time);
                break;
            case Drag.TrimIn:
                host.PreviewTimeline(TimelineEditor.TrimIn(_gestureStart!, _clip!.Id, _clip.In + delta, host.GridOf));
                break;
            case Drag.TrimOut:
                host.PreviewTimeline(TimelineEditor.TrimOut(_gestureStart!, _clip!.Id, _clip.Out + delta, host.GridOf));
                break;
            case Drag.PendingSlip when dragged:
                SelectPressedClip();
                host.BeginGesture();
                _drag = Drag.Slip;
                SlipAudio(time, shift);
                break;
            case Drag.Slip:
                SlipAudio(time, shift);
                break;
            case Drag.PendingTrack when dragged:
                SelectPressedTrackClip();
                host.BeginGesture();
                _drag = Drag.TrackMove;
                MoveTrack(delta, shift);
                break;
            case Drag.TrackMove:
                MoveTrack(delta, shift);
                break;
            case Drag.TrackTrimIn:
            case Drag.TrackTrimOut:
                TrimTrack(delta, shift);
                break;
        }
    }

    /// <summary>Shift was pressed or released while the pointer stood still: a scrub snaps (or lets go) at once.</summary>
    public void ShiftChanged(bool shift)
    {
        if (_drag == Drag.Scrub)
            host.Seek(shift ? Snap(_lastTime) : _lastTime, scrubbing: true);
        if (_drag is Drag.TrackTrimIn or Drag.TrackTrimOut)
            TrimTrack(_lastTime - _pressTime, shift);
        if (_drag == Drag.TrackMove)
            MoveTrack(_lastTime - _pressTime, shift);
        if (_drag == Drag.Slip)
            SlipAudio(_lastTime, shift);
    }

    /// <summary>Alt was pressed or released mid-gesture: a slip goes on at the new speed from where the audio is now.</summary>
    public void AltChanged(bool alt)
    {
        if (_drag == Drag.Slip)
            RebaseSlip(alt);
        _alt = alt;
    }

    private void RebaseSlip(bool alt)
    {
        if (alt == _alt)
            return;
        _slipBase = RawSlip(_lastTime);
        _slipFrom = _lastTime;
    }

    /// <summary>The slip for the pointer at <paramref name="time"/>, before any snapping.</summary>
    private TimeSpan RawSlip(TimeSpan time) =>
        _slipBase + TimeSpan.FromMilliseconds(Math.Round((time - _slipFrom).TotalMilliseconds * (_alt ? 0.1 : 1)));

    /// <summary>
    /// Slips the selected clips' audio for the pointer at <paramref name="time"/> (see <see cref="RawSlip"/>), with
    /// <paramref name="shift"/> snapped so that where the pressed clip's audio starts
    /// or runs out (or loops) lands on the clip's start or end, when one is within reach.
    /// </summary>
    private void SlipAudio(TimeSpan time, bool shift)
    {
        var slip = RawSlip(time);
        if (shift)
            slip = MagnetSlip(slip);
        host.PreviewTimeline(TimelineEditor.NudgeAudioOffset(_gestureStart!, host.Selection, slip));
        host.PreviewAudioDrag(slip);
    }

    /// <summary>
    /// The slip nearest to <paramref name="slip"/> (within <see cref="SnapRadius"/> pixels) that puts an edge of the
    /// pressed clip's audio on one of the clip's sides, else <paramref name="slip"/> itself.
    /// </summary>
    private TimeSpan MagnetSlip(TimeSpan slip)
    {
        var clip = _clip!;
        var radius = TimeSpan.FromSeconds(SnapRadius / Math.Max(host.Zoom, 1e-9));
        // Where the file's start falls inside the clip (clip-local time) before this drag.
        var fileStart = clip.AudioSlip - clip.In;
        var fileEnd = host.AudioEnd(clip) is { Ticks: > 0 } end ? end : (TimeSpan?)null;
        TimeSpan? best = null;
        void Consider(TimeSpan candidate)
        {
            if ((candidate - slip).Duration() <= radius && (best is not { } b || (candidate - slip).Duration() < (b - slip).Duration()))
                best = candidate;
        }

        foreach (var side in (ReadOnlySpan<TimeSpan>)[TimeSpan.Zero, clip.Duration])
        {
            if (clip.LoopsAudio && fileEnd is { } length)
            {
                // Looped audio starts again every file length: bring the seam nearest to the side onto it.
                var k = Math.Round((side - fileStart - slip).Ticks / (double)length.Ticks);
                Consider(side - (fileStart + length * k));
                continue;
            }
            Consider(side - fileStart);
            if (fileEnd is { } e)
                Consider(side - (fileStart + e));
        }
        return best ?? slip;
    }

    /// <summary>
    /// Moves the dragged A2 clip edge by <paramref name="delta"/>; with Shift held the edge snaps to where the playhead
    /// would (clip edges and audio edges on V1/A1).
    /// </summary>
    private void TrimTrack(TimeSpan delta, bool shift)
    {
        var clip = _trackClip!;
        var trimIn = _drag == Drag.TrackTrimIn;
        var edge = (trimIn ? clip.Start : clip.End) + delta;
        if (shift)
            edge = Snap(edge);
        host.PreviewTrack(trimIn
            ? TrackEditor.TrimIn(_trackStart!, clip.Id, clip.In + (edge - clip.Start))
            : TrackEditor.TrimOut(_trackStart!, clip.Id, clip.Out + (edge - clip.End), host.FileEnd(clip)));
    }

    public void Release()
    {
        switch (_drag)
        {
            case Drag.Scrub:
                host.EndScrub();
                break;
            case Drag.PendingClip:
            case Drag.PendingSlip:
                host.Select(_clip!.Id, toggle: _command, range: _shift);
                break;
            case Drag.PendingTrack:
                host.Select(_trackClip!.Id, toggle: _command, range: _shift);
                break;
            case Drag.TrackMove:
            case Drag.TrackTrimIn:
            case Drag.TrackTrimOut:
                host.EndGesture();
                break;
            case Drag.Reorder:
                host.MoveSelection(InsertIndex);
                break;
            case Drag.TrimIn:
            case Drag.TrimOut:
                host.EndGesture();
                break;
            case Drag.Slip:
                host.EndGesture();
                host.PreviewAudioDrag(TimeSpan.Zero);
                break;
        }
        Reset();
    }

    /// <summary>The pointer was taken away mid-gesture: keeps what was dragged so far.</summary>
    public void Abort()
    {
        if (_drag is Drag.TrimIn or Drag.TrimOut or Drag.Slip or Drag.TrackMove or Drag.TrackTrimIn or Drag.TrackTrimOut)
            host.EndGesture();
        if (_drag == Drag.Slip)
            host.PreviewAudioDrag(TimeSpan.Zero);
        if (_drag == Drag.Scrub)
            host.EndScrub();
        Reset();
    }

    private void SelectPressedClip()
    {
        if (!host.Selection.Contains(_clip!.Id))
            host.Select(_clip.Id, toggle: false, range: false);
    }

    private void SelectPressedTrackClip()
    {
        if (!host.Selection.Contains(_trackClip!.Id))
            host.Select(_trackClip.Id, toggle: false, range: false);
    }

    /// <summary>
    /// Moves the dragged A2 clip by <paramref name="delta"/>. With Shift held, its start or its end (whichever is nearer
    /// to one) snaps where the playhead would: clip edges and audio edges on V1/A1. Without Shift it doesn't snap.
    /// </summary>
    private void MoveTrack(TimeSpan delta, bool shift)
    {
        var clip = _trackClip!;
        var start = clip.Start + delta;
        if (shift)
        {
            var byStart = Snap(start) - start;
            var byEnd = Snap(start + clip.Duration) - (start + clip.Duration);
            // Each is zero when nothing is in reach; take the one that snapped, the nearer when both did.
            start += byStart == TimeSpan.Zero ? byEnd
                : byEnd == TimeSpan.Zero ? byStart
                : byStart.Duration() <= byEnd.Duration() ? byStart : byEnd;
        }
        host.PreviewTrack(TrackEditor.Move(_trackStart!, clip.Id, start));
    }

    /// <summary>The clip or audio edge within <see cref="SnapRadius"/> pixels of <paramref name="time"/>, else the time itself.</summary>
    private TimeSpan Snap(TimeSpan time) =>
        TimelineSnap.Nearest(host.Timeline, time, TimeSpan.FromSeconds(SnapRadius / Math.Max(host.Zoom, 1e-9)), host.AudioEnd) ?? time;

    /// <summary>The clip boundary (index 0…count) nearest to <paramref name="time"/>.</summary>
    private int NearestCut(TimeSpan time)
    {
        var bounds = host.Timeline.Bounds;
        var best = 0;
        for (var k = 1; k < bounds.Length; k++)
            if ((bounds[k] - time).Duration() < (bounds[best] - time).Duration())
                best = k;
        return best;
    }

    private void Reset()
    {
        _drag = Drag.None;
        _clip = null;
        _gestureStart = null;
        _trackClip = null;
        _trackStart = null;
        InsertIndex = -1;
        _slipBase = TimeSpan.Zero;
    }
}
