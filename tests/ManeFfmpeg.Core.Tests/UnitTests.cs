using System.Collections.Immutable;
using System.Globalization;
using ManeFfmpeg.Core.Editing;
using ManeFfmpeg.Core.Ffmpeg;
using ManeFfmpeg.Core.Model;
using ManeFfmpeg.Core.Mpv;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.Core.Tests;

public class TimeFormatTests
{
    [Theory]
    [InlineData(12.3456789, "12.345678")]
    [InlineData(0, "0.000000")]
    [InlineData(3600.5, "3600.500000")]
    [InlineData(-0.12, "-0.120000")]
    public void Seconds_floors_to_microseconds_with_invariant_culture(double seconds, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
        try
        {
            Assert.Equal(expected, TimeFormat.Seconds(TimeSpan.FromTicks((long)Math.Round(seconds * 10_000_000))));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("1:02:03.5", 3723.5)]
    [InlineData("02:03,25", 123.25)]
    [InlineData("42", 42)]
    [InlineData("-0.12", -0.12)]
    public void TryParse_accepts_common_forms(string text, double seconds)
    {
        Assert.True(TimeFormat.TryParse(text, out var time));
        Assert.Equal(seconds, time.TotalSeconds, 6);
    }

    [Fact]
    public void SnapToFrame_lands_on_frame_starts_that_ffmpeg_keeps()
    {
        var ntsc = new Rational(30000, 1001);
        var snapped = TimeFormat.SnapToFrame(TimeSpan.FromSeconds(10.02), ntsc);
        var exact = 300 * 1001 / 30000.0;
        Assert.True(snapped.TotalSeconds >= exact);
        Assert.True(double.Parse(TimeFormat.Seconds(snapped), CultureInfo.InvariantCulture) <= exact + 1e-6);
    }
}

public class PresetArgumentsTests
{
    [Fact]
    public void Detects_codecs_and_copy()
    {
        var a = PresetArguments.Parse("-c:v libx265 -crf 28 -preset slow -c:a copy");
        Assert.Equal("libx265", a.VideoCodec);
        Assert.True(a.AudioCopy);
        Assert.False(a.VideoCopy);
        Assert.Equal(["-c:v", "libx265", "-crf", "28", "-preset", "slow", "-c:a", "copy"], a.OutputTokens);
    }

    [Fact]
    public void Generic_codec_applies_to_both_and_is_narrowed_on_fallback()
    {
        var a = PresetArguments.Parse("-c copy");
        Assert.True(a.VideoCopy && a.AudioCopy);
        Assert.Equal(["-c:v", "copy", "-c:a", "aac"], a.WithAudioCodecReplaced(["-c:a", "aac"]));
    }

    [Fact]
    public void Audio_codec_is_replaced_by_fallback()
    {
        var a = PresetArguments.Parse("-vcodec libx264 -acodec copy -movflags +faststart");
        Assert.Equal(["-vcodec", "libx264", "-movflags", "+faststart", "-c:a", "aac", "-b:a", "256k"],
            a.WithAudioCodecReplaced(["-c:a", "aac", "-b:a", "256k"]));
        Assert.True(a.SetsMovFlags);
    }

    [Fact]
    public void Filters_and_managed_options_are_pulled_out()
    {
        var a = PresetArguments.Parse("-y -i in.mp4 -vf \"scale=1280:-2\" -af 'volume=2' -map 0 -c:v libx264 -loglevel quiet");
        Assert.Equal("scale=1280:-2", a.VideoFilter);
        Assert.Equal("volume=2", a.AudioFilter);
        Assert.Equal(["-c:v", "libx264"], a.OutputTokens);
        Assert.Equal(3, a.Warnings.Count);
    }

    [Fact]
    public void Tokenizer_handles_quotes_and_escapes()
    {
        Assert.Equal(["-metadata", "title=My \"cut\"", "-x", "a b", @"c\d"],
            PresetArguments.Tokenize("-metadata \"title=My \\\"cut\\\"\" -x 'a b' c\\\\d"));
        Assert.Throws<FormatException>(() => PresetArguments.Tokenize("-vf 'scale"));
    }
}

public class TimelineEditorTests
{
    private static Clip C(string id, double inS, double outS, double offsetMs = 0) => new()
    {
        Id = id, SourceId = "s", In = TimeSpan.FromSeconds(inS), Out = TimeSpan.FromSeconds(outS),
        AudioOffset = TimeSpan.FromMilliseconds(offsetMs),
    };

    private static Timeline T(params Clip[] clips) => new([.. clips]);

    [Fact]
    public void Split_keeps_offset_on_both_halves()
    {
        var (t, right) = TimelineEditor.Split(T(C("a", 0, 10, 120)), 0, TimeSpan.FromSeconds(4));
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
        var (t, right) = TimelineEditor.Split(original, 0, TimeSpan.FromMilliseconds(10));
        Assert.Null(right);
        Assert.Same(original, t);
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
        Assert.Equal(TimeSpan.Zero, TimelineEditor.TrimIn(t, "a", TimeSpan.FromSeconds(-3)).Clips[0].In);
        Assert.Equal(TimeSpan.FromSeconds(5) - TimelineEditor.MinClipDuration,
            TimelineEditor.TrimIn(t, "a", TimeSpan.FromSeconds(9)).Clips[0].In);
        Assert.Equal(TimeSpan.FromSeconds(8), TimelineEditor.TrimOut(t, "a", TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(8)).Clips[0].Out);
    }

    [Fact]
    public void Locate_finds_clip_and_start()
    {
        var t = T(C("a", 0, 2), C("b", 10, 13));
        Assert.Equal((1, TimeSpan.FromSeconds(2)), t.Locate(TimeSpan.FromSeconds(3))!.Value);
        Assert.Equal((1, TimeSpan.FromSeconds(2)), t.Locate(TimeSpan.FromSeconds(99))!.Value);
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
}

public class ProjectFileTests
{
    [Fact]
    public void Round_trips_edits_and_layout_with_relative_paths()
    {
        var dir = Directory.CreateTempSubdirectory("mfvp");
        try
        {
            var media = Path.Combine(dir.FullName, "media", "clip 'one'.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(media)!);
            File.WriteAllText(media, "x");
            var project = new Project
            {
                State = new ProjectState(
                    [new SourceMedia { Id = "s1", Path = media, FileSize = 1, AudioStreamIndex = 1 }],
                    new Timeline([new Clip { Id = "c1", SourceId = "s1", In = TimeSpan.FromSeconds(1.5), Out = TimeSpan.FromSeconds(4), AudioOffset = TimeSpan.FromMilliseconds(-80) }])),
                Export = new ExportSettings { PresetId = Preset.ForStorage.Id, PresetSnapshot = Preset.ForStorage, OutputPath = "/tmp/out.mp4" },
                Layout = new ProjectLayout { TimelineZoom = 42.5, Playhead = TimeSpan.FromSeconds(3), Window = new WindowBounds(10, 20, 1200, 800, false) },
            };
            var path = Path.Combine(dir.FullName, "a.mfvp");
            var json = ProjectFile.Serialize(project, path);
            Assert.Contains("\"relativePath\": \"media/clip 'one'.mp4\"", json);

            // Move the whole folder: the relative path still finds the media.
            var moved = Path.Combine(Path.GetTempPath(), "mfvp-moved-" + Guid.NewGuid().ToString("N"));
            Directory.Move(dir.FullName, moved);
            dir = new DirectoryInfo(moved);
            var loaded = ProjectFile.Deserialize(json, Path.Combine(moved, "a.mfvp"));
            Assert.Equal(Path.Combine(moved, "media", "clip 'one'.mp4"), loaded.State.Sources[0].Path);
            Assert.Equal(project.State.Timeline.Clips[0], loaded.State.Timeline.Clips[0]);
            Assert.Equal(1, loaded.State.Sources[0].AudioStreamIndex);
            Assert.Equal(project.Export, loaded.Export);
            Assert.Equal(project.Layout, loaded.Layout);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Rejects_other_json_and_newer_versions()
    {
        Assert.Throws<ProjectFileException>(() => ProjectFile.Deserialize("{\"hello\":1}", "/tmp/x.mfvp"));
        Assert.Throws<ProjectFileException>(() => ProjectFile.Deserialize("{\"format\":\"mane-ffmpeg-project\",\"version\":99}", "/tmp/x.mfvp"));
    }
}

public class PresetStoreTests
{
    [Fact]
    public void New_store_has_built_ins_with_for_storage_first()
    {
        var path = Path.Combine(Path.GetTempPath(), $"presets-{Guid.NewGuid():N}.json");
        var store = new PresetStore(path);
        Assert.Equal(["for storage", "harsh", "plane copy"], store.Presets.Select(p => p.Name));
        Assert.Contains("yuv420p10le", store.Presets[0].Arguments);
        Assert.Equal("-c:v libx265 -crf 28 -preset slow -c:a copy", store.Presets[1].Arguments);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Version_1_file_gets_new_for_storage_unless_edited_and_gains_harsh(bool edited)
    {
        var path = Path.Combine(Path.GetTempPath(), $"presets-{Guid.NewGuid():N}.json");
        var storageArgs = edited ? "-c:v libx265 -crf 20 -c:a copy" : Preset.ForStorageV1Arguments;
        File.WriteAllText(path, $$"""
            { "version": 1, "presets": [
              { "id": "builtin-for-storage", "name": "for storage", "arguments": "{{storageArgs}}", "extension": "mp4", "audioFallbackArguments": "-c:a aac -b:a 256k" },
              { "id": "mine", "name": "mine", "arguments": "-c:v libx264", "extension": "mkv", "audioFallbackArguments": "-c:a aac -b:a 256k" } ] }
            """);
        try
        {
            var store = new PresetStore(path);
            Assert.Equal(["for storage", "harsh", "mine"], store.Presets.Select(p => p.Name));
            Assert.Equal(edited ? storageArgs : Preset.ForStorage.Arguments, store.Presets[0].Arguments);
            Assert.Contains("\"version\": 2", File.ReadAllText(path));

            // A second load doesn't migrate again (an edited preset stays as the user left it).
            Assert.Equal(store.Presets.Select(p => p.Arguments), new PresetStore(path).Presets.Select(p => p.Arguments));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class ProgressParserTests
{
    [Fact]
    public void Emits_snapshot_per_progress_line()
    {
        var parser = new ProgressParser();
        var seen = new List<FfmpegProgress>();
        parser.Updated += seen.Add;
        foreach (var line in "frame=10\nout_time_us=1500000\nspeed=2.5x\nprogress=continue\nout_time_us=N/A\nspeed=N/A\nprogress=end".Split('\n'))
            parser.Feed(line);
        Assert.Equal(2, seen.Count);
        Assert.Equal(TimeSpan.FromSeconds(1.5), seen[0].Position);
        Assert.Equal(2.5, seen[0].Speed);
        Assert.True(seen[1].Finished);
        Assert.Null(seen[1].Speed);
    }
}

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

    [Fact]
    public void Clip_placement_covers_the_whole_clip()
    {
        var clip = new Clip { Id = "c", SourceId = "s", In = TimeSpan.FromSeconds(1), Out = TimeSpan.FromSeconds(4), AudioOffset = TimeSpan.FromSeconds(1.5) };
        Assert.Equal(AudioPlacement.For(clip.In, clip.Duration, clip.AudioOffset, TimeSpan.FromSeconds(10)), clip.AudioPlacement(TimeSpan.FromSeconds(10)));
        Assert.Equal(TimeSpan.FromSeconds(0.5), clip.AudioPlacement(TimeSpan.FromSeconds(10)).Lead);
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
