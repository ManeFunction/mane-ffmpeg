using System.Runtime.Versioning;
using ManeFfmpeg.Core.Ffmpeg;
using ManeFfmpeg.Core.Model;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.Core.Tests;

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

public class PresetStoreTests
{
    /// <summary>The video presets' names in order (the audio presets have their own tab and order).</summary>
    private static IEnumerable<string> VideoNames(PresetStore store) => store.Presets.Where(p => p.Kind == PresetKind.Video).Select(p => p.Name);

    private static IEnumerable<string> AudioNames(PresetStore store) => store.Presets.Where(p => p.Kind == PresetKind.Audio).Select(p => p.Name);

    [Fact]
    public void New_store_has_the_audio_built_ins_with_extract_audio_first()
    {
        var store = new PresetStore(Path.Combine(Path.GetTempPath(), $"presets-{Guid.NewGuid():N}.json"));
        Assert.Equal(["extract audio (as is)", "m4a (AAC 256k)", "mp3 (V0)"], AudioNames(store));
        Assert.All(Preset.BuiltIns, p => Assert.True(PresetAnalysis.Of(p).IsValid, p.Name));
    }

    [Fact]
    public void Version_4_file_gains_the_audio_built_ins_after_the_users_presets()
    {
        var path = Path.Combine(Path.GetTempPath(), $"presets-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            { "version": 4, "presets": [
              { "id": "builtin-plane-copy", "name": "copy (w/o encode)", "arguments": "-c:v copy -c:a copy" },
              { "id": "mine", "name": "mine", "arguments": "-c:v libx264" } ] }
            """);
        try
        {
            var store = new PresetStore(path);
            Assert.Equal(["copy (w/o encode)", "mine", "extract audio (as is)", "m4a (AAC 256k)", "mp3 (V0)"], store.Presets.Select(p => p.Name));
            var text = File.ReadAllText(path);
            Assert.Contains("\"version\": 5", text);
            Assert.Contains("\"kind\": \"Audio\"", text);
            // The kind survives a reload, and a second load adds nothing.
            var reloaded = new PresetStore(path);
            Assert.Equal(store.Presets, reloaded.Presets);
            Assert.Equal(PresetKind.Audio, reloaded.Find(Preset.ExtractAudio.Id)?.Kind);
            Assert.Equal(PresetKind.Video, reloaded.Find("mine")?.Kind);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Moving_counts_only_presets_of_the_same_kind()
    {
        var path = Path.Combine(Path.GetTempPath(), $"presets-{Guid.NewGuid():N}.json");
        try
        {
            var store = new PresetStore(path);
            store.Move(Preset.AudioMp3.Id, 0);
            Assert.Equal(["mp3 (V0)", "extract audio (as is)", "m4a (AAC 256k)"], AudioNames(store));
            store.Move(Preset.AudioMp3.Id, 99);
            Assert.Equal(["extract audio (as is)", "m4a (AAC 256k)", "mp3 (V0)"], AudioNames(store));
            Assert.Equal(VideoNames(new PresetStore(Path.Combine(Path.GetTempPath(), $"presets-{Guid.NewGuid():N}.json"))), VideoNames(store));
        }
        finally
        {
            File.Delete(path);
        }
    }
    [Fact]
    public void New_store_has_built_ins_with_for_storage_first()
    {
        var path = Path.Combine(Path.GetTempPath(), $"presets-{Guid.NewGuid():N}.json");
        var store = new PresetStore(path);
        Assert.Equal(["for storage (balanced)", "for share (compatibility)", "harsh compression", "copy (w/o encode)", "copy to mp4 (w/o encode)"], VideoNames(store));
        Assert.Contains("yuv420p10le", store.Presets[0].Arguments);
        Assert.Equal("-c:v libx265 -crf 28 -preset slow -c:a copy", store.Presets[2].Arguments);
    }

    [Fact]
    public void Moved_preset_keeps_its_place_after_reloading()
    {
        var path = Path.Combine(Path.GetTempPath(), $"presets-{Guid.NewGuid():N}.json");
        try
        {
            var store = new PresetStore(path);
            store.Move(Preset.PlaneCopy.Id, 0);
            Assert.Equal(["copy (w/o encode)", "for storage (balanced)", "for share (compatibility)", "harsh compression", "copy to mp4 (w/o encode)"], VideoNames(store));
            // Past the end means last.
            store.Move(Preset.ForStorage.Id, 99);
            Assert.Equal(["copy (w/o encode)", "for share (compatibility)", "harsh compression", "copy to mp4 (w/o encode)", "for storage (balanced)"], VideoNames(new PresetStore(path)));
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
            Assert.Equal(["for storage (balanced)", "for share (compatibility)", "harsh compression", "mine", "copy to mp4 (w/o encode)"], VideoNames(store));
            Assert.Equal(edited ? storageArgs : Preset.ForStorage.Arguments, store.Presets[0].Arguments);
            Assert.Contains("\"version\": 5", File.ReadAllText(path));

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
            Assert.Equal(["for storage (balanced)", "for share (compatibility)", "harsh compression", "mine", "copy to mp4 (w/o encode)"], VideoNames(store));
            Assert.Equal("-c:v libx265 -crf 20 -c:a copy", store.Presets[0].Arguments);
            Assert.Contains("\"version\": 5", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Version_3_file_renames_built_ins_unless_edited_and_moves_share_second()
    {
        var path = Path.Combine(Path.GetTempPath(), $"presets-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            { "version": 3, "presets": [
              { "id": "builtin-for-storage", "name": "my storage", "arguments": "-c:v libx265 -crf 20 -c:a copy" },
              { "id": "builtin-harsh", "name": "harsh", "arguments": "-c:v libx265 -crf 28 -c:a copy" },
              { "id": "builtin-share", "name": "share", "arguments": "-c:v libx264" },
              { "id": "builtin-plane-copy", "name": "plane copy", "arguments": "-c:v copy -c:a copy" },
              { "id": "mine", "name": "plane copy but mp4", "arguments": "-c:v copy -c:a copy" } ] }
            """);
        try
        {
            var store = new PresetStore(path);
            Assert.Equal(
                ["my storage", "for share (compatibility)", "harsh compression", "copy (w/o encode)", "copy to mp4 (w/o encode)"],
                VideoNames(store));
            Assert.Contains("\"version\": 5", File.ReadAllText(path));
            Assert.Equal(Preset.CopyToMp4.Id, store.Presets.Last(p => p.Kind == PresetKind.Video).Id);
            // A project saved with the old preset still finds it, though its id changed.
            var saved = new Preset { Id = "mine", Name = "plane copy but mp4", Arguments = "-c:v copy -c:a copy", Extension = "mp4" };
            store.Save(store.Presets.Last(p => p.Kind == PresetKind.Video) with { Extension = "mp4" });
            Assert.Equal(Preset.CopyToMp4.Id, store.FindFor("mine", saved)?.Id);
            // Other options aren't taken for it.
            Assert.Null(store.FindFor("mine", saved with { Arguments = "-c:v copy -c:a copy -map 0" }));
            Assert.Null(store.FindFor("mine", null));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Version_3_file_without_a_copy_to_mp4_preset_gains_the_built_in_after_copy()
    {
        var path = Path.Combine(Path.GetTempPath(), $"presets-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            { "version": 3, "presets": [
              { "id": "builtin-plane-copy", "name": "plane copy", "arguments": "-c:v copy -c:a copy" },
              { "id": "mine", "name": "mine", "arguments": "-c:v libx264" } ] }
            """);
        try
        {
            Assert.Equal(["copy (w/o encode)", "copy to mp4 (w/o encode)", "mine"], VideoNames(new PresetStore(path)));
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

    [UnixFact]
    [UnsupportedOSPlatform("windows")]
    public void An_unwritable_folder_keeps_changes_in_memory_and_reports_it()
    {
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
        File.WriteAllText(path, """{ "version": 4, "presets": [ { "id": "mine", "name": "mine", "arguments": "-c:v libx264" } ] }""");
        try
        {
            var preset = Assert.Single(new PresetStore(path).Presets, p => p.Id == "mine");
            Assert.Equal(PresetKind.Video, preset.Kind);
            Assert.Equal(Preset.DefaultAudioFallback, preset.AudioFallbackArguments);
            Assert.Equal("", preset.Extension);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
