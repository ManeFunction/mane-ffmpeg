namespace ManeFfmpeg.Core.Platforms.Mac;

// Homebrew writes scripts with Unix file modes; MacPlatform is the only one that offers this installer.
#pragma warning disable CA1416

/// <summary>Installs ffmpeg and mpv with Homebrew, and Homebrew itself in Terminal first when it's missing.</summary>
/// <param name="findBrew">Finds brew; tests replace it and the next one.</param>
/// <param name="openInTerminal">Runs Homebrew's installer script in a terminal.</param>
public sealed class HomebrewInstaller(Func<string?>? findBrew = null, Func<string, Task>? openInTerminal = null) : IToolInstaller
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly Func<string?> _findBrew = findBrew ?? Homebrew.Find;
    private readonly Func<string, Task> _openInTerminal = openInTerminal ?? Homebrew.OpenInTerminalAsync;

    public string ManagerName => "Homebrew";

    public string Introduction =>
        "Mane FFmpeg uses tools installed on your Mac, and these are missing. Homebrew, the usual package manager for macOS, can install them.";

    public string ManagerSetupExplanation =>
        "First, Homebrew's official installer opens in Terminal. It asks for your Mac password and may install Apple's " +
        "Command Line Tools, which takes a few minutes. Come back here when it's done.";

    public string ManagerInstallCommand => Homebrew.InstallerCommand;

    public string? FindManager() => _findBrew();

    public async Task<string> StartManagerInstallAsync(string workDirectory)
    {
        try
        {
            await _openInTerminal(Homebrew.WriteInstallerScript(workDirectory));
        }
        catch (Exception ex)
        {
            throw new IOException($"Couldn't open Terminal: {ex.Message}", ex);
        }
        return "Follow the steps in Terminal. This window continues once Homebrew is installed.";
    }

    public async Task<int> WaitForManagerInstallAsync(string workDirectory, CancellationToken ct)
    {
        while (true)
        {
            if (Homebrew.ReadInstallerStatus(workDirectory) is { } code)
                return code;
            await Task.Delay(PollInterval, ct);
        }
    }

    public string DescribeManagerInstallFailure(int code) => code switch
    {
        0 => "Homebrew's installer finished, but brew wasn't found in /opt/homebrew or /usr/local.",
        129 or 130 or 143 => "Homebrew's installer was stopped before it finished.",
        _ => $"Homebrew's installer failed (exit code {code}). Terminal shows what went wrong.",
    };

    public string? PackageFor(string component) => component switch
    {
        "ffmpeg" => "ffmpeg",
        "libmpv" => "mpv",
        _ => null,
    };

    public string InstallCommand(IEnumerable<string> packages) => $"brew install {string.Join(' ', packages)}";

    public string InstallingHint =>
        "This usually takes a few minutes; on older macOS versions Homebrew builds from source, which can take an hour or more.";

    public Task<int> InstallAsync(string manager, IReadOnlyList<string> packages, Action<string> output, CancellationToken ct) =>
        Homebrew.InstallAsync(manager, packages, output, ct);
}
#pragma warning restore CA1416
