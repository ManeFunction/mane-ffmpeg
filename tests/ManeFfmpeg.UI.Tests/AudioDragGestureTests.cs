using ManeFfmpeg.Core.Media;
using ManeFfmpeg.Core.Model;
using ManeFfmpeg.UI.ViewModels;

namespace ManeFfmpeg.UI.Tests;

/// <summary>Dragging an audio file onto a clip, against a fake project.</summary>
public class AudioDragGestureTests
{
    private sealed class Host : IAudioDragHost
    {
        public List<AudioDropTarget?> Drops { get; } = [];
        public (string Clip, string File)? Replaced { get; private set; }
        public string? StatusMessage { get; set; }
        public void ShowAudioDrop(AudioDropTarget? target) => Drops.Add(target);
        public void ReplaceClipAudio(string clipId, string fileId) => Replaced = (clipId, fileId);
    }

    private static SourceItemViewModel Song(SourceStatus status = SourceStatus.Ready) =>
        new(new SourceMedia { Id = "f", Path = "/m/song.m4a" }, 0, FileKind.Audio) { Status = status };

    [Fact]
    public void Dropping_on_a_clip_replaces_its_sound()
    {
        var host = new Host();
        var drag = new AudioDragGesture(host);
        drag.Press(Song());
        Assert.False(drag.Move(TimelineGestures.DragThreshold, "c"));
        Assert.Empty(host.Drops);
        Assert.True(drag.Move(20, null));
        Assert.Contains("song.m4a", host.StatusMessage);
        Assert.True(drag.Move(40, "c"));
        Assert.Equal("c", drag.TargetClip);
        Assert.True(drag.Release());
        Assert.Equal(("c", "f"), host.Replaced);
        Assert.Null(host.Drops[^1]);
        Assert.Null(host.StatusMessage);
        Assert.False(drag.IsDragging);
    }

    [Fact]
    public void Released_off_a_clip_or_cancelled_nothing_changes()
    {
        var host = new Host();
        var drag = new AudioDragGesture(host);
        drag.Press(Song());
        drag.Move(20, "c");
        drag.Move(30, null);
        Assert.True(drag.Release());
        Assert.Null(host.Replaced);

        drag.Press(Song());
        drag.Move(20, "c");
        Assert.True(drag.Cancel());
        Assert.Null(host.Replaced);
        Assert.Null(host.Drops[^1]);
        Assert.False(drag.Release());
    }

    [Fact]
    public void A_click_or_a_missing_file_never_drags()
    {
        var host = new Host();
        var drag = new AudioDragGesture(host);
        drag.Press(Song());
        Assert.False(drag.Release());
        drag.Press(Song(SourceStatus.Missing));
        Assert.False(drag.Move(50, "c"));
        Assert.False(drag.Release());
        Assert.Empty(host.Drops);
        Assert.Null(host.Replaced);
    }
}
