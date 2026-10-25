using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManeFfmpeg.Core.Editing;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    /// <summary>Show keyframes and snap in points (splits, trims, marks) to them; on automatically for stream-copy presets.</summary>
    [ObservableProperty]
    public partial bool SnapToKeyframes { get; set; }

    partial void OnSnapToKeyframesChanged(bool value)
    {
        _catalog.WantKeyframes = value;
        Timeline.Invalidate();
    }

    public string TimelineDurationText => TimeFormat.Display(State.Timeline.Duration);

    // ---- Inspector --------------------------------------------------------------------------------

    private Clip? PrimaryClip => State.Timeline.Clips.FirstOrDefault(c => Timeline.Selection.Contains(c.Id));

    private IReadOnlySet<string> TargetClips()
    {
        if (Timeline.Selection.Count > 0)
            return Timeline.Selection;
        return State.Timeline.Locate(Timeline.Playhead) is { } at
            ? new HashSet<string> { State.Timeline.Clips[at.Index].Id }
            : new HashSet<string>();
    }

    public bool HasSelection => Timeline.Selection.Count > 0;

    public string SelectionTitle => Timeline.Selection.Count switch
    {
        0 => "No clip selected",
        1 => PrimaryClip is { } c ? State.Source(c.SourceId)?.DisplayName ?? "Clip" : "Clip",
        var n => $"{n} clips selected",
    };

    public string SelectionDetails
    {
        get
        {
            if (PrimaryClip is not { } clip)
                return "Click a clip on the timeline to edit it. Space plays, S splits at the playhead.";
            if (Timeline.Selection.Count > 1)
            {
                var total = State.Timeline.Clips.Where(c => Timeline.Selection.Contains(c.Id))
                    .Aggregate(TimeSpan.Zero, (s, c) => s + c.Duration);
                return $"Total {TimeFormat.Display(total)}";
            }
            return $"Duration {TimeFormat.Display(clip.Duration)}";
        }
    }

    public string ClipIn
    {
        get => PrimaryClip is { } c ? TimeFormat.Display(c.In) : "";
        set
        {
            if (PrimaryClip is { } c && TimeFormat.TryParse(value, out var time))
                CommitTimeline(t => TimelineEditor.SetRange(t, c.Id, time, c.Out, GridOf));
            OnSelectionChanged();
        }
    }

    public string ClipOut
    {
        get => PrimaryClip is { } c ? TimeFormat.Display(c.Out) : "";
        set
        {
            if (PrimaryClip is { } c && TimeFormat.TryParse(value, out var time))
                CommitTimeline(t => TimelineEditor.SetRange(t, c.Id, c.In, time, GridOf));
            OnSelectionChanged();
        }
    }

    /// <summary>Audio offset of the selected clip(s), in milliseconds; positive = audio later.</summary>
    public decimal? AudioOffsetMs
    {
        get => PrimaryClip is { } c ? (decimal)Math.Round(c.AudioOffset.TotalMilliseconds, 1) : null;
        set
        {
            if (value is not { } ms || Timeline.Selection.Count == 0)
                return;
            var offset = TimeSpan.FromTicks((long)Math.Round((double)ms * TimeSpan.TicksPerMillisecond));
            CommitTimeline(t => TimelineEditor.SetAudioOffset(t, Timeline.Selection, offset));
        }
    }

    public string AudioOffsetHint => PrimaryClip is { } c && FrameDuration(c) is { } frame && frame > TimeSpan.Zero
        ? c.AudioOffset == TimeSpan.Zero
            ? "Audio in sync with its video."
            : $"≈ {c.AudioOffset.TotalMilliseconds / frame.TotalMilliseconds:+0.0;−0.0} frames · audio plays {(c.AudioOffset > TimeSpan.Zero ? "later" : "earlier")}"
        : "";

    internal void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionTitle));
        OnPropertyChanged(nameof(SelectionDetails));
        OnPropertyChanged(nameof(ClipIn));
        OnPropertyChanged(nameof(ClipOut));
        OnPropertyChanged(nameof(AudioOffsetMs));
        OnPropertyChanged(nameof(AudioOffsetHint));
    }

    /// <summary>Where cuts may land in a source: on frames, and in points on keyframes while snapping to them.</summary>
    internal CutGrid? GridOf(string sourceId) =>
        State.Source(sourceId) is { } source && _catalog.Info(sourceId) is { } info
            ? CutGrid.For(info, SnapToKeyframes ? _catalog.Keyframes(source)?.Times : null)
            : null;

    private TimeSpan? FrameDuration(Clip clip) =>
        _catalog.Info(clip.SourceId)?.Video is { } v ? TimeFormat.FrameDuration(v.FrameRate) : null;

    // ---- Edits ------------------------------------------------------------------------------------

    [RelayCommand]
    private void Split()
    {
        if (!HasClips)
            return;
        var (next, rightId) = TimelineEditor.Split(State.Timeline, Timeline.Playhead, GridOf);
        if (rightId is null)
        {
            StatusMessage = "Move the playhead inside a clip (away from its edges) to split.";
            return;
        }
        Commit(State with { Timeline = next });
        // The left half, the part already played, keeps the clip's id.
        Timeline.SelectOnly(next.Clips[next.IndexOf(rightId) - 1].Id);
        StatusMessage = null;
    }

    [RelayCommand]
    private void DeleteSelection()
    {
        if (Timeline.Selection.Count == 0)
        {
            StatusMessage = "Select clips to delete.";
            return;
        }
        CommitTimeline(t => TimelineEditor.Delete(t, Timeline.Selection));
    }

    [RelayCommand]
    private void SelectAll() => Timeline.SelectAll();

    private static readonly TimeSpan NudgeStep = TimeSpan.FromMilliseconds(10);

    [RelayCommand]
    private void NudgeAudioEarlier() => NudgeAudio(-NudgeStep);

    [RelayCommand]
    private void NudgeAudioLater() => NudgeAudio(NudgeStep);

    [RelayCommand]
    private void NudgeAudioFrameEarlier() => NudgeAudioFrames(-1);

    [RelayCommand]
    private void NudgeAudioFrameLater() => NudgeAudioFrames(1);

    /// <summary>Slips the audio of the selected clips (or the clip under the playhead) by <paramref name="delta"/>.</summary>
    public void NudgeAudio(TimeSpan delta) => CommitTimeline(t => TimelineEditor.NudgeAudioOffset(t, TargetClips(), delta));

    private void NudgeAudioFrames(int frames)
    {
        var targets = TargetClips();
        var clip = State.Timeline.Clips.FirstOrDefault(c => targets.Contains(c.Id));
        if (clip is null || FrameDuration(clip) is not { } frame)
            return;
        CommitTimeline(t => TimelineEditor.NudgeAudioOffset(t, targets, frame * frames));
    }

    [RelayCommand]
    private void ResetAudioOffset() =>
        CommitTimeline(t => TimelineEditor.SetAudioOffset(t, TargetClips(), TimeSpan.Zero));

    [RelayCommand]
    private void ApplyOffsetToSource()
    {
        if (PrimaryClip is not { } clip)
            return;
        CommitTimeline(t => TimelineEditor.SetAudioOffsetForSource(t, clip.SourceId, clip.AudioOffset));
        StatusMessage = $"Audio offset {TimeFormat.Offset(clip.AudioOffset)} applied to every clip of {State.Source(clip.SourceId)?.DisplayName}.";
    }

    [RelayCommand]
    private void ZoomIn() => Timeline.ZoomAroundPlayhead(1.5);

    [RelayCommand]
    private void ZoomOut() => Timeline.ZoomAroundPlayhead(1 / 1.5);

    [RelayCommand]
    private void ZoomFit() => Timeline.Fit();
}
