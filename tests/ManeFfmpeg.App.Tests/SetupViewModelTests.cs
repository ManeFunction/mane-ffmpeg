using System.Runtime.Versioning;
using ManeFfmpeg.App.ViewModels;
using ManeFfmpeg.Core;

namespace ManeFfmpeg.App.Tests;

/// <summary>The install dialog's steps, with stand-ins for brew and Terminal.</summary>
[UnsupportedOSPlatform("windows")]
public class SetupViewModelTests
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mane-ffmpeg-setup").FullName;

    private string StatusFile => Path.Combine(_dir, "install-homebrew.status");

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 250 && !condition(); i++)
            await Task.Delay(20);
        Assert.True(condition());
    }

    private static SetupComponent[] Ffmpeg() => [new SetupComponent("ffmpeg", "ffmpeg", "")];

    private string FakeBrew(string body)
    {
        var path = Path.Combine(_dir, "brew");
        File.WriteAllText(path, $"#!/bin/sh\n{body}\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private SetupViewModel Create(Func<string?> findBrew, Func<string, Task>? openInTerminal = null) =>
        new(Ffmpeg(), findBrew, openInTerminal ?? (_ => Task.CompletedTask), _dir);

    [Fact]
    public void Without_Homebrew_it_offers_to_install_Homebrew_first()
    {
        var vm = Create(() => null);
        Assert.Equal(SetupStep.NeedsHomebrew, vm.Step);
        Assert.True(vm.ShowHomebrew);
        Assert.Equal("Install Homebrew…", vm.PrimaryText);
    }

    [Fact]
    public async Task Once_Homebrew_is_installed_it_offers_the_components()
    {
        string? brew = null;
        var opened = new List<string>();
        var vm = Create(() => brew, script =>
        {
            opened.Add(script);
            return Task.CompletedTask;
        });
        var running = vm.ContinueCommand.ExecuteAsync(null);
        await Until(() => vm.Step == SetupStep.WaitingForHomebrew);
        Assert.False(vm.ContinueCommand.CanExecute(null));
        Assert.EndsWith(".command", Assert.Single(opened));

        // The script in Terminal has finished.
        brew = "/opt/homebrew/bin/brew";
        File.WriteAllText(StatusFile, "0\n");
        await running;
        Assert.Equal(SetupStep.Ready, vm.Step);
        Assert.Equal("Install", vm.PrimaryText);
        Assert.False(vm.StatusIsError);
    }

    [Fact]
    public async Task A_stopped_Homebrew_installer_can_be_tried_again()
    {
        var vm = Create(() => null, _ =>
        {
            File.WriteAllText(StatusFile, "129\n");
            return Task.CompletedTask;
        });
        await vm.ContinueCommand.ExecuteAsync(null);
        Assert.Equal(SetupStep.NeedsHomebrew, vm.Step);
        Assert.True(vm.StatusIsError);
        Assert.Contains("stopped", vm.Status);
        Assert.True(vm.ContinueCommand.CanExecute(null));
    }

    [Fact]
    public void Install_needs_something_selected()
    {
        var vm = Create(() => "/opt/homebrew/bin/brew");
        Assert.Equal(SetupStep.Ready, vm.Step);
        Assert.Equal("brew install ffmpeg", vm.InstallCommand);
        Assert.True(vm.ContinueCommand.CanExecute(null));
        vm.Components[0].IsSelected = false;
        Assert.False(vm.ContinueCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_failed_install_shows_brews_output_and_can_be_tried_again()
    {
        var brew = FakeBrew("echo '==> Fetching ffmpeg'; echo 'Error: no bottle available' >&2; exit 1");
        var vm = Create(() => brew);
        await vm.ContinueCommand.ExecuteAsync(null);
        Assert.Equal(SetupStep.Failed, vm.Step);
        Assert.True(vm.StatusIsError);
        Assert.Equal("Try Again", vm.PrimaryText);
        await Until(() => vm.Log?.Contains("Error: no bottle available") == true);
    }

    [Fact]
    public async Task Stop_ends_a_running_install()
    {
        var brew = FakeBrew("trap 'kill $!; exit 130' INT\necho ready\nsleep 30 &\nwait");
        var vm = Create(() => brew);
        var running = vm.ContinueCommand.ExecuteAsync(null);
        Assert.True(vm.IsInstalling);
        Assert.Equal("Stop", vm.SecondaryText);
        await Until(() => vm.Log?.Contains("ready") == true);
        vm.DismissCommand.Execute(null);
        await running;
        Assert.Equal(SetupStep.Failed, vm.Step);
        Assert.Equal("Installation stopped.", vm.Status);
    }

    [Fact]
    public async Task A_successful_install_locates_the_tools_again()
    {
        if (Executables.Find("ffmpeg") is null)
            return;
        var vm = Create(() => FakeBrew("echo '==> Pouring ffmpeg'"));
        await vm.ContinueCommand.ExecuteAsync(null);
        Assert.Equal(SetupStep.Installed, vm.Step);
        Assert.StartsWith("✓ ffmpeg", vm.Status);
        Assert.Equal("Done", vm.PrimaryText);
        Assert.False(vm.HasSecondary);
    }
}
