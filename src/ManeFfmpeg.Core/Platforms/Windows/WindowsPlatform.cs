using System.Diagnostics;
using System.Runtime.Versioning;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.Core.Platforms.Windows;

/// <summary>
/// Windows. The other areas are in the WindowsPlatform.*.cs files next to this one. The release zip bundles libmpv
/// (libmpv-2.dll next to ManeFfmpeg.exe); ffmpeg comes from winget, Scoop, Chocolatey or a folder the user picks.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsPlatform : IPlatform
{
    /// <summary>The bundled libmpv's file name, as the packaging script puts it next to the executable.</summary>
    public const string BundledLibMpv = "libmpv-2.dll";

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public PlatformKind Kind => PlatformKind.Windows;
    public string RuntimePrefix => "win";
    public CommandKey CommandKey => CommandKey.Control;
    public bool HasGlobalAppMenu => false;
    public bool OpensFilesThroughEvents => false;
    public string FileManagerName => "Explorer";
    public StringComparison PathComparison => StringComparison.OrdinalIgnoreCase;

    // Windows blocks drag and drop from a process with fewer rights (UIPI), so Explorer can't drop on an elevated app.
    // IsPrivilegedProcess is true only for an elevated token, not for an admin account started normally.
    public string? FileDropBlockedReason => Environment.IsPrivilegedProcess
        ? "Running as administrator: Windows blocks dropping files from Explorer. Use “Add videos…” or start the app normally."
        : null;

    // Folders

    public string ConfigDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppPaths.AppName);
    public string CacheDirectory => Path.Combine(LocalAppData, AppPaths.AppName, "Cache");
    public string LogDirectory => Path.Combine(LocalAppData, AppPaths.AppName, "Logs");

    // Tools

    /// <summary>
    /// PATH, then where the package managers put their commands. A winget install made while the app runs changes PATH
    /// only for processes started later, so the user's and the system's PATH are also read from the registry, and
    /// winget's Links folders are searched explicitly. Without Developer Mode or admin rights winget can't make the
    /// symlink in Links for a portable package (Gyan.FFmpeg is one) and adds the package's folder to PATH instead, so
    /// that folder is searched too.
    /// </summary>
    public IEnumerable<string> ToolSearchDirectories()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirs = SplitPath(Environment.GetEnvironmentVariable("PATH"))
            .Concat(SplitPath(StoredPath(EnvironmentVariableTarget.User)))
            .Concat(SplitPath(StoredPath(EnvironmentVariableTarget.Machine)))
            .Concat([
                Path.Combine(LocalAppData, "Microsoft", "WinGet", "Links"),
                Path.Combine(programFiles, "WinGet", "Links"),
                Path.Combine(LocalAppData, "Microsoft", "WindowsApps"),
                Path.Combine(Home, "scoop", "shims"),
                Path.Combine(programData, "chocolatey", "bin"),
                Path.Combine(programFiles, "ffmpeg", "bin"),
                Path.Combine(Path.GetPathRoot(programFiles) ?? @"C:\", "ffmpeg", "bin"),
                AppContext.BaseDirectory,
            ])
            .Concat(WingetPackageBinDirectories(Path.Combine(LocalAppData, "Microsoft", "WinGet", "Packages"), "Gyan.FFmpeg"))
            .Concat(WingetPackageBinDirectories(Path.Combine(programFiles, "WinGet", "Packages"), "Gyan.FFmpeg"));
        foreach (var dir in dirs)
            if (dir.Length > 0 && seen.Add(dir))
                yield return dir;
    }

    internal static IEnumerable<string> SplitPath(string? path) =>
        (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(dir => Environment.ExpandEnvironmentVariables(dir.Trim('"')));

    /// <summary>PATH as stored in the registry for the user or the system, which may be newer than this process's.</summary>
    private static string? StoredPath(EnvironmentVariableTarget target)
    {
        try
        {
            return Environment.GetEnvironmentVariable("Path", target);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The bin folders of a portable winget package under <paramref name="packagesRoot"/>, newest version first:
    /// winget unpacks Gyan.FFmpeg to Packages\Gyan.FFmpeg_&lt;source&gt;\ffmpeg-&lt;version&gt;-full_build\bin.
    /// </summary>
    internal static IEnumerable<string> WingetPackageBinDirectories(string packagesRoot, string packageId)
    {
        try
        {
            if (!Directory.Exists(packagesRoot))
                return [];
            return Directory.EnumerateDirectories(packagesRoot, packageId + "_*")
                .SelectMany(package => Directory.EnumerateDirectories(package))
                .Select(version => Path.Combine(version, "bin"))
                .Where(Directory.Exists)
                .OrderByDescending(bin => bin, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public string ExecutableFileName(string tool) => tool.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? tool : tool + ".exe";

    public string FfmpegMissingHint => "ffmpeg wasn't found. Install it (winget install Gyan.FFmpeg) or set its path in Settings.";
    public string FfmpegSearchDescription => "automatic (PATH, winget, Scoop, Chocolatey…)";

    /// <summary>PowerShell: bare when nothing in it means anything to PowerShell, else in single quotes.</summary>
    public string QuoteCommandArgument(string argument) =>
        argument.Length > 0 && argument.All(c => char.IsLetterOrDigit(c) || @"-_./:=+%\".Contains(c))
            ? argument
            : PowerShellQuote(argument);

    public string CommandLinePrefix => "& ";

    /// <summary>A PowerShell string literal: single quotes, with single quotes inside doubled.</summary>
    internal static string PowerShellQuote(string value) => "'" + value.Replace("'", "''") + "'";

    // libmpv

    public IEnumerable<string> LibMpvCandidates(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            yield return explicitPath;
        // mpv builds have called the DLL libmpv-2.dll since 0.36, and mpv-2.dll / mpv-1.dll before.
        foreach (var name in LibMpvLoaderNames)
            yield return Path.Combine(AppContext.BaseDirectory, name);
    }

    /// <summary>The loader looks in the app folder, System32 and PATH.</summary>
    public IReadOnlyList<string> LibMpvLoaderNames => [BundledLibMpv, "mpv-2.dll", "mpv-1.dll"];

    public string LibMpvInstallHint =>
        $"{BundledLibMpv} comes with the app, next to ManeFfmpeg.exe. If it's missing, download an mpv-dev-lgpl build (the " +
        "one the app bundles) from https://github.com/zhongfly/mpv-winbuild/releases and put its DLL there, or set the " +
        "libmpv path in Settings.";

    public string LibMpvSearchDescription => $"automatic ({BundledLibMpv} next to the app, PATH)";

    public IReadOnlyDictionary<string, string>? LibMpvRelaunchEnvironment(string libMpvPath) => null;

    public IReadOnlyList<int> ProcessesToWaitFor(bool relaunched) => [Environment.ProcessId];

    // File manager

    public void RevealInFileManager(string path)
    {
        try
        {
            // Explorer parses its own command line: the switch and the quoted path are one argument.
            Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{path}\"", UseShellExecute = false });
        }
        catch (Exception)
        {
            // Nothing to do if Explorer can't be started.
        }
    }

    // Setup

    public IToolInstaller ToolInstaller { get; } = new WingetInstaller();

    // Windows: Avalonia's own move and resize show no torn frame here.

    public bool TryChangeWindowFrame(nint handle, string? handleDescriptor, double topDown, double grow) => false;
}
