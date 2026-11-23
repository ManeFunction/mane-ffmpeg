using System.Collections.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using ManeFfmpeg.Core.Editing;
using ManeFfmpeg.Core.Media;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.UI.ViewModels;

/// <summary>
/// An audio file dragged over the timeline: its name and color, and the clip it would replace the sound of, or where on
/// A2 it would start (<paramref name="TrackStart"/>) and for how long (<paramref name="TrackLength"/>, zero when unknown).
/// </summary>
public readonly record struct AudioDropTarget(string Name, Avalonia.Media.Color Color, string? ClipId,
    TimeSpan? TrackStart = null, TimeSpan TrackLength = default);

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

    /// <summary>
    /// What is selected: V1 clips, A2 clips or a whole audio track (never a mix); changed through <see cref="Select"/>
    /// and friends.
    /// </summary>
    public TimelineSelection Selected { get; private set; } = TimelineSelection.None;

    /// <summary>The selected clips' ids: V1 clips, or A2 clips; empty with a whole track selected.</summary>
    public IReadOnlySet<string> Selection => Selected.Ids;

    /// <summary>A whole audio track, selected by clicking its label (Selection then sets the track's volume), or null.</summary>
    public MixTrack? SelectedTrack => Selected.Track;

    /// <summary>Selects the whole track instead of any clips.</summary>
    public void SelectTrack(MixTrack track) => SetSelection(TimelineSelection.OfTrack(track));

    /// <summary>The selection is of A2 clips.</summary>
    public bool SelectionIsTrack => Selected.Kind == SelectionKind.TrackClips;

    public ImmutableList<TrackClip> Track => State.AudioTrack;

    public void PreviewTrack(ImmutableList<TrackClip> track) => owner.History.ReplaceCurrent(State with { AudioTrack = track });

    /// <summary>What an A2 clip plays; null when its file has no audio or isn't analyzed yet.</summary>
    public HeardAudio? Heard(TrackClip clip) => State.HeardAudio(clip, Info);

    public TimeSpan? FileEnd(TrackClip clip) => Heard(clip)?.End;

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
        owner.Preview?.OnPlayheadMoved();
    }

    public MediaInfo? Info(string sourceId) => owner.Catalog.Info(sourceId);

    /// <summary>The source's chosen audio stream.</summary>
    public AudioStreamInfo? Audio(SourceMedia source) => owner.Catalog.Audio(source);

    public CutGrid? GridOf(string sourceId) => owner.GridOf(sourceId);

    /// <summary>What the clip plays (as drawn on A1); null when it has no audio or isn't analyzed yet.</summary>
    public HeardAudio? Heard(Clip clip) => State.HeardAudio(clip, Info);

    /// <summary>Where the clip's heard audio stream ends; null when it has none or isn't analyzed yet.</summary>
    public TimeSpan? AudioEnd(Clip clip) => Heard(clip)?.End;

    public int ColorIndex(string sourceId) => owner.Sources.FirstOrDefault(s => s.Id == sourceId)?.ColorIndex ?? 0;

    public int AudioColorIndex(string fileId) => owner.AudioFiles.FirstOrDefault(s => s.Id == fileId)?.ColorIndex ?? 0;

    /// <summary>The audio file is missing or can't be read (or isn't in the Audio tab at all).</summary>
    public bool AudioFileHasProblem(string fileId) => owner.AudioFiles.FirstOrDefault(s => s.Id == fileId)?.IsProblem ?? true;

    /// <summary>Where an audio file being dragged would land, drawn by the control; null when nothing is dragged.</summary>
    public AudioDropTarget? AudioDrop { get; private set; }

    public void ShowAudioDrop(AudioDropTarget? target)
    {
        if (AudioDrop == target)
            return;
        AudioDrop = target;
        Invalidate();
    }

    /// <summary>The source is missing or can't be read (or isn't in the bin at all).</summary>
    public bool HasProblem(string sourceId) => owner.Sources.FirstOrDefault(s => s.Id == sourceId)?.IsProblem ?? true;

    public Waveform? Waveform(SourceMedia source) => owner.Catalog.Waveform(source);

    /// <summary>The source is still being probed or its waveform built (A1 shows "Loading…" meanwhile).</summary>
    public bool WaveformLoading(SourceMedia source) => owner.Catalog.WaveformLoading(source);

    public KeyframeIndex? Keyframes(SourceMedia source) => owner.Catalog.Keyframes(source);

    /// <summary>Where the last clipping check found the mix clipping, drawn red; see <see cref="ClipMarksCurrent"/>.</summary>
    public IReadOnlyList<ClipRange> ClipMarks => owner.ClipMarks;

    /// <summary>The project hasn't changed since the clipping check (otherwise its marks are drawn faded).</summary>
    public bool ClipMarksCurrent => owner.ClipMarksCurrent;

    public void Seek(TimeSpan time, bool scrubbing) => owner.Preview.SeekTo(Clamp(time), scrubbing);

    public void EndScrub() => owner.Preview.EndScrub();

    public TimeSpan Clamp(TimeSpan time)
    {
        var end = Timeline.Duration;
        return time < TimeSpan.Zero ? TimeSpan.Zero : time > end ? end : time;
    }

    public void Select(string clipId, bool toggle, bool range) => SetSelection(Selected.Click(State, clipId, toggle, range));

    public void SelectAll() => SetSelection(TimelineSelection.AllClips(State));

    /// <summary>Selects just this clip.</summary>
    public void SelectOnly(string clipId) => SetSelection(TimelineSelection.OfClips(State, [clipId]));

    public void ClearSelection() => SetSelection(TimelineSelection.None);

    /// <summary>Drops ids that no longer exist (after undo or delete).</summary>
    public void PruneSelection() => SetSelection(Selected.Pruned(State));

    private void SetSelection(TimelineSelection next)
    {
        if (next.Equals(Selected))
            return;
        Selected = next;
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
    public void PreviewAudioDrag(TimeSpan delta) => owner.Preview.PreviewAudioDelay(delta);

    // ---- Zoom -----------------------------------------------------------------------------------

    public void Fit()
    {
        var seconds = Math.Max(Timeline.Duration.TotalSeconds, 1);
        FitRange(TimeSpan.Zero, TimeSpan.FromSeconds(seconds));
    }

    /// <summary>Zooms and scrolls so the V1 or A2 clip &lt;paramref name="clipId"/&gt; fills the visible width.</summary>
    public void FitClip(string clipId)
    {
        var index = Timeline.IndexOf(clipId);
        if (index >= 0)
            FitRange(Timeline.StartOf(index), Timeline.StartOf(index + 1));
        else if (State.TrackClip(clipId) is { } trackClip)
            FitRange(trackClip.Start, trackClip.End);
    }

    private void FitRange(TimeSpan start, TimeSpan end) =>
        (Zoom, Scroll) = Controls.TimelineLayout.FitRange(ViewportWidth, start, end, MinZoom, MaxZoom);

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
        Scroll = Controls.TimelineLayout.FollowScroll(Scroll, ViewportWidth / Zoom, Playhead, Timeline.Duration);
    }
}
