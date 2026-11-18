using ManeFfmpeg.Core.Editing;
using ManeFfmpeg.Core.Model;
using ManeFfmpeg.Core.Mpv;

namespace ManeFfmpeg.Core.Tests;

public class TimelineEditorTests
{
    private static Clip C(string id, double inS, double outS, double offsetMs = 0) => new()
    {
        Id = id, SourceId = "s", In = TimeSpan.FromSeconds(inS), Out = TimeSpan.FromSeconds(outS),
        AudioOffset = TimeSpan.FromMilliseconds(offsetMs),
    };

    private static Timeline T(params Clip[] clips) => new([.. clips]);

    private static readonly CutGrid Frames30 = new(TimeSpan.FromSeconds(20), new Rational(30, 1));

    private static Func<string, CutGrid?> Grid(CutGrid? grid) => _ => grid;

    [Fact]
    public void Split_keeps_offset_on_both_halves()
    {
        var (t, right) = TimelineEditor.Split(T(C("a", 0, 10, 120)), TimeSpan.FromSeconds(4), Grid(null));
        Assert.NotNull(right);
        Assert.Equal(2, t.Clips.Count);
        Assert.Equal(TimeSpan.FromSeconds(4), t.Clips[0].Out);
        Assert.Equal(TimeSpan.FromSeconds(4), t.Clips[1].In);
        Assert.All(t.Clips, c => Assert.Equal(TimeSpan.FromMilliseconds(120), c.AudioOffset));
    }

    [Fact]
    public void Split_at_edge_does_nothing()
    {
        var original = T(C("a", 0, 10));
        var (t, right) = TimelineEditor.Split(original, TimeSpan.FromMilliseconds(10), Grid(null));
        Assert.Null(right);
        Assert.Same(original, t);
    }

    [Fact]
    public void Split_maps_the_playhead_into_the_source_and_snaps()
    {
        // Playhead 3.51 s is 1.51 s into the second clip, which starts at source 10 s.
        var timeline = T(C("a", 0, 2), C("b", 10, 15));
        var (byFrame, _) = TimelineEditor.Split(timeline, TimeSpan.FromSeconds(3.51), Grid(Frames30));
        Assert.Equal(TimeFormat.FrameStart(345, new Rational(30, 1)), byFrame.Clips[2].In);

        var keyframes = Frames30 with { Keyframes = [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(12)] };
        var (byKeyframe, _) = TimelineEditor.Split(timeline, TimeSpan.FromSeconds(3.51), Grid(keyframes));
        Assert.Equal(TimeSpan.FromSeconds(12), byKeyframe.Clips[1].Out);
        Assert.Equal(TimeSpan.FromSeconds(12), byKeyframe.Clips[2].In);
    }

    [Theory]
    [InlineData("b", 7, 4)]
    [InlineData("c", 3, 3)]
    [InlineData("b", 3.5, 2)]
    [InlineData("b", 5, 2)]
    [InlineData("a", 2, 0)]
    [InlineData("ac", 9, 3)]
    public void Deleting_keeps_the_time_on_the_same_moment(string deleted, double at, double expected)
    {
        // a, b and c start at 0, 2 and 5 s; the timeline ends at 9 s.
        var t = T(C("a", 0, 2), C("b", 0, 3), C("c", 0, 4));
        var ids = deleted.Select(id => id.ToString()).ToHashSet();
        Assert.Equal(TimeSpan.FromSeconds(expected), TimelineEditor.TimeAfterDelete(t, ids, TimeSpan.FromSeconds(at)));
    }

    [Fact]
    public void Inspector_ranges_snap_like_trims()
    {
        var keyframes = Frames30 with { Keyframes = [TimeSpan.Zero, TimeSpan.FromSeconds(3)] };
        var t = TimelineEditor.SetRange(T(C("a", 2, 5)), "a", TimeSpan.FromSeconds(2.6), TimeSpan.FromSeconds(7.01), Grid(keyframes));
        Assert.Equal(TimeSpan.FromSeconds(3), t.Clips[0].In);
        Assert.Equal(TimeFormat.FrameStart(210, new Rational(30, 1)), t.Clips[0].Out);
        Assert.Equal(TimeSpan.FromSeconds(3), TimelineEditor.TrimIn(T(C("a", 2, 5)), "a", TimeSpan.FromSeconds(2.9), Grid(keyframes)).Clips[0].In);
    }

    [Theory]
    [InlineData(0, "bcad")]
    [InlineData(4, "adbc")]
    [InlineData(1, "abcd")]
    [InlineData(3, "abcd")]
    public void Move_keeps_relative_order(int insertIndex, string expected)
    {
        var t = T(C("a", 0, 1), C("b", 1, 2), C("c", 2, 3), C("d", 3, 4));
        var moved = TimelineEditor.Move(t, new HashSet<string> { "b", "c" }, insertIndex);
        var order = string.Concat(moved.Clips.Select(c => c.Id));
        Assert.Equal(expected, order);
    }

    [Fact]
    public void Trim_is_clamped_to_source_and_minimum_length()
    {
        var t = T(C("a", 2, 5));
        Assert.Equal(TimeSpan.Zero, TimelineEditor.TrimIn(t, "a", TimeSpan.FromSeconds(-3), Grid(null)).Clips[0].In);
        Assert.Equal(TimeSpan.FromSeconds(5) - TimelineEditor.MinClipDuration,
            TimelineEditor.TrimIn(t, "a", TimeSpan.FromSeconds(9), Grid(null)).Clips[0].In);
        Assert.Equal(TimeSpan.FromSeconds(8), TimelineEditor.TrimOut(t, "a", TimeSpan.FromSeconds(20), Grid(new CutGrid(TimeSpan.FromSeconds(8)))).Clips[0].Out);
        // Unanalyzed source: no snapping, and the clip can't grow past its out point.
        Assert.Equal(TimeSpan.FromSeconds(5), TimelineEditor.TrimOut(t, "a", TimeSpan.FromSeconds(9), Grid(null)).Clips[0].Out);
        Assert.Equal(TimeFormat.FrameStart(127, new Rational(30, 1)),
            TimelineEditor.TrimOut(t, "a", TimeSpan.FromSeconds(4.23), Grid(Frames30)).Clips[0].Out);
    }

    [Fact]
    public void Locate_finds_clip_and_start()
    {
        var t = T(C("a", 0, 2), C("b", 10, 13), C("c", 0, 1));
        Assert.Equal((0, TimeSpan.Zero), t.Locate(TimeSpan.FromSeconds(-1))!.Value);
        Assert.Equal((0, TimeSpan.Zero), t.Locate(TimeSpan.FromSeconds(1.999))!.Value);
        Assert.Equal((1, TimeSpan.FromSeconds(2)), t.Locate(TimeSpan.FromSeconds(2))!.Value);
        Assert.Equal((1, TimeSpan.FromSeconds(2)), t.Locate(TimeSpan.FromSeconds(3))!.Value);
        Assert.Equal((2, TimeSpan.FromSeconds(5)), t.Locate(TimeSpan.FromSeconds(99))!.Value);
        Assert.Equal(TimeSpan.FromSeconds(6), t.Duration);
        Assert.Null(Timeline.Empty.Locate(TimeSpan.Zero));
        Assert.Equal(TimeSpan.Zero, Timeline.Empty.Duration);
    }

    [Fact]
    public void Bounds_follow_a_changed_clip_list()
    {
        var t = T(C("a", 0, 2), C("b", 10, 13));
        Assert.Equal([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)], t.Bounds.ToArray());
        var longer = t with { Clips = t.Clips.Add(C("c", 0, 4)) };
        Assert.Equal(TimeSpan.FromSeconds(9), longer.Duration);
        Assert.Equal(TimeSpan.FromSeconds(5), longer.StartOf(2));
        Assert.Equal(TimeSpan.FromSeconds(5), t.Duration);
    }

    [Fact]
    public void Undo_collapses_a_gesture_into_one_step()
    {
        var stack = new UndoStack<Timeline>(T(C("a", 0, 10)));
        stack.BeginGesture();
        stack.ReplaceCurrent(TimelineEditor.NudgeAudioOffset(stack.Current, new HashSet<string> { "a" }, TimeSpan.FromMilliseconds(10)));
        stack.ReplaceCurrent(TimelineEditor.NudgeAudioOffset(stack.Current, new HashSet<string> { "a" }, TimeSpan.FromMilliseconds(10)));
        stack.EndGesture();
        Assert.Equal(TimeSpan.FromMilliseconds(20), stack.Current.Clips[0].AudioOffset);
        Assert.True(stack.Undo());
        Assert.Equal(TimeSpan.Zero, stack.Current.Clips[0].AudioOffset);
        Assert.False(stack.CanUndo);
        Assert.True(stack.Redo());
        Assert.Equal(TimeSpan.FromMilliseconds(20), stack.Current.Clips[0].AudioOffset);
    }

    [Fact]
    public void RewindTo_drops_the_steps_since_and_keeps_the_ones_before()
    {
        Timeline Nudge(Timeline t) => TimelineEditor.NudgeAudioOffset(t, new HashSet<string> { "a" }, TimeSpan.FromMilliseconds(10));
        var initial = T(C("a", 0, 10));
        var stack = new UndoStack<Timeline>(initial);
        stack.Commit(Nudge(stack.Current));
        var checkpoint = stack.Current;
        stack.Commit(Nudge(stack.Current));
        stack.Commit(Nudge(stack.Current));

        Assert.True(stack.RewindTo(checkpoint));
        Assert.Same(checkpoint, stack.Current);
        Assert.False(stack.CanRedo);
        Assert.True(stack.Undo());
        Assert.Same(initial, stack.Current);
        Assert.False(stack.CanUndo);

        // Undone past the checkpoint: it's no longer in the history to undo.
        Assert.False(stack.RewindTo(checkpoint));
        Assert.Same(initial, stack.Current);
        Assert.True(stack.CanRedo);
    }
}

public class TimelineSnapTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static Clip C(string id, double inS, double outS, double offsetS = 0) => new()
    {
        Id = id, SourceId = "s", In = S(inS), Out = S(outS), AudioOffset = S(offsetS),
    };

    private static TimeSpan? Snap(Timeline timeline, double at, double radius, Func<Clip, TimeSpan?> audioEnd) =>
        TimelineSnap.Nearest(timeline, S(at), S(radius), audioEnd);

    [Fact]
    public void Snaps_to_the_nearest_edge_within_the_radius_only()
    {
        var timeline = new Timeline([C("a", 0, 2), C("b", 0, 2)]);
        Func<Clip, TimeSpan?> none = _ => null;
        Assert.Equal(S(0), Snap(timeline, 0.05, 0.1, none));
        Assert.Equal(S(2), Snap(timeline, 1.95, 0.1, none));
        Assert.Equal(S(4), Snap(timeline, 4.08, 0.1, none));
        Assert.Null(Snap(timeline, 1, 0.1, none));
        Assert.Null(Snap(timeline, 4.2, 0.1, none));
    }

    [Fact]
    public void Audio_edges_inside_a_clip_compete_with_its_edges()
    {
        // Audio heard 0.1 s late from a file ending at 1.5 s: it starts 0.1 s and runs out 1.6 s into the 2 s clip.
        var timeline = new Timeline([C("a", 0, 2, offsetS: 0.1)]);
        Assert.Equal(S(0.1), Snap(timeline, 0.08, 0.3, _ => S(1.5)));
        Assert.Equal(S(1.6), Snap(timeline, 1.75, 0.3, _ => S(1.5)));
        Assert.Equal(S(2), Snap(timeline, 1.85, 0.3, _ => S(1.5)));
        // Not analyzed yet (or no audio): only the clip edges.
        Assert.Equal(S(2), Snap(timeline, 1.75, 0.3, _ => null));
    }

    [Fact]
    public void Only_edges_between_sound_and_silence_count()
    {
        // Heard 0.6 s late from a long file: silence for the first 0.4 s, then sound to the end.
        var late = new Timeline([C("a", 0.2, 2.2, offsetS: 0.6)]);
        Assert.Equal(S(0.4), Snap(late, 0.5, 0.3, _ => S(60)));
        Assert.Null(Snap(late, 1.5, 0.3, _ => S(60)));
        // The file's audio ends before the clip starts: all silence, so no edge inside.
        var silent = new Timeline([C("b", 5, 7)]);
        Assert.Null(Snap(silent, 1, 0.3, _ => S(0.5)));
    }
}

public class TimelineEdlTests
{
    [Fact]
    public void Builds_two_streams_with_slip_and_silence()
    {
        var info = new MediaInfo
        {
            Duration = TimeSpan.FromSeconds(10),
            Video = new VideoStreamInfo { Index = 0, Codec = "h264", Width = 640, Height = 360, FrameRate = new Rational(30, 1) },
            AudioStreams = [new AudioStreamInfo { Index = 1, Codec = "aac", SampleRate = 48000, Channels = 2 }],
        };
        var state = new ProjectState(
            [new SourceMedia { Id = "s", Path = "/m/a,b.mp4" }],
            new Timeline([
                new Clip { Id = "1", SourceId = "s", In = TimeSpan.FromSeconds(2), Out = TimeSpan.FromSeconds(5), AudioOffset = TimeSpan.FromMilliseconds(120) },
                new Clip { Id = "2", SourceId = "s", In = TimeSpan.Zero, Out = TimeSpan.FromSeconds(2), AudioOffset = TimeSpan.FromMilliseconds(120) },
            ]));
        var edl = TimelineEdl.Build(state, new Dictionary<string, MediaInfo> { ["s"] = info }, "/c/silence.mka");
        Assert.Equal(
            "edl://!new_stream;!track_meta,title=mane-video;%10%/m/a,b.mp4,2.000000,3.000000;%10%/m/a,b.mp4,0.000000,2.000000;" +
            "!new_stream;!track_meta,title=mane-audio;%10%/m/a,b.mp4,1.880000,3.000000;" +
            "%14%/c/silence.mka,0.000000,0.120000;%10%/m/a,b.mp4,0.000000,1.880000",
            edl);
    }

    [Fact]
    public void Segments_use_file_timestamps_when_the_container_starts_late()
    {
        var info = new MediaInfo
        {
            Duration = TimeSpan.FromSeconds(10),
            StartTime = TimeSpan.FromSeconds(1.4),
            Video = new VideoStreamInfo { Index = 0, Codec = "h264", Width = 640, Height = 360, FrameRate = new Rational(30, 1) },
            AudioStreams = [new AudioStreamInfo { Index = 1, Codec = "aac", SampleRate = 48000, Channels = 2 }],
        };
        var state = new ProjectState(
            [new SourceMedia { Id = "s", Path = "/m/a.ts" }],
            new Timeline([new Clip { Id = "1", SourceId = "s", In = TimeSpan.FromSeconds(2), Out = TimeSpan.FromSeconds(5), AudioOffset = TimeSpan.FromMilliseconds(-100) }]));
        var edl = TimelineEdl.Build(state, new Dictionary<string, MediaInfo> { ["s"] = info }, "/c/silence.mka");
        Assert.Equal(
            "edl://!new_stream;!track_meta,title=mane-video;%7%/m/a.ts,3.400000,3.000000;" +
            "!new_stream;!track_meta,title=mane-audio;%7%/m/a.ts,3.500000,3.000000",
            edl);
    }
}
