using System.Diagnostics;
using CliWrap;

namespace ManeFfmpeg.Core.Platforms.Windows;

/// <summary>
/// Installs ffmpeg with winget. winget comes with Windows 10 and 11 as App Installer; when it's missing, the Microsoft
/// Store opens on its page. libmpv isn't offered: the Windows build bundles it.
/// </summary>
/// <param name="findWinget">Finds winget; tests replace it and the next one.</param>
/// <param name="openStore">Opens the Store page.</param>
public sealed class WingetInstaller(Func<string?>? findWinget = null, Action<string>? openStore = null) : IToolInstaller
{
    public const string AppInstallerPage = "ms-windows-store://pdp/?ProductId=9NBLGGH4NBL5";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long a stopped install gets to clean up after Ctrl-C before it's killed.</summary>
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// winget's codes for a package that's already installed: <c>UPDATE_NOT_APPLICABLE</c> (0x8A15002B, what
    /// <c>winget install</c> returns when it finds the package and no newer version) and <c>PACKAGE_ALREADY_INSTALLED</c>
    /// (0x8A150061). The install then counts as done and the app looks for the tool.
    /// </summary>
    internal static bool MeansAlreadyInstalled(int code) => code is unchecked((int)0x8A15002B) or unchecked((int)0x8A150061);

    /// <summary>The winget package ffmpeg comes from. A portable package: winget unpacks it instead of running an installer.</summary>
    internal const string FfmpegPackage = "Gyan.FFmpeg";

    /// <summary>
    /// The bin folders of <see cref="FfmpegPackage"/> under a winget Packages folder, newest version first: winget unpacks
    /// it to Packages\Gyan.FFmpeg_&lt;source&gt;\ffmpeg-&lt;version&gt;-full_build\bin.
    /// </summary>
    internal static IEnumerable<string> FfmpegBinDirectories(string packagesRoot)
    {
        try
        {
            if (!Directory.Exists(packagesRoot))
                return [];
            return Directory.EnumerateDirectories(packagesRoot, FfmpegPackage + "_*")
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

    private readonly Func<string?> _findWinget = findWinget ?? (() => Executables.Find("winget"));
    private readonly Action<string> _openStore = openStore ?? (uri => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose());

    public string ManagerName => "winget";

    public string Introduction =>
        "Mane FFmpeg uses ffmpeg, which isn't installed. winget, the package manager that comes with Windows, can install it.";

    public string ManagerSetupExplanation =>
        "winget comes with Windows as App Installer, and it's missing here. The Microsoft Store opens on its page: " +
        "install or update it there, then come back here.";

    public string ManagerInstallCommand => AppInstallerPage;

    public string? FindManager() => _findWinget();

    public Task<string> StartManagerInstallAsync(string workDirectory)
    {
        try
        {
            _openStore(AppInstallerPage);
        }
        catch (Exception ex)
        {
            throw new IOException($"Couldn't open the Microsoft Store: {ex.Message}", ex);
        }
        return Task.FromResult("Install App Installer in the Microsoft Store. This window continues once winget is available.");
    }

    /// <summary>The Store reports nothing back, so this waits until winget shows up.</summary>
    public async Task<int> WaitForManagerInstallAsync(string workDirectory, CancellationToken ct)
    {
        while (FindManager() is null)
            await Task.Delay(PollInterval, ct);
        return 0;
    }

    public string DescribeManagerInstallFailure(int code) =>
        "winget still isn't available. Install App Installer from the Microsoft Store, or install ffmpeg yourself and set its path in Settings.";

    public string? PackageFor(string component) => component == "ffmpeg" ? FfmpegPackage : null;

    public string InstallCommand(IEnumerable<string> packages) => string.Join("; ", packages.Select(p => $"winget install -e --id {p}"));

    public string InstallingHint => "This usually takes a minute or two.";

    public async Task<int> InstallAsync(string manager, IReadOnlyList<string> packages, Action<string> output, CancellationToken ct)
    {
        using var forceful = new CancellationTokenSource();
        // Disposed before `forceful`, so the callback never sees a disposed source.
        await using var _ = ct.Register(static state => ((CancellationTokenSource)state!).CancelAfter(StopGrace), forceful);
        var pipe = PipeTarget.ToDelegate(output);
        foreach (var package in packages)
        {
            var result = await Cli.Wrap(manager)
                .WithArguments(["install", "--exact", "--id", package, "--source", "winget",
                    "--accept-source-agreements", "--accept-package-agreements", "--disable-interactivity"])
                .WithStandardOutputPipe(pipe)
                .WithStandardErrorPipe(pipe)
                .WithValidation(CommandResultValidation.None)
                .ExecuteAsync(forceful.Token, ct);
            if (MeansAlreadyInstalled(result.ExitCode))
                output($"{package} is already installed.");
            else if (result.ExitCode != 0)
                return result.ExitCode;
        }
        return 0;
    }
}
