using System.Collections.Immutable;
using System.Globalization;
using ManeFfmpeg.Core.Desktop;
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

public class ArithmeticTests
{
    [Theory]
    [InlineData("0-700", -700)]
    [InlineData(" -700 ", -700)]
    [InlineData("−120", -120)]
    [InlineData("100 + 20.5", 120.5)]
    [InlineData("1,5*2", 3)]
    [InlineData("2*(40+1)", 82)]
    [InlineData("1000/3*3", 1000)]
    [InlineData("10-2-3", 5)]
    [InlineData("-(5-8)", 3)]
    [InlineData("--4", 4)]
    [InlineData("3 × 4 ÷ 2", 6)]
    public void Evaluates_simple_arithmetic(string text, double expected)
    {
        Assert.True(Arithmetic.TryEvaluate(text, out var value));
        Assert.Equal(expected, value, 9);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0-")]
    [InlineData("abc")]
    [InlineData("(1+2")]
    [InlineData("1+2)")]
    [InlineData("1..2")]
    [InlineData("5/0")]
    [InlineData("12 ms")]
    public void Refuses_what_is_not_a_finite_number(string text) =>
        Assert.False(Arithmetic.TryEvaluate(text, out _));
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

    [Fact]
    public void Stray_values_are_refused_because_ffmpeg_would_write_them_as_outputs()
    {
        var ex = Assert.Throws<FormatException>(() => PresetArguments.Parse("-c:v libx264 -f mpegts tcp://127.0.0.1:9"));
        Assert.Contains("tcp://127.0.0.1:9", ex.Message);
        Assert.Throws<FormatException>(() => PresetArguments.Parse("-c copy /tmp/side.mkv"));
        Assert.Equal(["-c:v", "libx264", "-bf", "-1"], PresetArguments.Parse("-c:v libx264 -bf -1").OutputTokens);
    }
}

public class PresetAnalysisTests
{
    [Theory]
    [InlineData("-c:v libx264 -vn", "disables video")]
    [InlineData("-c:v copy -vf scale=640:-2", "Video filters")]
    [InlineData("-c:a copy -af volume=2", "Audio filters")]
    [InlineData("-c:v libx264 extra.mkv", "extra.mkv")]
    [InlineData("-vf 'scale", "quote")]
    public void Unusable_presets_are_errors(string arguments, string expected)
    {
        var analysis = PresetAnalysis.Of(arguments, Preset.DefaultAudioFallback);
        Assert.False(analysis.IsValid);
        Assert.Contains(analysis.Errors, e => e.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Bad_fallback_is_an_error_and_warnings_pass_through()
    {
        var analysis = PresetAnalysis.Of("-y -c:v copy -c:a copy", "-c:a 'aac");
        Assert.Contains(analysis.Errors, e => e.StartsWith("Audio fallback"));
        Assert.Single(analysis.Warnings);
        Assert.True(analysis.VideoCopy);
    }

    [Fact]
    public void Audio_summary_follows_the_planner()
    {
        Assert.Contains("audio offset", PresetAnalysis.Of(Preset.PlaneCopy).AudioSummary("-c:a aac")!);
        Assert.Contains("single clip", PresetAnalysis.Of(Preset.ForStorage).AudioSummary("-c:a aac")!);
        Assert.Contains("dropped", PresetAnalysis.Of("-c:v libx264 -an", "").AudioSummary("")!);
    }

    [Fact]
    public void Planner_refuses_an_invalid_preset_by_name()
    {
        var job = new ExportJob
        {
            State = new ProjectState([new SourceMedia { Id = "s", Path = "/m/a.mp4" }],
                new Timeline([new Clip { Id = "c", SourceId = "s", In = TimeSpan.Zero, Out = TimeSpan.FromSeconds(1) }])),
            Media = new Dictionary<string, MediaInfo>(),
            Preset = Preset.PlaneCopy with { Name = "broken", Arguments = "-c:v copy -vn" },
            OutputPath = "/m/out.mp4",
            WorkDirectory = "/tmp/w",
        };
        var ex = Assert.Throws<ExportPlanException>(() => ExportPlanner.Plan(job));
        Assert.Contains("\"broken\"", ex.Message);
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
}

public class ProjectFileTests
{
    private const string Header = "\"format\": \"mane-ffmpeg-project\", \"version\": 1";

    [Fact]
    public void Missing_lists_and_entries_load_as_empty()
    {
        var empty = ProjectFile.Deserialize($"{{ {Header}, \"sources\": null, \"clips\": null }}", "/p/x.mfvp");
        Assert.Empty(empty.State.Sources);
        Assert.Empty(empty.State.Timeline.Clips);

        var partial = ProjectFile.Deserialize($$"""
            { {{Header}},
              "sources": [null, { "id": "s", "path": "/m/a.mp4" }, { "id": null, "path": "/m/b.mp4" }],
              "clips": [null, { "id": "c", "source": "s", "in": "00:00:01", "out": "00:00:02" }, { "id": "d", "source": null }] }
            """, "/p/x.mfvp");
        Assert.Equal("s", Assert.Single(partial.State.Sources).Id);
        Assert.Equal("c", Assert.Single(partial.State.Timeline.Clips).Id);
    }

    [Fact]
    public void Any_unreadable_file_is_a_project_file_error()
    {
        Assert.Throws<ProjectFileException>(() => ProjectFile.Deserialize($"{{ {Header}, \"clips\": [{{ \"in\": \"soon\" }}] }}", "/p/x.mfvp"));
        Assert.Throws<ProjectFileException>(() => ProjectFile.Deserialize(
            $"{{ {Header}, \"sources\": [{{ \"id\": \"s\", \"path\": \"/gone.mp4\", \"relativePath\": \"a\\u0000b\" }}] }}", "/p/x.mfvp"));
    }

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
        Assert.Equal(["for storage", "harsh", "share", "plane copy"], store.Presets.Select(p => p.Name));
        Assert.Contains("yuv420p10le", store.Presets[0].Arguments);
        Assert.Equal("-c:v libx265 -crf 28 -preset slow -c:a copy", store.Presets[1].Arguments);
    }

    [Fact]
    public void Moved_preset_keeps_its_place_after_reloading()
    {
        var path = Path.Combine(Path.GetTempPath(), $"presets-{Guid.NewGuid():N}.json");
        try
        {
            var store = new PresetStore(path);
            store.Move(Preset.PlaneCopy.Id, 0);
            Assert.Equal(["plane copy", "for storage", "harsh", "share"], store.Presets.Select(p => p.Name));
            // Past the end means last.
            store.Move(Preset.ForStorage.Id, 99);
            Assert.Equal(["plane copy", "harsh", "share", "for storage"], new PresetStore(path).Presets.Select(p => p.Name));
        }
        finally
        {
            File.Delete(path);
        }
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
            Assert.Equal(["for storage", "harsh", "share", "mine"], store.Presets.Select(p => p.Name));
            Assert.Equal(edited ? storageArgs : Preset.ForStorage.Arguments, store.Presets[0].Arguments);
            Assert.Contains("\"version\": 3", File.ReadAllText(path));

            // A second load doesn't migrate again (an edited preset stays as the user left it).
            Assert.Equal(store.Presets.Select(p => p.Arguments), new PresetStore(path).Presets.Select(p => p.Arguments));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Version_2_file_gains_share_after_harsh_and_keeps_the_rest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"presets-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            { "version": 2, "presets": [
              { "id": "builtin-for-storage", "name": "for storage", "arguments": "-c:v libx265 -crf 20 -c:a copy" },
              { "id": "builtin-harsh", "name": "harsh", "arguments": "-c:v libx265 -crf 28 -c:a copy" },
              { "id": "mine", "name": "mine", "arguments": "-c:v libx264" } ] }
            """);
        try
        {
            var store = new PresetStore(path);
            Assert.Equal(["for storage", "harsh", "share", "mine"], store.Presets.Select(p => p.Name));
            Assert.Equal("-c:v libx265 -crf 20 -c:a copy", store.Presets[0].Arguments);
            Assert.Contains("\"version\": 3", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Share_preset_is_valid_and_encodes_audio()
    {
        var analysis = PresetAnalysis.Of(Preset.Share);
        Assert.True(analysis.IsValid);
        Assert.Empty(analysis.Warnings);
        Assert.Equal("libx264", analysis.Arguments!.VideoCodec);
        Assert.False(analysis.Arguments.AudioCopy);
    }

    [Fact]
    public void An_unwritable_folder_keeps_changes_in_memory_and_reports_it()
    {
        if (OperatingSystem.IsWindows())
            return;
        var dir = Directory.CreateTempSubdirectory("presets-ro");
        try
        {
            var store = new PresetStore(Path.Combine(dir.FullName, "presets.json"));
            File.SetUnixFileMode(dir.FullName, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            store.Save(Preset.PlaneCopy with { Id = "mine", Name = "mine" });
            Assert.Contains(store.Presets, p => p.Id == "mine");
            Assert.NotNull(store.SaveError);

            var settings = new SettingsStore(Path.Combine(dir.FullName, "settings.json"));
            settings.AddRecentProject("/p/x.mfvp");
            Assert.Equal(["/p/x.mfvp"], settings.Current.RecentProjects);
        }
        finally
        {
            File.SetUnixFileMode(dir.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Settings_saved_before_a_setting_existed_get_its_default()
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "hardwareDecoding": false, "theme": "Light" }""");
        try
        {
            var settings = new SettingsStore(path).Current;
            Assert.False(settings.HardwareDecoding);
            Assert.Equal(ThemeChoice.Light, settings.Theme);
            Assert.True(settings.Notifications);
            Assert.True(settings.AskToInstallAtStartup);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Missing_fields_get_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"presets-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "version": 3, "presets": [ { "id": "mine", "name": "mine", "arguments": "-c:v libx264" } ] }""");
        try
        {
            var preset = Assert.Single(new PresetStore(path).Presets);
            Assert.Equal(Preset.DefaultAudioFallback, preset.AudioFallbackArguments);
            Assert.Equal("", preset.Extension);
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

public class ProjectFileAssociationTests
{
    [Theory]
    [InlineData("/opt/mane/ManeFfmpeg", "\"/opt/mane/ManeFfmpeg\"")]
    [InlineData("/home/a b/Mane FFmpeg/ManeFfmpeg", "\"/home/a b/Mane FFmpeg/ManeFfmpeg\"")]
    [InlineData("/x/$HOME`id`/100%/ManeFfmpeg", "\"/x/\\\\$HOME\\\\`id\\\\`/100%%/ManeFfmpeg\"")]
    [InlineData("/x/a\\b\"c", "\"/x/a\\\\\\\\b\\\\\"c\"")]
    public void Quotes_the_executable_for_a_desktop_entry(string path, string expected) =>
        Assert.Equal(expected, ProjectFileAssociation.ExecArgument(path));

    [Fact]
    public void Removes_only_the_project_type_from_mimeapps_list()
    {
        const string list = "[Default Applications]\nvideo/mp4=vlc.desktop\napplication/x-mane-ffmpeg-project=mane-ffmpeg.desktop\n\n" +
                            "[Added Associations]\napplication/x-mane-ffmpeg-project=mane-ffmpeg.desktop;\napplication/x-mane-ffmpeg-project-old=x.desktop;\n";
        Assert.Equal("[Default Applications]\nvideo/mp4=vlc.desktop\n\n[Added Associations]\napplication/x-mane-ffmpeg-project-old=x.desktop;\n",
            ProjectFileAssociation.WithoutAssociations(list, "application/x-mane-ffmpeg-project"));
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
