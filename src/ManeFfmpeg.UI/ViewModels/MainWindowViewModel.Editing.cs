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

    private Clip? PrimaryClip => Timeline.Selected.Kind == SelectionKind.TimelineClips
        ? State.Timeline.Clips.FirstOrDefault(c => Timeline.Selected.Contains(c.Id))
        : null;

    /// <summary>The first selected A2 clip, when A2 clips are selected.</summary>
    private TrackClip? PrimaryTrackClip => Timeline.Selected.Kind == SelectionKind.TrackClips
        ? State.AudioTrack.FirstOrDefault(c => Timeline.Selected.Contains(c.Id))
        : null;

    private IReadOnlySet<string> TargetClips()
    {
        if (Timeline.Selection.Count > 0)
            return Timeline.Selection;
        return State.Timeline.Locate(Timeline.Playhead) is { } at
            ? new HashSet<string> { State.Timeline.Clips[at.Index].Id }
            : new HashSet<string>();
    }

    /// <summary>V1 clips are selected (their in/out and audio offset can be edited).</summary>
    public bool HasSelection => PrimaryClip is not null;

    /// <summary>A2 clips are selected.</summary>
    public bool HasTrackSelection => PrimaryTrackClip is not null;

    /// <summary>A whole audio track is selected by its label: Selection shows only its volume.</summary>
    public bool IsWholeTrackSelected => Timeline.Selected.Kind == SelectionKind.WholeTrack;

    public bool IsA1Selected => Timeline.SelectedTrack == MixTrack.A1;

    public bool IsA2Selected => Timeline.SelectedTrack == MixTrack.A2;

    /// <summary>Clips on either track, or a whole track, are selected (their volume can be edited).</summary>
    public bool HasAnySelection => !Timeline.Selected.IsEmpty;

    [RelayCommand]
    private void SelectA1Track() => Timeline.SelectTrack(MixTrack.A1);

    [RelayCommand]
    private void SelectA2Track() => Timeline.SelectTrack(MixTrack.A2);

    public string A1TrackTip => $"A1: the clips' sound. Click to select the whole track and set its volume ({Gain.Percent(State.A1Volume)} now).";

    public string A2TrackTip => $"A2: audio mixed over the clips. Click to select the whole track and set its volume ({Gain.Percent(State.A2Volume)} now).";

    public string SelectionTitle => Timeline.Selection.Count switch
    {
        0 when Timeline.SelectedTrack is { } whole => $"{whole} track selected",
        0 => "No clip selected",
        1 when PrimaryTrackClip is { } t => $"A2 · {State.AudioFile(t.FileId)?.DisplayName ?? "missing audio file"}",
        1 => PrimaryClip is { } c ? State.Source(c.SourceId)?.DisplayName ?? "Clip" : "Clip",
        var n => HasTrackSelection ? $"{n} A2 clips selected" : $"{n} clips selected",
    };

    public string SelectionDetails
    {
        get
        {
            if (Timeline.SelectedTrack is { } whole)
            {
                var what = whole == MixTrack.A2
                    ? State.AudioTrack.Count switch { 0 => "A2 has no clips yet", 1 => "1 clip on A2", var n => $"{n} clips on A2" }
                    : State.Timeline.Clips.Count == 1 ? "The sound of the clip on V1" : $"The sound of all {State.Timeline.Clips.Count} clips on V1";
                return $"{what}. Its volume applies on top of each clip's own volume, in the preview and the export.";
            }
            if (PrimaryTrackClip is { } track)
            {
                if (Timeline.Selection.Count > 1)
                {
                    var length = State.AudioTrack.Where(c => Timeline.Selection.Contains(c.Id)).Aggregate(TimeSpan.Zero, (s, c) => s + c.Duration);
                    return $"Total {TimeFormat.Display(length)}";
                }
                return $"Starts at {TimeFormat.Display(track.Start)} · {TimeFormat.Display(track.Duration)} long\n" +
                       $"File {TimeFormat.Display(track.In)} – {TimeFormat.Display(track.Out)}";
            }
            if (PrimaryClip is not { } clip)
                return "Click a clip on the timeline to edit it. Space plays, S splits at the playhead. Drag an audio file onto A2 to mix it in.";
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

    // ---- Volume -----------------------------------------------------------------------------------

    private bool _volumeDragging;

    /// <summary>
    /// The selected clips' volume in percent (0–200), V1 or A2; the first selected clip's when they differ. With a whole
    /// track selected, that track's volume.
    /// </summary>
    public double ClipVolume
    {
        get => Math.Round((Timeline.SelectedTrack is { } whole ? State.TrackVolume(whole) : PrimaryTrackClip?.Volume ?? PrimaryClip?.Volume ?? 1) * 100);
        set
        {
            var gain = Gain.Clamp(value / 100);
            var next = ProjectEditor.SetVolume(State, Timeline.Selected, gain);
            // A slider drag previews every step and records one undo step when it ends.
            if (_volumeDragging)
                History.ReplaceCurrent(next);
            else
                Commit(next);
            OnVolumeChanged();
        }
    }

    public string ClipVolumeText
    {
        get
        {
            var gain = ClipVolume / 100;
            return $"{Gain.Percent(gain)} · {Gain.Decibels(gain)}";
        }
    }

    /// <summary>The volume slider's thumb was grabbed: changes until <see cref="EndVolumeDrag"/> are one undo step.</summary>
    public void BeginVolumeDrag()
    {
        History.BeginGesture();
        _volumeDragging = true;
    }

    public void EndVolumeDrag()
    {
        _volumeDragging = false;
        History.EndGesture();
    }

    [RelayCommand]
    private void ResetVolume() => Commit(ProjectEditor.SetVolume(State, Timeline.Selected, 1));

    public string VolumeTip => Timeline.SelectedTrack is { } whole
        ? $"Volume of the whole {whole} track, on top of each clip's own: 100 % leaves it as it is, 200 % is about +6 dB"
        : "Volume of the selected clips: 100 % leaves the sound as it is, 200 % is about +6 dB";

    public string ApplyVolumeText => HasTrackSelection || HasReplacedAudio
        ? "Use this volume everywhere this audio file plays"
        : "Use this volume for all clips of this video";

    [RelayCommand]
    private void ApplyVolumeToAll()
    {
        // An audio file's volume goes to every place it plays (A1 replacements and A2); a video's own sound only to its
        // clips that still play it.
        var (fileId, gain) = PrimaryTrackClip is { } track ? (track.FileId, track.Volume)
            : PrimaryClip is { ReplacedAudio: { } r } replaced ? (r.FileId, replaced.Volume)
            : ((string?)null, 0.0);
        if (fileId is not null)
        {
            Commit(ProjectEditor.SetAudioFileVolume(State, fileId, gain));
            StatusMessage = $"Volume {Gain.Percent(gain)} applied everywhere {State.AudioFile(fileId)?.DisplayName} plays, on A1 and A2.";
            return;
        }
        if (PrimaryClip is not { } clip)
            return;
        CommitTimeline(t => TimelineEditor.SetVolumeForSource(t, clip.SourceId, clip.Volume));
        StatusMessage = $"Volume {Gain.Percent(clip.Volume)} applied to every clip of {State.Source(clip.SourceId)?.DisplayName} that plays its own sound.";
    }

    private void OnVolumeChanged()
    {
        OnPropertyChanged(nameof(ClipVolume));
        OnPropertyChanged(nameof(ClipVolumeText));
        OnPropertyChanged(nameof(A1TrackTip));
        OnPropertyChanged(nameof(A2TrackTip));
    }

    // ---- A2 ---------------------------------------------------------------------------------------

    /// <summary>How long a file would play on A2: where its audio ends, or null while it isn't analyzed.</summary>
    public TimeSpan? TrackLength(string fileId) => State.HeardAudioFile(fileId, _catalog.Info)?.End;

    /// <summary>How long a file from the file manager would play on A2, when it's already in the project and analyzed.</summary>
    public TimeSpan? TrackLengthOfPath(string path) =>
        State.AudioFiles.FirstOrDefault(f => string.Equals(f.Path, Path.GetFullPath(path), ManeFfmpeg.Core.Platforms.Platform.Current.PathComparison)) is { } file
            ? TrackLength(file.Id)
            : null;

    /// <summary>Puts audio file <paramref name="fileId"/> on A2 at <paramref name="start"/> (an audio file dragged there).</summary>
    public void AddToTrack(string fileId, TimeSpan start)
    {
        if (TrackLength(fileId) is not { } length)
        {
            StatusMessage = $"{State.AudioFile(fileId)?.DisplayName} is still being analyzed; drop it again in a moment.";
            return;
        }
        var (next, id) = ProjectEditor.AddTrackClip(State, fileId, start, length);
        if (id is null)
        {
            StatusMessage = TrackTakenMessage;
            return;
        }
        Commit(next);
        Timeline.SelectOnly(id);
        StatusMessage = TrackMessage(fileId);
    }

    private const string TrackTakenMessage = "That part of A2 is taken: drop the file on an empty stretch.";

    private string TrackMessage(string fileId) =>
        $"{State.AudioFile(fileId)?.DisplayName} is mixed in on A2. Drag it to move it, drag its edges to trim it; Selection sets its volume.";

    /// <summary>Everything the Selection panel shows that depends on what is selected.</summary>
    private static readonly string[] SelectionProperties =
    [
        nameof(IsWholeTrackSelected), nameof(IsA1Selected), nameof(IsA2Selected), nameof(VolumeTip), nameof(HasTrackSelection),
        nameof(HasAnySelection), nameof(ApplyVolumeText), nameof(HasReplacedAudio), nameof(ReplacedAudioName),
        nameof(ReplacedAudioBrush), nameof(LoopAudio), nameof(ApplyOffsetText), nameof(HasSelection), nameof(SelectionTitle),
        nameof(SelectionDetails), nameof(ClipIn), nameof(ClipOut), nameof(AudioOffsetText), nameof(AudioOffsetHint),
    ];

    internal void OnSelectionChanged()
    {
        foreach (var name in SelectionProperties)
            OnPropertyChanged(name);
        OnVolumeChanged();
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
        if (ProjectEditor.Split(State, Timeline.Selected, Timeline.Playhead, GridOf) is not { } split)
        {
            StatusMessage = Timeline.SelectionIsTrack
                ? "Move the playhead inside a selected A2 clip (away from its edges) to split it."
                : "Move the playhead inside a clip (away from its edges) to split.";
            return;
        }
        Commit(split.State);
        // The left half of a V1 clip, the part already played, keeps the clip's id.
        if (split.Select is { } left)
            Timeline.SelectOnly(left);
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
        var playhead = Timeline.Playhead;
        var (next, moved) = ProjectEditor.DeleteSelection(State, Timeline.Selected, playhead);
        Commit(next);
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
