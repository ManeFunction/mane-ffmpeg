using System.Diagnostics;
using System.Text;
using CliWrap;
using CliWrap.Buffered;
using ManeFfmpeg.Core.Platforms;
using ManeFfmpeg.Core.Platforms.Windows;

namespace ManeFfmpeg.Core.Tests;

#pragma warning disable CA1416 // The text and path rules are checked everywhere; [WindowsFact] tests touch the system.

/// <summary>Windows' rules that are plain text and paths, checked on any system.</summary>
public class WindowsPlatformRuleTests
{
    private static readonly WindowsPlatform Windows = new();

    [Theory]
    [InlineData("-c:v", "-c:v")]
    [InlineData("libx265", "libx265")]
    [InlineData(@"C:\ffmpeg\bin\ffmpeg.exe", @"C:\ffmpeg\bin\ffmpeg.exe")]
    [InlineData(@"C:\Program Files\out.mp4", @"'C:\Program Files\out.mp4'")]
    [InlineData("it's.mp4", "'it''s.mp4'")]
    [InlineData("scale=1280:-2,fps=30", "'scale=1280:-2,fps=30'")]
    [InlineData("@list", "'@list'")]
    [InlineData("", "''")]
    public void Quotes_arguments_for_PowerShell(string argument, string expected) =>
        Assert.Equal(expected, Windows.QuoteCommandArgument(argument));

    [Fact]
    public void Commands_run_through_the_call_operator() => Assert.Equal("& ", Windows.CommandLinePrefix);

    [Fact]
    public void Tools_are_exe_files()
    {
        Assert.Equal("ffmpeg.exe", Windows.ExecutableFileName("ffmpeg"));
        Assert.Equal("winget.exe", Windows.ExecutableFileName("winget.exe"));
    }

    [Fact]
    public void The_bundled_libmpv_comes_first_after_an_explicit_path()
    {
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll"), Windows.LibMpvCandidates(null).First());
        Assert.Equal(@"D:\mpv\libmpv-2.dll", Windows.LibMpvCandidates(@"D:\mpv\libmpv-2.dll").First());
        Assert.Equal(["libmpv-2.dll", "mpv-2.dll", "mpv-1.dll"], Windows.LibMpvLoaderNames);
        Assert.Null(Windows.LibMpvRelaunchEnvironment(@"C:\x\libmpv-2.dll"));
    }

    [Fact]
    public void Project_files_open_with_the_quoted_executable() =>
        Assert.Equal("\"C:\\Apps\\Mane FFmpeg\\ManeFfmpeg.exe\" \"%1\"", WindowsPlatform.OpenCommand(@"C:\Apps\Mane FFmpeg\ManeFfmpeg.exe"));

    [Fact]
    public void Encoded_commands_are_utf16_base64() =>
        Assert.Equal("Write-Host 'é'", Encoding.Unicode.GetString(Convert.FromBase64String(WindowsPlatform.EncodeCommand("Write-Host 'é'"))));

    [Fact]
    public void Only_ffmpeg_is_installed_with_winget()
    {
        var winget = new WingetInstaller(() => null, _ => { });
        Assert.Equal("Gyan.FFmpeg", winget.PackageFor("ffmpeg"));
        Assert.Null(winget.PackageFor("libmpv"));
        Assert.Equal("winget install -e --id Gyan.FFmpeg", winget.InstallCommand(["Gyan.FFmpeg"]));
    }

    [Fact]
    public async Task Winget_is_waited_for_until_it_shows_up()
    {
        string? winget = null;
        var opened = new List<string>();
        var installer = new WingetInstaller(() => winget, opened.Add);
        Assert.Contains("Microsoft Store", await installer.StartManagerInstallAsync(""));
        Assert.Equal(WingetInstaller.AppInstallerPage, Assert.Single(opened));
        var waiting = installer.WaitForManagerInstallAsync("", CancellationToken.None);
        await Task.Delay(100);
        Assert.False(waiting.IsCompleted);
        winget = @"C:\Users\x\AppData\Local\Microsoft\WindowsApps\winget.exe";
        Assert.Equal(0, await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void The_update_script_is_PowerShell_with_a_byte_order_mark()
    {
        var dir = Directory.CreateTempSubdirectory("mane-ffmpeg-ps1").FullName;
        try
        {
            var installed = Path.Combine(dir, "Mane FFmpeg");
            var plan = new UpdatePlan(installed, Path.Combine(dir, "it's new"), new Version(1, 2, 3), [11, 22], Relaunch: true,
                Reopen: [Path.Combine(dir, "a b.mfvp")], Path.Combine(dir, "update.log"), CleanUp: Path.Combine(dir, "work"));
            var script = Windows.WriteUpdateScript(dir, plan);
            var bytes = File.ReadAllBytes(script);
            Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
            var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            Assert.Contains("\r\n", text);
            Assert.Contains($"$installed = '{installed}'", text);
            Assert.Contains("$staged = '" + Path.Combine(dir, "it''s new") + "'", text);
            Assert.Contains("foreach ($id in @(11, 22))", text);
            Assert.Contains($"-ArgumentList '\"{Path.Combine(dir, "a b.mfvp")}\"'", text);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

/// <summary>Windows' system calls and scripts, on Windows.</summary>
public class WindowsPlatformTests
{
    private static readonly WindowsPlatform Windows = new();
    private readonly string _dir = Directory.CreateTempSubdirectory("mane-ffmpeg-windows").FullName;

    private string Installed => Path.Combine(_dir, "apps", "Mane FFmpeg");
    private string Staged => Path.Combine(_dir, "work", "new", "Mane FFmpeg");
    private string Log => Path.Combine(_dir, "logs", "update.log");

    private static void WriteFile(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private async Task<string> RunUpdateAsync(string? staged = null, IReadOnlyList<int>? waitFor = null)
    {
        var plan = new UpdatePlan(Installed, staged ?? Staged, new Version(1, 2, 3), waitFor ?? [], Relaunch: false, Reopen: [], Log,
            CleanUp: Path.Combine(_dir, "work"));
        var script = Windows.WriteUpdateScript(Path.Combine(_dir, "script"), plan);
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        await Cli.Wrap(powershell).WithArguments(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script])
            .WithValidation(CommandResultValidation.None).ExecuteBufferedAsync();
        return File.ReadAllText(Log);
    }

    [WindowsFact]
    public async Task The_update_script_replaces_the_app_folder()
    {
        WriteFile(Path.Combine(Installed, "ManeFfmpeg.exe"), "old");
        WriteFile(Path.Combine(Installed, "removed.dll"), "old");
        WriteFile(Path.Combine(Staged, "ManeFfmpeg.exe"), "new");

        var log = await RunUpdateAsync();

        Assert.Contains("Installed.", log);
        Assert.Equal("new", File.ReadAllText(Path.Combine(Installed, "ManeFfmpeg.exe")));
        Assert.False(File.Exists(Path.Combine(Installed, "removed.dll")));
        Assert.Equal(["Mane FFmpeg"], Directory.GetFileSystemEntries(Path.Combine(_dir, "apps")).Select(Path.GetFileName));
        Assert.False(Directory.Exists(Path.Combine(_dir, "work")));
    }

    [WindowsFact]
    public async Task The_update_script_keeps_the_old_version_when_the_new_one_is_missing()
    {
        WriteFile(Path.Combine(Installed, "ManeFfmpeg.exe"), "old");

        var log = await RunUpdateAsync(staged: Path.Combine(_dir, "nothing-here"));

        Assert.Contains("The update failed", log);
        Assert.Equal("old", File.ReadAllText(Path.Combine(Installed, "ManeFfmpeg.exe")));
    }

    [WindowsFact]
    public async Task The_update_script_waits_for_the_app_to_quit()
    {
        WriteFile(Path.Combine(Installed, "ManeFfmpeg.exe"), "old");
        WriteFile(Path.Combine(Staged, "ManeFfmpeg.exe"), "new");
        using var app = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 2 127.0.0.1 >nul") { CreateNoWindow = true })!;
        var started = Stopwatch.StartNew();

        await RunUpdateAsync(waitFor: [app.Id]);

        Assert.True(started.Elapsed >= TimeSpan.FromSeconds(0.8), $"Didn't wait: {started.Elapsed}");
        Assert.Equal("new", File.ReadAllText(Path.Combine(Installed, "ManeFfmpeg.exe")));
    }

    /// <summary>cmd.exe runs ping as its child, the way a Chocolatey or Scoop shim runs the real ffmpeg.</summary>
    [WindowsFact]
    public async Task A_frozen_process_and_its_children_are_suspended_until_resumed()
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul") { CreateNoWindow = true })!;
        try
        {
            IReadOnlyList<int> tree = [];
            for (var i = 0; i < 50 && tree.Count < 2; i++)
            {
                await Task.Delay(100);
                tree = WindowsPlatform.ProcessTree(process.Id);
            }
            Assert.True(tree.Count >= 2, "ping didn't start");
            Assert.Equal(process.Id, tree[0]);

            // A thread that is already blocked (cmd.exe waiting for ping) shows as suspended only once the kernel has
            // delivered the suspend to it, a moment after NtSuspendProcess returns; likewise for resuming.
            var frozen = Windows.Freeze(process.Id);
            foreach (var id in tree)
                await Wait.Until(() => IsSuspended(id), $"pid {id} to be suspended");
            frozen.Resume();
            foreach (var id in tree)
                await Wait.Until(() => !IsSuspended(id), $"pid {id} to be resumed");
        }
        finally
        {
            process.Kill(entireProcessTree: true);
        }
    }

    /// <summary>Cancelling a paused export kills the shim; its suspended child must not outlive it.</summary>
    [WindowsFact]
    public async Task Releasing_a_frozen_tree_ends_the_children_left_behind()
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul") { CreateNoWindow = true })!;
        try
        {
            IReadOnlyList<int> tree = [];
            for (var i = 0; i < 50 && tree.Count < 2; i++)
            {
                await Task.Delay(100);
                tree = WindowsPlatform.ProcessTree(process.Id);
            }
            Assert.True(tree.Count >= 2, "ping didn't start");
            var frozen = Windows.Freeze(process.Id);
            // Kill only the shim, the way a cancelled command may leave the real ffmpeg behind.
            process.Kill(entireProcessTree: false);
            process.WaitForExit();
            frozen.Release();
            foreach (var id in tree.Skip(1))
                await Wait.Until(() => !IsRunning(id), $"pid {id} to end");
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsSuspended(int pid)
    {
        using var process = Process.GetProcessById(pid);
        return process.Threads.Cast<ProcessThread>()
            .All(t => t.ThreadState == System.Diagnostics.ThreadState.Wait && t.WaitReason == ThreadWaitReason.Suspended);
    }

    [WindowsFact]
    public async Task Pausable_commands_report_the_program_itself()
    {
        var pid = 0;
        var lines = new List<string>();
        var result = await Windows.StartPausable("cmd.exe", ["/c", "echo hello"], p => pid = p, lines.Add, _ => { }, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.NotEqual(0, pid);
        Assert.Contains("hello", lines);
    }

    [WindowsFact]
    public void Keeping_awake_holds_a_power_request()
    {
        using var awake = Windows.KeepAwake("Mane FFmpeg test");
        Assert.NotSame(NoOp.Instance, awake);
    }

    [WindowsFact]
    public void Tools_are_found_on_PATH_with_exe_appended() =>
        Assert.EndsWith("cmd.exe", Executables.Find("cmd"), StringComparison.OrdinalIgnoreCase);
}
#pragma warning restore CA1416
