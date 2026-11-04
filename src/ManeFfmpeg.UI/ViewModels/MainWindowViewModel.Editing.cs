using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManeFfmpeg.Core.Editing;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.UI.ViewModels;

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

    /// <summary>
    /// Audio offset of the selected clip(s), in milliseconds; positive = audio later.
    /// Takes simple arithmetic too, so "0-700" sets −700 ms.
    /// </summary>
    public string AudioOffsetText
    {
        get => PrimaryClip is { } c
            ? Math.Round(c.ActiveAudioOffset.TotalMilliseconds, 1).ToString("0.#", CultureInfo.InvariantCulture)
            : "";
        set
        {
            if (Timeline.Selection.Count > 0 && PrimaryClip is { } primary && Arithmetic.TryEvaluate(value, out var ms))
            {
                var max = TimelineEditor.MaxAudioOffsetFor(primary).TotalMilliseconds;
                var offset = TimeSpan.FromTicks((long)Math.Round(Math.Clamp(ms, -max, max) * TimeSpan.TicksPerMillisecond));
                CommitTimeline(t => TimelineEditor.SetAudioOffset(t, Timeline.Selection, offset));
            }
            OnPropertyChanged(nameof(AudioOffsetText));
        }
    }

    public string AudioOffsetHint => PrimaryClip is { ReplacedAudio: { } r }
        ? r.Offset == TimeSpan.Zero
            ? "Starts where it was dropped."
            : r.Offset > TimeSpan.Zero
                ? $"Starts {Seconds(r.Offset)} later."
                : $"Starts {Seconds(-r.Offset)} into the file."
        : PrimaryClip is { } c && FrameDuration(c) is { } frame && frame > TimeSpan.Zero
        ? c.AudioOffset == TimeSpan.Zero
            ? "Audio in sync with its video."
            : $"≈ {c.AudioOffset.TotalMilliseconds / frame.TotalMilliseconds:+0.0;−0.0} frames · audio plays {(c.AudioOffset > TimeSpan.Zero ? "later" : "earlier")}"
        : "";

    private static string Seconds(TimeSpan t) => string.Create(CultureInfo.InvariantCulture, $"{t.TotalSeconds:0.###} s");

    // ---- Replaced audio ---------------------------------------------------------------------------

    /// <summary>The selected clip plays an audio file instead of its own sound.</summary>
    public bool HasReplacedAudio => PrimaryClip?.ReplacedAudio is not null;

    public string ReplacedAudioName => PrimaryClip?.ReplacedAudio is { } r
        ? $"Audio: {State.AudioFile(r.FileId)?.DisplayName ?? "missing file"}"
        : "";

    public IBrush? ReplacedAudioBrush => PrimaryClip?.ReplacedAudio is { } r && FileItem(r.FileId) is { } item ? item.Brush : null;

    /// <summary>Repeat the replaced audio of the selected clips so they have no silence.</summary>
    public bool LoopAudio
    {
        get => PrimaryClip?.LoopsAudio ?? false;
        set
        {
            if (value != LoopAudio)
                CommitTimeline(t => TimelineEditor.SetAudioLoop(t, Timeline.Selection, value));
            OnPropertyChanged(nameof(LoopAudio));
        }
    }

    public string ApplyOffsetText => HasReplacedAudio ? "Use this offset for all clips with this audio" : "Use this offset for all clips of this video";

    /// <summary>Plays audio file <paramref name="fileId"/> instead of the clip's sound (an audio file dropped on it).</summary>
    public void ReplaceClipAudio(string clipId, string fileId)
    {
        var next = ProjectEditor.ReplaceClipAudio(State, clipId, fileId);
        if (ReferenceEquals(next, State))
            return;
        Commit(next);
        Timeline.SelectOnly(clipId);
        StatusMessage = ReplacedMessage(fileId);
    }

    private string ReplacedMessage(string fileId) =>
        $"{State.AudioFile(fileId)?.DisplayName} replaces the clip's sound. Drag its audio row to move it; Selection has Loop audio.";

    [RelayCommand]
    private void RestoreOriginalAudio() => CommitTimeline(t => TimelineEditor.RestoreOriginalAudio(t, TargetClips()));

    internal void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(HasReplacedAudio));
        OnPropertyChanged(nameof(ReplacedAudioName));
        OnPropertyChanged(nameof(ReplacedAudioBrush));
        OnPropertyChanged(nameof(LoopAudio));
        OnPropertyChanged(nameof(ApplyOffsetText));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionTitle));
        OnPropertyChanged(nameof(SelectionDetails));
        OnPropertyChanged(nameof(ClipIn));
        OnPropertyChanged(nameof(ClipOut));
        OnPropertyChanged(nameof(AudioOffsetText));
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
        // Clips deleted before the playhead pull what's under it earlier; the playhead follows it.
        var playhead = Timeline.Playhead;
        var moved = TimelineEditor.TimeAfterDelete(State.Timeline, Timeline.Selection, playhead);
        CommitTimeline(t => TimelineEditor.Delete(t, Timeline.Selection));
        if (moved != playhead)
            Preview.MovePlayheadAfterEdit(moved);
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
        if (clip.ReplacedAudio is { } r)
        {
            CommitTimeline(t => TimelineEditor.SetReplacedAudioOffsetForFile(t, r.FileId, r.Offset));
            StatusMessage = $"Audio offset {TimeFormat.Offset(r.Offset)} applied to every clip playing {State.AudioFile(r.FileId)?.DisplayName}.";
            return;
        }
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
