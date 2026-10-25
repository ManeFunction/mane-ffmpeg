using ManeFfmpeg.Core.Editing;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.App.ViewModels;

/// <summary>What a point on the timeline is over.</summary>
public enum TimelineZone
{
    /// <summary>The ruler (and the gap under it): clicks only seek.</summary>
    Ruler,

    /// <summary>Track rows, but no clip there (or between rows): clicks seek and clear the selection.</summary>
    Empty,

    /// <summary>A clip's left edge in V1.</summary>
    TrimIn,

    /// <summary>A clip's right edge in V1.</summary>
    TrimOut,

    /// <summary>A clip's body in V1: select, or drag to reorder.</summary>
    Clip,

    /// <summary>A clip in A1: select, or drag to slip its audio.</summary>
    Audio,
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
}

/// <summary>
/// The timeline's pointer gestures in timeline terms: a press on a zone, moves to a time, a release. Clicks
/// select and seek; drags scrub, reorder clips, trim edges or slip audio, each drag one undo step.
/// </summary>
public sealed class TimelineGestures(ITimelineGestureHost host)
{
    /// <summary>How far (pixels) the pointer must move before a press on a clip becomes a drag.</summary>
    public const double DragThreshold = 4;

    private enum Drag { None, Scrub, PendingClip, Reorder, TrimIn, TrimOut, PendingSlip, Slip }

    private Drag _drag;
    private TimeSpan _pressTime;
    private Clip? _clip;
    private Timeline? _gestureStart;
    private bool _command;
    private bool _shift;

    public bool IsActive => _drag != Drag.None;

    public bool IsReordering => _drag == Drag.Reorder;

    /// <summary>Where the dragged clips would land (an index into the clips), or -1 when not reordering.</summary>
    public int InsertIndex { get; private set; } = -1;

    /// <param name="command">Cmd/Ctrl was held: a click toggles the clip in the selection.</param>
    /// <param name="shift">Shift was held: a click extends the selection to the clip.</param>
    public void Press(TimelineHit hit, bool command, bool shift)
    {
        Reset();
        _pressTime = hit.Time;
        _command = command;
        _shift = shift;
        var index = hit.ClipId is { } id ? host.Timeline.IndexOf(id) : -1;
        if (index < 0)
        {
            _drag = Drag.Scrub;
            if (hit.Zone == TimelineZone.Empty)
                host.ClearSelection();
            host.Seek(hit.Time, scrubbing: true);
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
    /// <param name="fine">Slip at a tenth of the pointer's speed (Shift held).</param>
    public void Move(TimeSpan time, bool fine)
    {
        var delta = time - _pressTime;
        var dragged = Math.Abs(delta.TotalSeconds * host.Zoom) > DragThreshold;
        switch (_drag)
        {
            case Drag.Scrub:
                host.Seek(time, scrubbing: true);
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
                break;
            case Drag.Slip:
            {
                var slip = TimeSpan.FromMilliseconds(Math.Round(delta.TotalMilliseconds * (fine ? 0.1 : 1)));
                host.PreviewTimeline(TimelineEditor.NudgeAudioOffset(_gestureStart!, host.Selection, slip));
                host.PreviewAudioDrag(slip);
                break;
            }
        }
    }

    /// <param name="time">Timeline time under the pointer at release.</param>
    public void Release(TimeSpan time)
    {
        switch (_drag)
        {
            case Drag.Scrub:
                host.EndScrub();
                break;
            case Drag.PendingClip:
            case Drag.PendingSlip:
                host.Select(_clip!.Id, toggle: _command, range: _shift);
                if (!_command && !_shift)
                    host.Seek(time, scrubbing: false);
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
        if (_drag is Drag.TrimIn or Drag.TrimOut or Drag.Slip)
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
        InsertIndex = -1;
    }
}
