namespace ManeFfmpeg.UI.ViewModels;

/// <summary>What dragging an audio file onto a clip changes in the project.</summary>
public interface IAudioDragHost
{
    /// <summary>Feedback on the timeline: the file being dragged and the clip it would replace the sound of.</summary>
    void ShowAudioDrop(AudioDropTarget? target);

    void ReplaceClipAudio(string clipId, string fileId);

    string? StatusMessage { get; set; }
}

/// <summary>
/// Dragging a file from the Audio tab onto a clip to replace its sound. An in-app pointer drag rather than the
/// platform's drag and drop: it can't get stuck in a native drag session, draws its own feedback on the timeline and
/// can be scripted. The window only turns pointer positions into a distance moved and the clip under the pointer.
/// </summary>
public sealed class AudioDragGesture(IAudioDragHost host)
{
    private SourceItemViewModel? _pressed;

    /// <summary>The pointer moved far enough with a file pressed: the drag is on.</summary>
    public bool IsDragging { get; private set; }

    /// <summary>The clip the file would be dropped on.</summary>
    public string? TargetClip { get; private set; }

    /// <summary>The pointer went down on a row (null when not on a usable file).</summary>
    public void Press(SourceItemViewModel? item)
    {
        End(drop: false);
        _pressed = item is { IsProblem: false } ? item : null;
    }

    /// <summary>
    /// The pointer moved <paramref name="distance"/> pixels from the press and is over <paramref name="clipId"/> (or no
    /// clip). Returns whether the drag is on, so the window can capture the pointer and show the drop cursor.
    /// </summary>
    public bool Move(double distance, string? clipId)
    {
        if (_pressed is not { } item)
            return false;
        if (!IsDragging)
        {
            if (distance <= TimelineGestures.DragThreshold)
                return false;
            IsDragging = true;
            host.StatusMessage = $"Drop {item.Name} on a clip to replace its sound.";
        }
        TargetClip = clipId;
        host.ShowAudioDrop(new AudioDropTarget(item.Name, item.Color, clipId));
        return true;
    }

    /// <summary>The pointer went up: drops the file on the target clip, if any. Returns whether a drag ended.</summary>
    public bool Release() => End(drop: true);

    /// <summary>Escape, or the pointer capture was lost: the drag ends without dropping.</summary>
    public bool Cancel() => End(drop: false);

    private bool End(bool drop)
    {
        var (item, clip, dragging) = (_pressed, TargetClip, IsDragging);
        _pressed = null;
        TargetClip = null;
        IsDragging = false;
        if (!dragging)
            return false;
        host.ShowAudioDrop(null);
        host.StatusMessage = null;
        if (drop && item is not null && clip is not null)
            host.ReplaceClipAudio(clip, item.Id);
        return true;
    }
}
