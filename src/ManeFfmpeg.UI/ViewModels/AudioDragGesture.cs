namespace ManeFfmpeg.UI.ViewModels;

/// <summary>What dragging an audio file onto the timeline changes in the project.</summary>
public interface IAudioDragHost
{
    /// <summary>Feedback on the timeline: the file being dragged and the clip it would replace the sound of, or its place on A2.</summary>
    void ShowAudioDrop(AudioDropTarget? target);

    void ReplaceClipAudio(string clipId, string fileId);

    /// <summary>Puts the file on A2 at timeline time <paramref name="start"/>.</summary>
    void AddToTrack(string fileId, TimeSpan start);

    /// <summary>How long the file would play on A2; null while it isn't analyzed.</summary>
    TimeSpan? TrackLength(string fileId);

    string? StatusMessage { get; set; }
}

/// <summary>Where a dragged audio file would go: onto a clip (replacing its sound), or onto A2 at a time.</summary>
public abstract record AudioDropPoint
{
    private AudioDropPoint() { }

    /// <summary>Onto a V1 clip, to replace its sound.</summary>
    public sealed record ClipDrop(string ClipId) : AudioDropPoint;

    /// <summary>Onto A2, to mix it in from timeline time <paramref name="Start"/>.</summary>
    public sealed record TrackDrop(TimeSpan Start) : AudioDropPoint;

    public static AudioDropPoint OnClip(string clipId) => new ClipDrop(clipId);

    public static AudioDropPoint OnTrack(TimeSpan start) => new TrackDrop(start < TimeSpan.Zero ? TimeSpan.Zero : start);
}

/// <summary>
/// Dragging a file from the Audio tab onto a clip to replace its sound, or onto A2 to mix it in. An in-app pointer drag
/// rather than the platform's drag and drop: it can't get stuck in a native drag session, draws its own feedback on the
/// timeline and can be scripted. The window only turns pointer positions into a distance moved and the place under the
/// pointer.
/// </summary>
public sealed class AudioDragGesture(IAudioDragHost host)
{
    private SourceItemViewModel? _pressed;

    /// <summary>The pointer moved far enough with a file pressed: the drag is on.</summary>
    public bool IsDragging { get; private set; }

    /// <summary>Where the file would be dropped; null when not over a clip or A2.</summary>
    public AudioDropPoint? Target { get; private set; }

    /// <summary>The clip the file would be dropped on.</summary>
    public string? TargetClip => (Target as AudioDropPoint.ClipDrop)?.ClipId;

    /// <summary>The pointer went down on a row (null when not on a usable file).</summary>
    public void Press(SourceItemViewModel? item)
    {
        End(drop: false);
        _pressed = item is { IsProblem: false } ? item : null;
    }

    /// <summary>
    /// The pointer moved <paramref name="distance"/> pixels from the press and is over <paramref name="target"/> (or
    /// nowhere to drop). Returns whether the drag is on, so the window can capture the pointer and show the drop cursor.
    /// </summary>
    public bool Move(double distance, AudioDropPoint? target)
    {
        if (_pressed is not { } item)
            return false;
        if (!IsDragging)
        {
            if (distance <= TimelineGestures.DragThreshold)
                return false;
            IsDragging = true;
            host.StatusMessage = $"Drop {item.Name} on a clip to replace its sound, or on A2 to mix it in.";
        }
        Target = target;
        host.ShowAudioDrop(target is AudioDropPoint.TrackDrop(var start)
            ? new AudioDropTarget(item.Name, item.Color, null, start, host.TrackLength(item.Id) ?? default)
            : new AudioDropTarget(item.Name, item.Color, (target as AudioDropPoint.ClipDrop)?.ClipId));
        return true;
    }

    /// <summary>The pointer went up: drops the file on the target, if any. Returns whether a drag ended.</summary>
    public bool Release() => End(drop: true);

    /// <summary>Escape, or the pointer capture was lost: the drag ends without dropping.</summary>
    public bool Cancel() => End(drop: false);

    private bool End(bool drop)
    {
        var (item, target, dragging) = (_pressed, Target, IsDragging);
        _pressed = null;
        Target = null;
        IsDragging = false;
        if (!dragging)
            return false;
        host.ShowAudioDrop(null);
        host.StatusMessage = null;
        if (drop && item is not null)
        {
            switch (target)
            {
                case AudioDropPoint.ClipDrop(var clip):
                    host.ReplaceClipAudio(clip, item.Id);
                    break;
                case AudioDropPoint.TrackDrop(var start):
                    host.AddToTrack(item.Id, start);
                    break;
            }
        }
        return true;
    }
}
