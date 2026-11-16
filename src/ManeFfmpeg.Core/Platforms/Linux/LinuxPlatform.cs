using System.Diagnostics;
using System.Runtime.Versioning;
using CliWrap.Buffered;
using CliWrap;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.Core.Platforms.Linux;

/// <summary>Linux (and other Unix systems, best effort). The .mfvp association is in LinuxPlatform.ProjectFiles.cs.</summary>
[UnsupportedOSPlatform("windows")]
public sealed partial class LinuxPlatform : UnixPlatform
{
    /// <summary>The folder and file names the Linux bundle and XDG folders use.</summary>
    internal const string LinuxName = "mane-ffmpeg";

    public override PlatformKind Kind => PlatformKind.Linux;
    public override string RuntimePrefix => "linux";
    public override CommandKey CommandKey => CommandKey.Control;
    public override bool HasGlobalAppMenu => false;
    public override bool OpensFilesThroughEvents => false;
    public override string FileManagerName => "the file manager";
    public override StringComparison PathComparison => StringComparison.Ordinal;

    // Folders

    public override string ConfigDirectory => Path.Combine(Xdg("XDG_CONFIG_HOME", ".config"), LinuxName);
    public override string CacheDirectory => Path.Combine(Xdg("XDG_CACHE_HOME", ".cache"), LinuxName);
    public override string LogDirectory => CacheDirectory;

    /// <summary>An XDG base directory: the variable when it holds an absolute path, else ~/<paramref name="fallback"/>.</summary>
    internal static string Xdg(string variable, string fallback) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value && Path.IsPathRooted(value)
            ? value
            : Path.Combine(Home, fallback);

    // Tools

    public override string FfmpegMissingHint => "ffmpeg wasn't found. Install it with your package manager or set its path in Settings.";
    public override string FfmpegSearchDescription => "automatic (PATH, /usr/bin…)";

    // libmpv

    public override IEnumerable<string> LibMpvCandidates(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            yield return explicitPath;
        foreach (var dir in new[] { "/usr/lib/x86_64-linux-gnu", "/usr/lib/aarch64-linux-gnu", "/usr/lib64", "/usr/lib", "/usr/local/lib" })
        {
            yield return Path.Combine(dir, "libmpv.so.2");
            yield return Path.Combine(dir, "libmpv.so.1");
            yield return Path.Combine(dir, "libmpv.so");
        }
    }

    public override IReadOnlyList<string> LibMpvLoaderNames => ["libmpv.so.2", "libmpv.so.1"];

    public override string LibMpvInstallHint =>
        "Install libmpv (Debian/Ubuntu: sudo apt install libmpv2, Fedora: sudo dnf install mpv-libs, Arch: sudo pacman -S mpv).";

    public override string LibMpvSearchDescription => "automatic (system libmpv)";

    // Pausing

    protected override int StopSignal => 19;
    protected override int ContinueSignal => 18;

    // Power: a systemd-inhibit lock, which shows in `systemd-inhibit --list`.

    public override IDisposable KeepAwake(string reason)
    {
        try
        {
            if (Executables.Find("systemd-inhibit") is not { } systemdInhibit)
            {
                AppLog.Write("Can't keep the computer awake: systemd-inhibit not found");
                return NoOp.Instance;
            }
            // cat reads the pipe below, so the lock also goes away when this process dies.
            var process = Process.Start(new ProcessStartInfo(systemdInhibit)
            {
                ArgumentList = { "--what=idle:sleep", "--mode=block", $"--who={AppPaths.AppName}", $"--why={reason}", "cat" },
                UseShellExecute = false,
                RedirectStandardInput = true,
            });
            return process is null ? NoOp.Instance : new InhibitLock(process);
        }
        catch (Exception ex)
        {
            AppLog.Write($"Can't keep the computer awake: {ex.Message}");
            return NoOp.Instance;
        }
    }

    private sealed class InhibitLock(Process process) : IDisposable
    {
        private Process? _process = process;

        public void Dispose()
        {
            // systemd-inhibit holds its lock until cat exits, and cat exits when its stdin closes.
            _process?.StandardInput.Close();
            _process?.Dispose();
            _process = null;
        }
    }

    // Notifications: notify-send, or the freedesktop D-Bus call when it's missing.

    public override async Task NotifyAsync(string title, string body)
    {
        try
        {
            if (Executables.Find("notify-send") is { } notifySend)
            {
                var result = await Cli.Wrap(notifySend)
                    .WithArguments(["--app-name", AppPaths.AppName, "--icon", "video-x-generic", title, body])
                    .WithValidation(CommandResultValidation.None)
                    .ExecuteAsync();
                if (result.ExitCode == 0)
                    return;
            }
            if (Executables.Find("gdbus") is not { } gdbus)
                return;
            await Cli.Wrap(gdbus)
                .WithArguments(["call", "--session", "--dest", "org.freedesktop.Notifications",
                    "--object-path", "/org/freedesktop/Notifications", "--method", "org.freedesktop.Notifications.Notify",
                    AppPaths.AppName, "0", "video-x-generic", title, body, "[]", "{}", "8000"])
                .WithValidation(CommandResultValidation.None)
                .ExecuteAsync();
        }
        catch (Exception)
        {
            // Notifications are best-effort; the export panel always shows the result.
        }
    }

    // File manager: xdg-open can't select a file, so it opens the folder.

    public override void RevealInFileManager(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { Path.GetDirectoryName(path)! }, UseShellExecute = false });
        }
        catch (Exception)
        {
            // Nothing to do if no file manager is available.
        }
    }

    // Updates: the release is a tarball holding mane-ffmpeg/app/ and install.sh; an update replaces the app/ folder.

    private const string AppFolder = "app";

    public override string UpdateAssetExtension => ".tar.gz";
    public override string UpdateStagedName => LinuxName;

    public override async Task UnpackUpdateAsync(string archive, string folder, CancellationToken ct)
    {
        var result = await Cli.Wrap(Executables.Find("tar") ?? "tar").WithArguments(["-xzf", archive, "-C", folder])
            .WithValidation(CommandResultValidation.None).ExecuteBufferedAsync(ct);
        if (result.ExitCode != 0)
            throw new IOException($"Couldn't unpack {Path.GetFileName(archive)}: {result.StandardError.Trim()}");
    }

    public override string UpdateExecutableIn(string staged) => Path.Combine(staged, AppFolder, "ManeFfmpeg");

    /// <summary>The bundle's app/ folder.</summary>
    public override string? UpdateInstalledPath(string processPath)
    {
        var folder = Path.GetDirectoryName(processPath);
        return Path.GetFileName(folder) == AppFolder ? folder : null;
    }

    /// <summary>The bundle's install.sh installs here; updating this copy runs the new install.sh (it also refreshes the menu entry).</summary>
    protected override bool RunsInstaller(string installed) =>
        Path.GetFullPath(installed) == Path.Combine(Home, ".local", "opt", LinuxName, AppFolder);

    internal bool RunsInstallerFor(string installed) => RunsInstaller(installed);

    protected override string CopyInScript => "cp -R \"$staged/app\" \"$incoming\"";
    protected override string RelaunchScript(string reopen) => $"nohup \"$installed/ManeFfmpeg\" {reopen} >/dev/null 2>&1 &";

    // Setup: distro package managers need root and differ too much; the hints say what to install.

    public override IToolInstaller? ToolInstaller => null;
}
