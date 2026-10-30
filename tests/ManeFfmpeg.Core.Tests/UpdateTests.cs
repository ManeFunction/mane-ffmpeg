using System.Diagnostics;
using System.Runtime.Versioning;
using CliWrap;
using CliWrap.Buffered;
using ManeFfmpeg.Core.Updates;

namespace ManeFfmpeg.Core.Tests;

public class AppVersionTests
{
    [Theory]
    [InlineData("v0.9.1", "0.9.1")]
    [InlineData("0.10.0", "0.10.0")]
    [InlineData("v1.2", "1.2.0")]
    [InlineData("v1.0.0-rc.1", "1.0.0")]
    [InlineData("1.4.2+abc", "1.4.2")]
    public void Parses_tags(string tag, string expected) => Assert.Equal(Version.Parse(expected), AppVersion.Parse(tag));

    [Theory]
    [InlineData("Unreleased")]
    [InlineData("")]
    [InlineData("vnext")]
    public void Rejects_non_versions(string tag) => Assert.Null(AppVersion.Parse(tag));

    [Fact]
    public void Assembly_versions_compare_equal_to_tags() =>
        Assert.Equal(AppVersion.Parse("v0.9.0"), AppVersion.Normalize(new Version(0, 9, 0, 0)));
}

public class ChangelogTests
{
    private const string Markdown = """
        # Changelog

        All notable changes to this project will be documented in this file.

        ## [Unreleased]
        ### Added
        - Not out yet.

        ## [0.10.0] - 2026-11-20
        ### Added
        - Ten.

        ## [0.9.2] - 2026-11-10
        ### Fixed
        - Nine two.

        ## [0.9.1] - 2026-11-01
        ### Fixed
        - Nine one.

        ## [0.9.0] - 2026-10-26
        ### Added
        - First release.

        [0.9.1]: https://github.com/ManeFunction/mane-ffmpeg/compare/v0.9.0...v0.9.1
        """;

    [Fact]
    public void Takes_the_versions_after_the_current_one_up_to_the_latest()
    {
        var text = Changelog.Between(Markdown, new Version(0, 9, 0), new Version(0, 9, 2));
        Assert.Equal("""
            ## [0.9.2] - 2026-11-10
            ### Fixed
            - Nine two.

            ## [0.9.1] - 2026-11-01
            ### Fixed
            - Nine one.
            """.ReplaceLineEndings("\n"), text);
    }

    [Fact]
    public void Leaves_out_unreleased_older_versions_and_link_references()
    {
        var text = Changelog.Between(Markdown, new Version(0, 8, 0), new Version(0, 10, 0))!;
        Assert.Contains("- Ten.", text);
        Assert.Contains("- First release.", text);
        Assert.DoesNotContain("Not out yet", text);
        Assert.DoesNotContain("compare/", text);
    }

    [Fact]
    public void Is_null_without_newer_sections() =>
        Assert.Null(Changelog.Between(Markdown, new Version(0, 10, 0), new Version(0, 10, 0)));
}

/// <summary>The script that swaps the new version in, run on throwaway folders instead of the installed app.</summary>
[UnsupportedOSPlatform("windows")]
public class ApplyUpdateScriptTests
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mane-ffmpeg-update").FullName;

    private string Installed => Path.Combine(_dir, "opt", "app");
    private string Staged => Path.Combine(_dir, "work", "new", "mane-ffmpeg");
    private string Log => Path.Combine(_dir, "logs", "update.log");

    private void WriteFile(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private async Task<string> RunAsync(bool runInstaller = false, IReadOnlyList<int>? waitFor = null, bool macOS = false, string? staged = null)
    {
        var plan = new UpdateInstaller.ApplyPlan(macOS, Installed, staged ?? Staged, new Version(1, 2, 3), waitFor ?? [],
            Relaunch: false, Reopen: [], runInstaller, Log, CleanUp: Path.Combine(_dir, "work"));
        var script = UpdateInstaller.WriteApplyScript(Path.Combine(_dir, "work"), plan);
        await Cli.Wrap("/bin/sh").WithArguments([script]).WithValidation(CommandResultValidation.None).ExecuteBufferedAsync();
        return File.ReadAllText(Log);
    }

    [Fact]
    public async Task Replaces_the_app_folder()
    {
        WriteFile(Path.Combine(Installed, "ManeFfmpeg"), "old");
        WriteFile(Path.Combine(Installed, "removed.dll"), "old");
        WriteFile(Path.Combine(Staged, "app", "ManeFfmpeg"), "new");

        var log = await RunAsync();

        Assert.Contains("Installed.", log);
        Assert.Equal("new", File.ReadAllText(Path.Combine(Installed, "ManeFfmpeg")));
        Assert.False(File.Exists(Path.Combine(Installed, "removed.dll")));
        Assert.Equal(["app"], Directory.GetFileSystemEntries(Path.Combine(_dir, "opt")).Select(Path.GetFileName));
        Assert.False(Directory.Exists(Path.Combine(_dir, "work")));
    }

    [Fact]
    public async Task Keeps_the_old_version_when_the_new_one_is_missing()
    {
        WriteFile(Path.Combine(Installed, "ManeFfmpeg"), "old");

        var log = await RunAsync(staged: Path.Combine(_dir, "nothing-here"));

        Assert.Contains("The update failed", log);
        Assert.Equal("old", File.ReadAllText(Path.Combine(Installed, "ManeFfmpeg")));
        Assert.Equal(["app"], Directory.GetFileSystemEntries(Path.Combine(_dir, "opt")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Runs_the_new_install_script_for_the_standard_install()
    {
        WriteFile(Path.Combine(Installed, "ManeFfmpeg"), "old");
        WriteFile(Path.Combine(Staged, "app", "ManeFfmpeg"), "new");
        var install = Path.Combine(Staged, "install.sh");
        WriteFile(install, $"#!/bin/sh\ncp -R \"$(dirname \"$0\")/app\" '{Path.Combine(_dir, "opt")}/'\n");
        File.SetUnixFileMode(install, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var log = await RunAsync(runInstaller: true);

        Assert.Contains("Installed.", log);
        Assert.Equal("new", File.ReadAllText(Path.Combine(Installed, "ManeFfmpeg")));
        Assert.Equal(["app"], Directory.GetFileSystemEntries(Path.Combine(_dir, "opt")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Restores_the_old_version_when_the_install_script_fails()
    {
        WriteFile(Path.Combine(Installed, "ManeFfmpeg"), "old");
        var install = Path.Combine(Staged, "install.sh");
        WriteFile(install, "#!/bin/sh\nexit 3\n");
        File.SetUnixFileMode(install, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var log = await RunAsync(runInstaller: true);

        Assert.Contains("restoring the old version", log);
        Assert.Equal("old", File.ReadAllText(Path.Combine(Installed, "ManeFfmpeg")));
    }

    [Fact]
    public async Task Waits_for_the_app_to_quit()
    {
        WriteFile(Path.Combine(Installed, "ManeFfmpeg"), "old");
        WriteFile(Path.Combine(Staged, "app", "ManeFfmpeg"), "new");
        using var app = Process.Start("sleep", "1");
        var started = Stopwatch.StartNew();

        await RunAsync(waitFor: [app.Id]);

        Assert.True(started.Elapsed >= TimeSpan.FromSeconds(0.8), $"Didn't wait: {started.Elapsed}");
        Assert.Equal("new", File.ReadAllText(Path.Combine(Installed, "ManeFfmpeg")));
    }

    [Fact]
    public async Task Replaces_a_mac_app_bundle()
    {
        if (!OperatingSystem.IsMacOS())
            return; // uses ditto
        var bundle = Path.Combine(_dir, "Applications", "Mane FFmpeg.app");
        WriteFile(Path.Combine(bundle, "Contents", "MacOS", "ManeFfmpeg"), "old");
        var staged = Path.Combine(_dir, "work", "new", "Mane FFmpeg.app");
        WriteFile(Path.Combine(staged, "Contents", "MacOS", "ManeFfmpeg"), "new");
        var plan = new UpdateInstaller.ApplyPlan(true, bundle, staged, new Version(1, 2, 3), [], Relaunch: false, Reopen: [],
            RunInstaller: false, Log, CleanUp: Path.Combine(_dir, "work"));

        var script = UpdateInstaller.WriteApplyScript(Path.Combine(_dir, "work"), plan);
        await Cli.Wrap("/bin/sh").WithArguments([script]).WithValidation(CommandResultValidation.None).ExecuteBufferedAsync();

        Assert.Contains("Installed.", File.ReadAllText(Log));
        Assert.Equal("new", File.ReadAllText(Path.Combine(bundle, "Contents", "MacOS", "ManeFfmpeg")));
        Assert.Equal(["Mane FFmpeg.app"], Directory.GetFileSystemEntries(Path.Combine(_dir, "Applications")).Select(Path.GetFileName));
    }
}
