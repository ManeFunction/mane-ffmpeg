using System.Diagnostics;
using System.Runtime.Versioning;
using CliWrap;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.Core.Platforms.Mac;

/// <summary>macOS. The other areas are in the MacPlatform.*.cs files next to this one.</summary>
[UnsupportedOSPlatform("windows")]
public sealed partial class MacPlatform : UnixPlatform
{
    public override PlatformKind Kind => PlatformKind.MacOS;
    public override string RuntimePrefix => "osx";
    public override CommandKey CommandKey => CommandKey.Meta;
    public override bool HasGlobalAppMenu => true;
    public override bool OpensFilesThroughEvents => true;
    public override string FileManagerName => "Finder";
    public override StringComparison PathComparison => StringComparison.OrdinalIgnoreCase;

    // Folders

    public override string ConfigDirectory => Path.Combine(Home, "Library", "Application Support", AppPaths.AppName);
    public override string CacheDirectory => Path.Combine(Home, "Library", "Caches", AppPaths.AppName);
    public override string LogDirectory => Path.Combine(Home, "Library", "Logs", AppPaths.AppName);

    // Tools

    public override string FfmpegMissingHint => "ffmpeg wasn't found. Install it (brew install ffmpeg) or set its path in Settings.";
    public override string FfmpegSearchDescription => "automatic (PATH, Homebrew, /usr/bin…)";

    // libmpv

    public const string IinaFrameworks = "/Applications/IINA.app/Contents/Frameworks";

    public override IEnumerable<string> LibMpvCandidates(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            yield return explicitPath;
        foreach (var dir in new[] { "/opt/homebrew/lib", "/usr/local/lib", "/opt/local/lib", IinaFrameworks })
        {
            yield return Path.Combine(dir, "libmpv.2.dylib");
            yield return Path.Combine(dir, "libmpv.dylib");
        }
    }

    public override IReadOnlyList<string> LibMpvLoaderNames => [];

    public override string LibMpvInstallHint =>
        "Install IINA (brew install --cask iina) or mpv (brew install mpv), or set the libmpv path in Settings.";

    public override string LibMpvSearchDescription => "automatic (Homebrew, MacPorts, IINA, system)";

    /// <summary>
    /// IINA's libmpv resolves its dependencies through @rpath, which only works when DYLD_FALLBACK_LIBRARY_PATH points at
    /// IINA's Frameworks folder before the process starts. Homebrew/MacPorts dylibs have absolute install names.
    /// </summary>
    public override IReadOnlyDictionary<string, string>? LibMpvRelaunchEnvironment(string libMpvPath)
    {
        var dir = Path.GetDirectoryName(libMpvPath)!;
        if (!dir.EndsWith("/Contents/Frameworks", StringComparison.Ordinal))
            return null;
        var current = Environment.GetEnvironmentVariable("DYLD_FALLBACK_LIBRARY_PATH") ?? "";
        if (current.Split(':').Contains(dir))
            return null;
        var defaults = current.Length > 0 ? current : $"{Home}/lib:/usr/local/lib:/usr/lib";
        return new Dictionary<string, string> { ["DYLD_FALLBACK_LIBRARY_PATH"] = $"{dir}:{defaults}" };
    }

    /// <summary>The copy that relaunched this one for libmpv waits for it and then exits, so the update waits for both.</summary>
    public override IReadOnlyList<int> ProcessesToWaitFor(bool relaunched) =>
        relaunched ? [Environment.ProcessId, GetParentProcessId()] : [Environment.ProcessId];

    // Pausing

    protected override int StopSignal => 17;
    protected override int ContinueSignal => 19;

    // Notifications

    // Title and body go in as argv, so no AppleScript escaping is needed.
    public override async Task NotifyAsync(string title, string body)
    {
        try
        {
            await Cli.Wrap("/usr/bin/osascript")
                .WithArguments(["-e", "on run argv", "-e", "display notification (item 2 of argv) with title (item 1 of argv)",
                    "-e", "end run", title, body])
                .WithValidation(CommandResultValidation.None)
                .ExecuteAsync();
        }
        catch (Exception)
        {
            // Notifications are best-effort; the export panel always shows the result.
        }
    }

    // File manager

    public override void RevealInFileManager(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("/usr/bin/open") { ArgumentList = { "-R", path }, UseShellExecute = false });
        }
        catch (Exception)
        {
            // Nothing to do if Finder can't be asked.
        }
    }

    // Setup

    public override IToolInstaller ToolInstaller { get; } = new HomebrewInstaller();
}
