using CommunityToolkit.Mvvm.ComponentModel;
using ManeFfmpeg.App.Services;
using ManeFfmpeg.Core.Editing;
using ManeFfmpeg.Core.Media;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.App.ViewModels;

/// <summary>What the timeline control draws (zoom, scroll, playhead, selection) and the edits it performs.</summary>
public sealed partial class TimelineViewModel : ViewModelBase, ITimelineGestureHost
{
    private readonly MainWindowViewModel owner;

    public TimelineViewModel(MainWindowViewModel owner)
    {
        this.owner = owner;
        Gestures = new TimelineGestures(this);
    }

    public const double MinZoom = 0.02;
    public const double MaxZoom = 1200;

    public ProjectState State => owner.State;

    public Timeline Timeline => owner.State.Timeline;

    public HashSet<string> Selection { get; } = [];

    IReadOnlySet<string> ITimelineGestureHost.Selection => Selection;

    /// <summary>Presses, drags and releases on the timeline, in timeline terms.</summary>
    public TimelineGestures Gestures { get; }

    /// <summary>Pixels per second. 0 until the first fit.</summary>
    [ObservableProperty]
    public partial double Zoom { get; set; }

    /// <summary>Timeline time (seconds) at the left edge of the tracks.</summary>
    [ObservableProperty]
    public partial double Scroll { get; set; }

    [ObservableProperty]
    public partial TimeSpan Playhead { get; set; }

    /// <summary>Width of the track area in pixels, reported by the control.</summary>
    public double ViewportWidth { get; set; } = 800;

    public bool ShowKeyframes => owner.SnapToKeyframes;

    /// <summary>The control redraws on this.</summary>
    public event Action? Invalidated;

    public void Invalidate() => Invalidated?.Invoke();

    partial void OnZoomChanged(double value) => Invalidate();

    partial void OnScrollChanged(double value) => Invalidate();

    partial void OnPlayheadChanged(TimeSpan value)
    {
        Invalidate();
        owner.OnPlayheadMoved();
    }

    public MediaInfo? Info(string sourceId) => owner.Media.GetValueOrDefault(sourceId);

    public CutGrid? GridOf(string sourceId) => owner.GridOf(sourceId);

    public int ColorIndex(string sourceId) => owner.Sources.FirstOrDefault(s => s.Id == sourceId)?.ColorIndex ?? 0;

    public bool IsMissing(string sourceId) => owner.Sources.FirstOrDefault(s => s.Id == sourceId)?.IsProblem ?? true;

    public Waveform? Waveform(SourceMedia source) =>
        Info(source.Id)?.Audio(source.AudioStreamIndex) is { } audio
            ? AppServices.Instance.Waveforms?.TryGet(source.Path, audio.Index)
            : null;

    public KeyframeIndex? Keyframes(SourceMedia source) => AppServices.Instance.Keyframes?.TryGet(source.Path);

    public void Seek(TimeSpan time, bool scrubbing) => owner.SeekTo(Clamp(time), scrubbing);

    public void EndScrub() => owner.EndScrub();

    public TimeSpan Clamp(TimeSpan time)
    {
        var end = Timeline.Duration;
        return time < TimeSpan.Zero ? TimeSpan.Zero : time > end ? end : time;
    }

    public void Select(string clipId, bool toggle, bool range)
    {
        if (range && Selection.Count > 0)
        {
            var clips = Timeline.Clips;
            var anchor = clips.FindIndex(c => Selection.Contains(c.Id));
            var target = Timeline.IndexOf(clipId);
            if (anchor >= 0 && target >= 0)
            {
                Selection.Clear();
                for (var i = Math.Min(anchor, target); i <= Math.Max(anchor, target); i++)
                    Selection.Add(clips[i].Id);
            }
        }
        else if (toggle)
        {
            if (!Selection.Remove(clipId))
                Selection.Add(clipId);
        }
        else if (!Selection.Contains(clipId) || Selection.Count > 1)
        {
            Selection.Clear();
            Selection.Add(clipId);
        }
        SelectionChanged();
    }

    public void SelectAll()
    {
        Selection.Clear();
        foreach (var clip in Timeline.Clips)
            Selection.Add(clip.Id);
        SelectionChanged();
    }

    public void ClearSelection()
    {
        if (Selection.Count == 0)
            return;
        Selection.Clear();
        SelectionChanged();
    }

    /// <summary>Drops ids that no longer exist (after undo or delete).</summary>
    public void PruneSelection()
    {
        var ids = Timeline.Clips.Select(c => c.Id).ToHashSet();
        if (Selection.RemoveWhere(id => !ids.Contains(id)) > 0)
            SelectionChanged();
    }

    private void SelectionChanged()
    {
        Invalidate();
        owner.OnSelectionChanged();
    }

    // ---- Gestures ---------------------------------------------------------------------------------

    public void BeginGesture() => owner.History.BeginGesture();

    public void PreviewTimeline(Timeline timeline) => owner.History.ReplaceCurrent(State with { Timeline = timeline });

    public void EndGesture() => owner.History.EndGesture();

    public void MoveSelection(int insertIndex) =>
        owner.CommitTimeline(t => TimelineEditor.Move(t, Selection, insertIndex));

    /// <summary>Live audio feedback while an offset is being dragged.</summary>
    public void PreviewAudioDrag(TimeSpan delta) => owner.PreviewAudioDelay(delta);

    // ---- Zoom -----------------------------------------------------------------------------------

    public void Fit()
    {
        var seconds = Math.Max(Timeline.Duration.TotalSeconds, 1);
        Zoom = Math.Clamp((ViewportWidth - 24) / seconds, MinZoom, MaxZoom);
        Scroll = 0;
    }

    /// <summary>Zooms keeping the time under <paramref name="anchorX"/> (pixels from the track start) in place.</summary>
    public void ZoomBy(double factor, double anchorX)
    {
        if (Zoom <= 0)
            Fit();
        var anchorTime = Scroll + anchorX / Zoom;
        Zoom = Math.Clamp(Zoom * factor, MinZoom, MaxZoom);
        Scroll = Math.Max(0, anchorTime - anchorX / Zoom);
    }

    public void ZoomAroundPlayhead(double factor)
    {
        if (Zoom <= 0)
            Fit();
        var x = (Playhead.TotalSeconds - Scroll) * Zoom;
        ZoomBy(factor, x is >= 0 && x <= ViewportWidth ? x : ViewportWidth / 2);
    }

    /// <summary>Keeps the playhead visible during playback.</summary>
    public void FollowPlayhead()
    {
        if (Zoom <= 0)
            return;
        var visible = ViewportWidth / Zoom;
        var t = Playhead.TotalSeconds;
        if (t < Scroll || t > Scroll + visible * 0.95)
            Scroll = Math.Max(0, t - visible * 0.1);
    }
}
