using ManeFfmpeg.Core.Editing;
using ManeFfmpeg.Core.Model;
using ManeFfmpeg.Core.Mpv;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.Core.Tests;

public class AudioPlacementTests
{
    [Theory]
    // video start, length, offset ms, audio end → lead, read from, read length, tail
    [InlineData(2.0, 3.0, 120, 10.0, 0.0, 1.88, 3.0, 0.0)] // inside the file
    [InlineData(0.0, 2.0, 120, 10.0, 0.12, 0.0, 1.88, 0.0)] // slipped before the file start
    [InlineData(8.0, 2.0, -500, 10.0, 0.0, 8.5, 1.5, 0.5)] // slipped past the audio end
    [InlineData(0.0, 1.0, 2000, 10.0, 1.0, 0.0, 0.0, 0.0)] // wholly before the file
    [InlineData(9.0, 1.0, -2000, 10.0, 0.0, 11.0, 0.0, 1.0)] // wholly past the audio end
    [InlineData(1.0, 4.0, 3000, 2.5, 2.0, 0.0, 2.0, 0.0)] // stream copy start, short audio
    public void Places_slipped_audio(double videoStart, double length, double offsetMs, double audioEnd,
        double lead, double readFrom, double readLength, double tail)
    {
        var p = AudioPlacement.For(TimeSpan.FromSeconds(videoStart), TimeSpan.FromSeconds(length),
            TimeSpan.FromMilliseconds(offsetMs), TimeSpan.FromSeconds(audioEnd));
        Assert.Equal(lead, p.Lead.TotalSeconds, 6);
        Assert.Equal(readFrom, p.ReadFrom.TotalSeconds, 6);
        Assert.Equal(readLength, p.ReadLength.TotalSeconds, 6);
        Assert.Equal(tail, p.Tail.TotalSeconds, 6);
        Assert.Equal(readLength == 0, p.IsSilent);
    }
}

/// <summary>What a clip sounds like: the one decision the preview, the export, the timeline and snapping all render.</summary>
public class AudioLayoutTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static Clip Own(double inS, double outS, double offsetS) =>
        new() { Id = "c", SourceId = "s", In = S(inS), Out = S(outS), AudioOffset = S(offsetS) };

    private static Clip Replaced(double inS, double outS, double anchorS, double offsetS, bool loop) =>
        Own(inS, outS, 0) with { ReplacedAudio = new AudioReplacement { FileId = "f", Anchor = S(anchorS), Offset = S(offsetS), Loop = loop } };

    private static AudioLayout Layout(Clip clip, double audioEnd) => AudioLayout.For(clip, clip.In, clip.Duration, S(audioEnd));

    [Fact]
    public void Placed_audio_is_one_read_with_silence_around_it()
    {
        // Heard 1.5 s late: half a second of silence, then the file from its start.
        var layout = Layout(Own(1, 4, 1.5), 10);
        Assert.IsType<AudioLayout.Placed>(layout);
        Assert.Equal([new AudioSpan(S(0), S(0.5), null), new AudioSpan(S(0.5), S(2.5), S(0))], layout.Spans());
        Assert.Equal([S(0.5)], layout.Edges(S(0), S(3)));
        Assert.Empty(layout.Edges(S(1), S(3)));
        Assert.Equal(S(-0.5), layout.FileTime(S(0)));
        Assert.Equal(S(1.5), layout.FileTime(S(2)));
    }

    [Fact]
    public void Audio_wholly_past_the_file_is_one_silence()
    {
        var layout = Layout(Own(5, 7, 0), 0.5);
        Assert.Equal([new AudioSpan(S(0), S(2), null)], layout.Spans());
        Assert.Empty(layout.Edges(S(0), S(2)));
    }

    [Fact]
    public void Looped_audio_repeats_without_silence_and_marks_its_seams()
    {
        // A 3 s file slipped 1.5 s: it starts 2.5 s in and starts again 0.5 s, 3.5 s and 6.5 s into the clip.
        var layout = Layout(Replaced(1, 8, anchorS: 1, offsetS: 0.5, loop: true), 3);
        Assert.IsType<AudioLayout.Looped>(layout);
        Assert.Equal([
            new AudioSpan(S(0), S(0.5), S(2.5)), new AudioSpan(S(0.5), S(3), S(0)),
            new AudioSpan(S(3.5), S(3), S(0)), new AudioSpan(S(6.5), S(0.5), S(0)),
        ], layout.Spans());
        Assert.Equal([S(0.5), S(3.5), S(6.5)], layout.Edges(S(0), S(7)));
        Assert.Equal([S(3.5)], layout.Edges(S(3), S(4)));
        Assert.Equal(S(2.5), layout.FileTime(S(0)));
        Assert.Equal(S(0.5), layout.FileTime(S(4)));
    }

    [Fact]
    public void Looped_audio_that_does_not_wrap_is_one_read_not_silence()
    {
        // A split half inside the second repetition of a 3 s file.
        var layout = Layout(Replaced(4.5, 5.5, anchorS: 1, offsetS: 0, loop: true), 3);
        Assert.Equal([new AudioSpan(S(0), S(1), S(0.5))], layout.Spans());
        Assert.Empty(layout.Edges(S(0), S(1)));
    }

    [Fact]
    public void Too_short_files_and_loop_off_play_once()
    {
        Assert.IsType<AudioLayout.Placed>(Layout(Replaced(0, 2, 0, 0, loop: true), 0.05));
        Assert.IsType<AudioLayout.Placed>(Layout(Replaced(0, 2, 0, 0, loop: false), 3));
    }

    [Fact]
    public void Heard_audio_is_the_replacement_or_the_clip_s_own_once_analyzed()
    {
        var video = new MediaInfo { Duration = S(10), AudioStreams = [new AudioStreamInfo { Index = 1, Codec = "aac", SampleRate = 48000, Channels = 2 }] };
        var tone = new MediaInfo { Duration = S(3), AudioStreams = [new AudioStreamInfo { Index = 0, Codec = "aac", SampleRate = 44100, Channels = 2, Duration = S(2.5) }] };
        var state = new ProjectState([new SourceMedia { Id = "s", Path = "/m/a.mp4" }], new Timeline([]), [new SourceMedia { Id = "f", Path = "/m/t.m4a" }]);
        var media = new Dictionary<string, MediaInfo> { ["s"] = video, ["f"] = tone };

        Assert.Equal("s", state.HeardAudio(Own(0, 1, 0), media.GetValueOrDefault)!.File.Id);
        var replaced = state.HeardAudio(Replaced(0, 1, 0, 0, false), media.GetValueOrDefault)!;
        Assert.Equal(("f", S(2.5)), (replaced.File.Id, replaced.End));
        Assert.Null(state.HeardAudio(Replaced(0, 1, 0, 0, false), new Dictionary<string, MediaInfo> { ["s"] = video }.GetValueOrDefault));
        Assert.Null(state.HeardAudio(Own(0, 1, 0) with { SourceId = "gone" }, media.GetValueOrDefault));
        Assert.Null(state.HeardAudio(Own(0, 1, 0), new Dictionary<string, MediaInfo> { ["s"] = video with { AudioStreams = [] } }.GetValueOrDefault));
    }
}

public class AudioLoopTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Theory]
    // video start, length, slip, period → phase, wraps
    [InlineData(1.0, 1.0, 1.0, 3.0, 0.0, false)] // starts where it was dropped
    [InlineData(1.0, 7.0, 1.0, 3.0, 0.0, true)] // longer than the file
    [InlineData(1.0, 2.0, 1.5, 3.0, 2.5, true)] // slipped later: the lead comes from the file's end
    [InlineData(4.0, 1.0, 1.0, 3.0, 0.0, false)] // a split half, one period later
    [InlineData(1.0, 1.5, -1.0, 3.0, 2.0, true)] // slipped earlier, into the file
    public void Loops_from_the_slipped_phase(double videoStart, double length, double slip, double period, double phase, bool wraps)
    {
        var loop = AudioLoop.For(S(videoStart), S(length), S(slip), S(period));
        Assert.Equal(phase, loop.Phase.TotalSeconds, 6);
        Assert.Equal(wraps, loop.Wraps);
        Assert.Equal(S(length), loop.Pieces().Aggregate(TimeSpan.Zero, (sum, p) => sum + p.Length));
    }

    [Fact]
    public void Pieces_run_to_the_end_then_whole_files_then_the_rest()
    {
        var pieces = new AudioLoop(S(2.5), S(3), S(7)).Pieces().ToList();
        Assert.Equal([(S(2.5), S(0.5)), (S(0), S(3)), (S(0), S(3)), (S(0), S(0.5))], pieces);
        Assert.False(AudioLoop.CanLoop(TimeSpan.FromMilliseconds(50)));
    }
}

public class AudioReplacementTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static Clip C(string id, double inS, double outS, double offsetMs = 0) => new()
    {
        Id = id, SourceId = "s", In = S(inS), Out = S(outS), AudioOffset = TimeSpan.FromMilliseconds(offsetMs),
    };

    private static HashSet<string> Ids(params string[] ids) => [.. ids];

    [Fact]
    public void Replacing_starts_the_file_at_the_in_point_and_keeps_the_own_offset()
    {
        var t = TimelineEditor.ReplaceAudio(new Timeline([C("a", 2, 8, 120)]), "a", "f");
        var clip = t.Clips[0];
        Assert.Equal(new AudioReplacement { FileId = "f", Anchor = S(2) }, clip.ReplacedAudio);
        Assert.Equal(S(2), clip.AudioSlip);
        Assert.Equal(TimeSpan.Zero, clip.AudioIn);
        Assert.Equal(TimeSpan.Zero, clip.ActiveAudioOffset);

        var nudged = TimelineEditor.NudgeAudioOffset(t, Ids("a"), S(90));
        Assert.Equal(S(90), nudged.Clips[0].ReplacedAudio!.Offset);
        Assert.Equal(TimeSpan.FromMilliseconds(120), nudged.Clips[0].AudioOffset);

        var restored = TimelineEditor.RestoreOriginalAudio(nudged, Ids("a"));
        Assert.Null(restored.Clips[0].ReplacedAudio);
        Assert.Equal(TimeSpan.FromMilliseconds(120), restored.Clips[0].ActiveAudioOffset);
    }

    [Fact]
    public void Limits_differ_for_own_and_replaced_audio()
    {
        var t = new Timeline([C("a", 0, 5), C("b", 5, 9)]);
        t = TimelineEditor.ReplaceAudio(t, "b", "f");
        t = TimelineEditor.SetAudioOffset(t, Ids("a", "b"), TimeSpan.FromHours(30));
        Assert.Equal(TimelineEditor.MaxAudioOffset, t.Clips[0].AudioOffset);
        Assert.Equal(TimelineEditor.MaxReplacedAudioOffset, t.Clips[1].ReplacedAudio!.Offset);
    }

    [Fact]
    public void A_new_drop_resets_the_offset_but_keeps_the_loop()
    {
        var t = TimelineEditor.ReplaceAudio(new Timeline([C("a", 1, 8)]), "a", "f");
        t = TimelineEditor.SetAudioLoop(TimelineEditor.NudgeAudioOffset(t, Ids("a"), S(1)), Ids("a"), true);
        var again = TimelineEditor.ReplaceAudio(t, "a", "g").Clips[0].ReplacedAudio!;
        Assert.Equal(("g", TimeSpan.Zero, true), (again.FileId, again.Offset, again.Loop));
    }

    [Fact]
    public void Splitting_continues_the_sound_on_both_halves()
    {
        var t = TimelineEditor.ReplaceAudio(new Timeline([C("a", 1, 8)]), "a", "f");
        var (split, _) = TimelineEditor.Split(t, S(3), _ => null);
        Assert.All(split.Clips, c => Assert.Equal(S(1), c.ReplacedAudio!.Anchor));
        // The right half starts 3 s into the file, where the left half stopped.
        Assert.Equal(S(3), split.Clips[1].AudioIn);
    }

    [Fact]
    public void Source_wide_offsets_leave_replacements_alone_and_removed_files_restore_clips()
    {
        var t = TimelineEditor.ReplaceAudio(new Timeline([C("a", 0, 5), C("b", 5, 9)]), "b", "f");
        t = TimelineEditor.SetReplacedAudioOffsetForFile(TimelineEditor.SetAudioOffsetForSource(t, "s", S(0.2)), "f", S(4));
        Assert.All(t.Clips, c => Assert.Equal(S(0.2), c.AudioOffset));
        Assert.Equal(S(4), t.Clips[1].ReplacedAudio!.Offset);
        Assert.All(TimelineEditor.RemoveAudioFile(t, "f").Clips, c => Assert.Null(c.ReplacedAudio));
    }

    [Fact]
    public void Project_files_keep_audio_files_and_replacements()
    {
        var dir = Directory.CreateTempSubdirectory("mfvp-audio");
        try
        {
            var song = Path.Combine(dir.FullName, "audio", "song.m4a");
            Directory.CreateDirectory(Path.GetDirectoryName(song)!);
            File.WriteAllText(song, "x");
            var clip = C("c", 1, 4) with { ReplacedAudio = new AudioReplacement { FileId = "f", Anchor = S(1), Offset = S(-2.5), Loop = true } };
            var state = new ProjectState([new SourceMedia { Id = "s", Path = "/m/a.mp4" }], new Timeline([clip]), [new SourceMedia { Id = "f", Path = song, AudioStreamIndex = 1 }]);
            var path = Path.Combine(dir.FullName, "p.mfvp");
            var json = ProjectFile.Serialize(new Project { State = state }, path);
            Assert.Contains("\"version\": 2", json);
            Assert.Contains("\"relativePath\": \"audio/song.m4a\"", json);
            var loaded = ProjectFile.Deserialize(json, path);
            Assert.Equal(state, loaded.State);

            // Without audio files the file stays readable by 1.0.0.
            Assert.Contains("\"version\": 1", ProjectFile.Serialize(new Project { State = state with { AudioFiles = [], Timeline = new Timeline([C("c", 1, 4)]) } }, path));
            // A replacement whose audio file is gone falls back to the clip's own audio.
            var orphan = ProjectFile.Deserialize(json.Replace("\"id\": \"f\"", "\"id\": \"g\""), path);
            Assert.Null(orphan.State.Timeline.Clips[0].ReplacedAudio);
            Assert.Throws<ProjectFileException>(() => ProjectFile.Deserialize(json.Replace("\"version\": 2", "\"version\": 4"), path));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static readonly MediaInfo Video = new()
    {
        Duration = S(10),
        Video = new VideoStreamInfo { Index = 0, Codec = "h264", Width = 640, Height = 360, FrameRate = new Rational(30, 1) },
        AudioStreams = [new AudioStreamInfo { Index = 1, Codec = "aac", SampleRate = 48000, Channels = 2 }],
    };

    private static readonly MediaInfo Tone = new()
    {
        Duration = S(3),
        AudioStreams = [new AudioStreamInfo { Index = 0, Codec = "aac", SampleRate = 44100, Channels = 2, Duration = S(3) }],
    };

    private static ProjectState Replaced(Clip clip) =>
        new ProjectState([new SourceMedia { Id = "s", Path = "/m/a.mp4" }], new Timeline([clip]), [new SourceMedia { Id = "f", Path = "/m/t.m4a" }]);

    [Fact]
    public void Edl_plays_the_replacement_slipped_or_looped()
    {
        var media = new Dictionary<string, MediaInfo> { ["s"] = Video, ["f"] = Tone };
        var later = C("c", 1, 5) with { ReplacedAudio = new AudioReplacement { FileId = "f", Anchor = S(1), Offset = S(0.5) } };
        Assert.EndsWith("!new_stream;!track_meta,title=mane-audio;%14%/c/silence.mka,0.000000,0.500000;%8%/m/t.m4a,0.000000,3.000000;%14%/c/silence.mka,0.000000,0.500000",
            TimelineEdl.Build(Replaced(later), media, "/c/silence.mka"));

        var looped = later with { ReplacedAudio = later.ReplacedAudio! with { Loop = true } };
        Assert.EndsWith("!new_stream;!track_meta,title=mane-audio;%8%/m/t.m4a,2.500000,0.500000;%8%/m/t.m4a,0.000000,3.000000;%8%/m/t.m4a,0.000000,0.500000",
            TimelineEdl.Build(Replaced(looped), media, "/c/silence.mka"));

        // A split half inside a later repetition doesn't wrap, but still plays the file, not silence.
        var half = C("c", 4.5, 5.5) with { ReplacedAudio = new AudioReplacement { FileId = "f", Anchor = S(1), Loop = true } };
        Assert.EndsWith("!new_stream;!track_meta,title=mane-audio;%8%/m/t.m4a,0.500000,1.000000",
            TimelineEdl.Build(Replaced(half), media, "/c/silence.mka"));

        // Not analyzed yet: silence, until the catalog reports it and the preview reloads.
        Assert.DoesNotContain("/m/t.m4a", TimelineEdl.Build(Replaced(later), new Dictionary<string, MediaInfo> { ["s"] = Video }, "/c/silence.mka"));
    }
}
