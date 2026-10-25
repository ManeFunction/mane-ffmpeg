using System.Diagnostics;
using System.Runtime.Versioning;
using CliWrap;
using CliWrap.Buffered;

namespace ManeFfmpeg.Core.Tests;

/// <summary>Homebrew's installer script and <c>brew install</c>, run against stand-ins for the real Homebrew.</summary>
[UnsupportedOSPlatform("windows")]
public class HomebrewTests
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mane-ffmpeg-brew").FullName;

    private string WorkDirectory => Path.Combine(_dir, "work");

    private string Executable(string name, string body)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, $"#!/bin/sh\n{body}\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    /// <summary>Runs the installer script as Terminal would, downloading <paramref name="installerBody"/> instead of Homebrew's.</summary>
    private async Task<int?> RunInstallerScriptAsync(string installerBody)
    {
        var installer = Executable("install.sh", installerBody);
        var script = Homebrew.WriteInstallerScript(WorkDirectory, new Uri(installer).AbsoluteUri);
        await Cli.Wrap(script).WithValidation(CommandResultValidation.None).ExecuteBufferedAsync();
        return Homebrew.ReadInstallerStatus(WorkDirectory);
    }

    [Fact]
    public async Task Installer_script_reports_success()
    {
        Assert.Equal(0, await RunInstallerScriptAsync("echo installing; exit 0"));
    }

    [Fact]
    public async Task Installer_script_reports_the_installers_exit_code()
    {
        Assert.Equal(7, await RunInstallerScriptAsync("exit 7"));
    }

    [Fact]
    public async Task Installer_script_reports_a_failed_download()
    {
        // An empty download would otherwise run as an empty script and "succeed".
        var script = Homebrew.WriteInstallerScript(WorkDirectory, new Uri(Path.Combine(_dir, "missing.sh")).AbsoluteUri);
        await Cli.Wrap(script).WithValidation(CommandResultValidation.None).ExecuteBufferedAsync();
        Assert.Equal(1, Homebrew.ReadInstallerStatus(WorkDirectory));
    }

    [Fact]
    public async Task Installer_script_reports_a_closed_window_as_a_failure()
    {
        var installer = Executable("install.sh", "sleep 30");
        var script = Homebrew.WriteInstallerScript(WorkDirectory, new Uri(installer).AbsoluteUri);
        // Closing a Terminal window hangs up its whole process group; job control gives the script a group of its own.
        await Cli.Wrap("/bin/bash")
            .WithArguments(["-c", "set -m; \"$0\" & pid=$!; sleep 1; kill -HUP -- -$pid; wait $pid", script])
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync();
        Assert.Equal(129, Homebrew.ReadInstallerStatus(WorkDirectory));
    }

    [Fact]
    public void Installer_status_is_unknown_until_the_script_has_written_it()
    {
        Homebrew.WriteInstallerScript(WorkDirectory);
        Assert.Null(Homebrew.ReadInstallerStatus(WorkDirectory));
        File.WriteAllText(Path.Combine(WorkDirectory, "install-homebrew.status"), "");
        Assert.Null(Homebrew.ReadInstallerStatus(WorkDirectory));
    }

    [Fact]
    public void Writing_the_installer_script_forgets_the_last_run()
    {
        Homebrew.WriteInstallerScript(WorkDirectory);
        File.WriteAllText(Path.Combine(WorkDirectory, "install-homebrew.status"), "1\n");
        Homebrew.WriteInstallerScript(WorkDirectory);
        Assert.Null(Homebrew.ReadInstallerStatus(WorkDirectory));
    }

    [Fact]
    public async Task Install_passes_the_formulae_and_streams_stdout_and_stderr_in_order()
    {
        var brew = Executable("brew", "echo \"args: $*\"; echo 'Error: no bottle' >&2; echo \"hints: $HOMEBREW_NO_ENV_HINTS\"; exit 3");
        var lines = new List<string>();
        var code = await Homebrew.InstallAsync(brew, ["ffmpeg", "mpv"], lines.Add, CancellationToken.None);
        Assert.Equal(3, code);
        Assert.Equal(["args: install ffmpeg mpv", "Error: no bottle", "hints: 1"], lines);
    }

    [Fact]
    public async Task Stopping_an_install_interrupts_brew_like_ctrl_c()
    {
        // "ready" comes after the trap is set; a SIGINT before that would just kill the shell.
        var brew = Executable("brew", "trap 'echo interrupted; kill $!; exit 130' INT\necho ready\nsleep 30 &\nwait");
        var lines = new List<string>();
        using var cts = new CancellationTokenSource();
        var clock = new Stopwatch();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Homebrew.InstallAsync(brew, ["ffmpeg"], line =>
        {
            lock (lines)
                lines.Add(line);
            if (line == "ready")
            {
                clock.Start();
                cts.Cancel();
            }
        }, cts.Token));
        // Well inside the grace period: brew got SIGINT and exited on its own.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), $"took {clock.Elapsed}");
        Assert.Contains("interrupted", lines);
    }
}
