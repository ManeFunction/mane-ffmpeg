using CliWrap;
using CliWrap.Buffered;
using ManeFfmpeg.Core.Ffmpeg;
using ManeFfmpeg.Core.Media;
using ManeFfmpeg.Core.Model;
using ManeFfmpeg.Core.Platforms;
using ManeFfmpeg.Core.Platforms.Mac;

namespace ManeFfmpeg.Core.Tests;

/// <summary>Finder Quick Actions: the workflows they're made of, and converting whole files next to themselves.</summary>
public class QuickActionTests(FfmpegFixture media) : IClassFixture<FfmpegFixture>
{
    [Fact]
    public void The_command_line_names_the_preset_and_the_files()
    {
        Assert.Null(FileConversion.Parse([]));
        Assert.Null(FileConversion.Parse(["/a.mov"]));
        Assert.Null(FileConversion.Parse([FileConversion.CommandLineFlag]));
        Assert.Null(FileConversion.Parse(["/v/a.mov", FileConversion.CommandLineFlag, "builtin-share"]));
        var request = FileConversion.Parse([FileConversion.CommandLineFlag, "builtin-share", "/v/a.mov", "/v/b b.mp4", "/v/a.mov"]);
        Assert.NotNull(request);
        Assert.Equal("builtin-share", request.PresetId);
        Assert.Equal(["/v/a.mov", "/v/b b.mp4"], request.Files);
    }

    [Fact]
    public void Converted_files_go_next_to_the_source_and_never_replace_a_file()
    {
        var dir = Directory.CreateTempSubdirectory("mane-convert").FullName;
        try
        {
            var source = Path.Combine(dir, "clip.mov");
            File.WriteAllText(source, "");
            Assert.Equal(Path.Combine(dir, "clip.mp4"), FileConversion.OutputPathFor(source, Preset.Share, ProjectState.Empty, new Dictionary<string, MediaInfo>()));
            // No extension in the preset: the source's, which is taken, so numbered.
            Assert.Equal(Path.Combine(dir, "clip-2.mov"), FileConversion.OutputPathFor(source, Preset.PlaneCopy, ProjectState.Empty, new Dictionary<string, MediaInfo>()));
            File.WriteAllText(Path.Combine(dir, "clip.mp4"), "");
            File.WriteAllText(Path.Combine(dir, "clip-2.mp4"), "");
            Assert.Equal(Path.Combine(dir, "clip-3.mp4"), FileConversion.OutputPathFor(source, Preset.Share, ProjectState.Empty, new Dictionary<string, MediaInfo>()));
            Assert.Equal(Path.Combine(dir, "clip.m4a"), FileConversion.OutputPathFor(source, Preset.AudioM4a, ProjectState.Empty, new Dictionary<string, MediaInfo>()));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [FfmpegFact]
    public async Task A_whole_video_is_converted_next_to_itself()
    {
        var copyToMkv = Preset.CopyToMp4 with { Id = "test-mkv", Extension = "mkv" };
        var (plan, output) = await ConvertAsync(media.Sync30, copyToMkv);
        Assert.Equal(Path.ChangeExtension(media.Sync30, ".mkv"), output);
        var info = await media.ProbeAsync(output);
        Assert.InRange(info.Duration.TotalSeconds, 11.9, 12.2);
        Assert.NotNull(info.Video);
        Assert.NotEmpty(info.AudioStreams);
        Assert.DoesNotContain(plan.Steps.SelectMany(s => s.Arguments), a => a is "libx264" or "libx265");
    }

    [FfmpegFact]
    public async Task An_audio_preset_saves_the_whole_sound_next_to_the_video()
    {
        var (_, output) = await ConvertAsync(media.Sync25, Preset.ExtractAudio);
        // "extract audio (as is)" copies AAC into an .m4a.
        Assert.Equal(Path.ChangeExtension(media.Sync25, ".m4a"), output);
        var info = await media.ProbeAsync(output);
        Assert.Null(info.Video);
        Assert.InRange(info.Duration.TotalSeconds, 7.9, 8.2);
    }

    [FfmpegFact]
    public async Task Conversions_planned_at_the_same_time_get_their_own_files()
    {
        var dir = Directory.CreateTempSubdirectory("mane-convert").FullName;
        try
        {
            var video = Path.Combine(dir, "clip.mp4");
            File.Copy(media.Sync25, video);
            var info = await media.ProbeAsync(video);
            var keyframes = new KeyframeService(FfmpegFixture.Tools!);
            var first = await FileConversion.PlanAsync(video, info, Preset.ExtractAudio, keyframes, Path.Combine(dir, "w1"));
            var second = await FileConversion.PlanAsync(video, info, Preset.ExtractAudio, keyframes, Path.Combine(dir, "w2"));
            Assert.Equal(Path.Combine(dir, "clip.m4a"), first.OutputPath);
            Assert.Equal(Path.Combine(dir, "clip-2.m4a"), second.OutputPath);
            // Claimed as empty files until ffmpeg writes them.
            Assert.Equal(0, new FileInfo(first.OutputPath).Length);
            var result = await new ExportRunner(FfmpegFixture.Tools!).RunAsync(first, null, CancellationToken.None);
            Assert.Equal(ExportOutcome.Succeeded, result.Outcome);
            Assert.True(new FileInfo(first.OutputPath).Length > 0);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [FfmpegFact]
    public async Task A_file_without_video_is_refused()
    {
        var info = await media.ProbeAsync(media.Tone);
        var error = await Assert.ThrowsAsync<ExportPlanException>(() =>
            FileConversion.PlanAsync(media.Tone, info, Preset.Share, new KeyframeService(FfmpegFixture.Tools!), media.Directory));
        Assert.Contains("no video", error.Message);
    }

    private async Task<(ExportPlan Plan, string Output)> ConvertAsync(string source, Preset preset)
    {
        var info = await media.ProbeAsync(source);
        var work = Path.Combine(media.Directory, "work-" + Guid.NewGuid().ToString("N"));
        var plan = await FileConversion.PlanAsync(source, info, preset, new KeyframeService(FfmpegFixture.Tools!), work);
        var result = await new ExportRunner(FfmpegFixture.Tools!).RunAsync(plan, null, CancellationToken.None);
        Assert.True(result.Outcome == ExportOutcome.Succeeded, $"{result.Message}\n{result.Log}\n{plan.CommandText("ffmpeg")}");
        Assert.True(File.Exists(plan.OutputPath));
        return (plan, plan.OutputPath);
    }

    // Workflows

    private static (MacQuickActions Actions, string Dir, List<int> Refreshed) Workflows() => Workflows([]);

    private static (MacQuickActions Actions, string Dir, List<int> Refreshed) Workflows(List<string> enabled)
    {
        var dir = Directory.CreateTempSubdirectory("mane-services").FullName;
        var refreshed = new List<int>();
#pragma warning disable CA1416
        var actions = new MacQuickActions(dir, id => $"/usr/bin/open -n -a '/Applications/Mane FFmpeg.app' --args --quick-action {UnixPlatform.Quote(id)} \"$@\"",
            () => refreshed.Add(1), new RecordingMenu(enabled));
#pragma warning restore CA1416
        return (actions, dir, refreshed);
    }

    private sealed class RecordingMenu(List<string> calls) : IServicesMenu
    {
        public void Added(string name, string? previousName) => calls.Add("+" + name + (previousName is null ? "" : $" (was {previousName})"));
        public void Removed(string name) => calls.Add("-" + name);
    }

    private static string Only(string dir) => Assert.Single(Directory.GetDirectories(dir, "*.workflow"));

#pragma warning disable CA1416
    [Fact]
    public void A_preset_becomes_a_workflow_named_after_it_and_goes_away_again()
    {
        var enabled = new List<string>();
        var (actions, dir, refreshed) = Workflows(enabled);
        try
        {
            Assert.False(actions.IsInstalled(Preset.Share.Id));
            actions.Install(Preset.Share);
            Assert.True(actions.IsInstalled(Preset.Share.Id));
            Assert.False(actions.IsInstalled(Preset.ForStorage.Id));
            var workflow = Only(dir);
            Assert.Equal("for share (compatibility).workflow", Path.GetFileName(workflow));
            var info = File.ReadAllText(Path.Combine(workflow, "Contents", "Info.plist"));
            Assert.Contains("<string>for share (compatibility)</string>", info);
            Assert.Contains("<string>public.movie</string>", info);
            Assert.Contains("<string>NSTouchBarCommunicationVideo</string>", info);
            var document = File.ReadAllText(Path.Combine(workflow, "Contents", "document.wflow"));
            Assert.Contains("--quick-action 'builtin-share' \"$@\"", document);
            Assert.Contains("com.apple.Automator.servicesMenu", document);

            actions.Install(Preset.Share with { Name = "share: small / fast" });
            // Finder shows the ":" as "/".
            Assert.Equal("share- small : fast.workflow", Path.GetFileName(Only(dir)));
            Assert.Contains("<string>share- small / fast</string>", File.ReadAllText(Path.Combine(Only(dir), "Contents", "Info.plist")));

            actions.Remove(Preset.Share.Id);
            Assert.Empty(Directory.GetDirectories(dir));
            Assert.False(actions.IsInstalled(Preset.Share.Id));
            Assert.Equal(3, refreshed.Count);
            // Enabled and ordered by the name the menu shows; a rename takes over the old name's place.
            Assert.Equal(["+for share (compatibility)", "+share- small / fast (was for share (compatibility))", "-share- small / fast"], enabled);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_workflow_with_the_same_name_is_left_alone()
    {
        var calls = new List<string>();
        var (actions, dir, _) = Workflows(calls);
        try
        {
            var mine = Path.Combine(dir, "for share (compatibility).workflow", "Contents");
            Directory.CreateDirectory(mine);
            File.WriteAllText(Path.Combine(mine, "Info.plist"), "<plist><dict/></plist>");
            actions.Install(Preset.Share);
            actions.Install(Preset.Share with { Id = "other", Kind = PresetKind.Audio });
            Assert.True(File.Exists(Path.Combine(mine, "Info.plist")));
            Assert.True(Directory.Exists(Path.Combine(dir, "for share (compatibility) (Mane FFmpeg).workflow")));
            var audio = Path.Combine(dir, "for share (compatibility) (Mane FFmpeg 2).workflow", "Contents");
            Assert.True(Directory.Exists(audio));
            Assert.Contains("<string>NSTouchBarAudioOutputVolumeHigh</string>", File.ReadAllText(Path.Combine(audio, "Info.plist")));
            Assert.Contains("<string>NSTouchBarAudioOutputVolumeHigh</string>", File.ReadAllText(Path.Combine(audio, "document.wflow")));
            // The menu items are named like the folders: Finder's settings for the user's item are keyed by its name.
            Assert.Contains("<string>for share (compatibility) (Mane FFmpeg)</string>",
                File.ReadAllText(Path.Combine(dir, "for share (compatibility) (Mane FFmpeg).workflow", "Contents", "Info.plist")));
            Assert.Contains("<string>for share (compatibility) (Mane FFmpeg 2)</string>", File.ReadAllText(Path.Combine(audio, "Info.plist")));
            actions.Remove(Preset.Share.Id);
            actions.Remove("other");
            Assert.Equal(["for share (compatibility).workflow"], Directory.GetDirectories(dir).Select(Path.GetFileName));
            Assert.Equal(["+for share (compatibility) (Mane FFmpeg)", "+for share (compatibility) (Mane FFmpeg 2)",
                "-for share (compatibility) (Mane FFmpeg)", "-for share (compatibility) (Mane FFmpeg 2)"], calls);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [MacFact]
    public async Task The_workflow_files_are_valid_property_lists()
    {
        var (actions, dir, _) = Workflows();
        try
        {
            actions.Install(Preset.Share with { Id = "it's <odd> & \"quoted\"", Name = "A & B <c>" });
            Assert.True(actions.IsInstalled("it's <odd> & \"quoted\""));
            var contents = Path.Combine(Only(dir), "Contents");
            foreach (var file in new[] { "Info.plist", "document.wflow" })
            {
                var lint = await Cli.Wrap("/usr/bin/plutil").WithArguments(["-lint", Path.Combine(contents, file)])
                    .WithValidation(CommandResultValidation.None).ExecuteBufferedAsync();
                Assert.True(lint.ExitCode == 0, lint.StandardOutput + lint.StandardError);
            }
            var script = await Cli.Wrap("/usr/bin/plutil")
                .WithArguments(["-extract", "actions.0.action.ActionParameters.COMMAND_STRING", "raw", Path.Combine(contents, "document.wflow")])
                .ExecuteBufferedAsync();
            Assert.Contains("--quick-action 'it'\\''s <odd> & \"quoted\"' \"$@\"", script.StandardOutput);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
    [Fact]
    public void New_quick_actions_are_enabled_and_go_last_in_finders_order()
    {
        var prefs = System.Xml.Linq.XElement.Parse("""
            <dict>
                <key>FinderOrdering</key>
                <dict><key>APPEXTENSION-rotate</key><integer>0</integer><key>is.workflow.actions.x</key><integer>5</integer></dict>
                <key>ServicesShortcutsPresent</key><true/>
            </dict>
            """);
        string Order() => string.Join(", ", Plist.Dict(prefs, "FinderOrdering").Elements("key")
            .Select(k => $"{k.Value}={((System.Xml.Linq.XElement)k.NextNode!).Value}"));

        FinderServices.Add(prefs, "a", null);
        FinderServices.Add(prefs, "b", null);
        Assert.Equal("APPEXTENSION-rotate=0, is.workflow.actions.x=5, SERVICE-(null) - a - runWorkflowAsService=6, " +
            "SERVICE-(null) - b - runWorkflowAsService=7", Order());
        var status = Plist.Dict(Plist.Dict(prefs, "NSServicesStatus"), "(null) - a - runWorkflowAsService");
        Assert.Contains("<key>FinderPreview</key><true />", status.ToString(System.Xml.Linq.SaveOptions.DisableFormatting));
        Assert.Contains("SERVICE-(null) - a - runWorkflowAsService", Plist.Dict(prefs, "FinderActive").ToString());

        // A rename keeps the place; adding the same name again too.
        FinderServices.Add(prefs, "a2", "a");
        FinderServices.Add(prefs, "b", null);
        Assert.Equal("APPEXTENSION-rotate=0, is.workflow.actions.x=5, SERVICE-(null) - b - runWorkflowAsService=7, " +
            "SERVICE-(null) - a2 - runWorkflowAsService=6", Order());
        Assert.DoesNotContain("(null) - a -", prefs.ToString());

        FinderServices.Remove(prefs, "b");
        FinderServices.Add(prefs, "c", null);
        Assert.Equal("APPEXTENSION-rotate=0, is.workflow.actions.x=5, SERVICE-(null) - a2 - runWorkflowAsService=6, " +
            "SERVICE-(null) - c - runWorkflowAsService=7", Order());
        Assert.DoesNotContain("- b -", prefs.ToString());
        Assert.Contains("<key>ServicesShortcutsPresent</key>", prefs.ToString());
    }

    [MacFact]
    public async Task Finders_settings_are_written_back_as_a_valid_property_list()
    {
        var document = FinderServices.Parse("""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>NSServicesStatus</key>
                <dict><key>(null) - x - runShortcutAsService</key><dict><key>key_equivalent</key><string>#&#xF70B;</string></dict></dict>
                <key>Blank</key><string>  </string>
            </dict>
            </plist>
            """);
        FinderServices.Add(document.Root!.Element("dict")!, "for share & <more>", null);
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, FinderServices.Serialize(document));
            var lint = await Cli.Wrap("/usr/bin/plutil").WithArguments(["-lint", path])
                .WithValidation(CommandResultValidation.None).ExecuteBufferedAsync();
            Assert.True(lint.ExitCode == 0, lint.StandardOutput + lint.StandardError);
            var order = await Cli.Wrap("/usr/bin/plutil")
                .WithArguments(["-extract", "FinderOrdering", "xml1", "-o", "-", path]).ExecuteBufferedAsync();
            Assert.Contains("SERVICE-(null) - for share &amp; &lt;more&gt; - runWorkflowAsService", order.StandardOutput);
            var blank = await Cli.Wrap("/usr/bin/plutil").WithArguments(["-extract", "Blank", "raw", "-o", "-", path]).ExecuteBufferedAsync();
            Assert.Equal("  ", blank.StandardOutput.TrimEnd('\n'));
        }
        finally
        {
            File.Delete(path);
        }
    }

#pragma warning restore CA1416
}
