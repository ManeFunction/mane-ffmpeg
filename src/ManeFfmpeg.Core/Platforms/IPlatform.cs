using CliWrap;

namespace ManeFfmpeg.Core.Platforms;

public enum PlatformKind
{
    MacOS,
    Linux,
    Windows,
}

/// <summary>The key that, with a letter, runs a menu command: Cmd on macOS, Ctrl elsewhere.</summary>
public enum CommandKey
{
    Meta,
    Control,
}

/// <summary>
/// Everything that differs between macOS, Linux and Windows, in one place. The rest of the app asks
/// <see cref="Platform.Current"/> and never checks the operating system itself. Each implementation
/// (<see cref="Mac.MacPlatform"/>, <see cref="Linux.LinuxPlatform"/>, <see cref="Windows.WindowsPlatform"/>)
/// keeps one file per area below.
/// </summary>
public interface IPlatform
{
    // Identity and UI conventions

    PlatformKind Kind { get; }

    /// <summary>The platform part of the release workflow's runtime identifiers ("osx" in "osx-arm64").</summary>
    string RuntimePrefix { get; }

    CommandKey CommandKey { get; }

    /// <summary>
    /// Whether menus live in a global menu bar (macOS), with Settings in the app menu and menu shortcuts handled by the
    /// system. Elsewhere they're drawn in the window, Settings sits in the File menu, and the window binds the shortcuts.
    /// </summary>
    bool HasGlobalAppMenu { get; }

    /// <summary>
    /// Whether files the app is started with also arrive as "open document" events (macOS), so the command line's
    /// file arguments must be ignored to open them only once.
    /// </summary>
    bool OpensFilesThroughEvents { get; }

    /// <summary>How the system file manager is called in the UI: Finder, Explorer, Files.</summary>
    string FileManagerName { get; }

    /// <summary>How file paths compare on the usual file systems here.</summary>
    StringComparison PathComparison { get; }

    /// <summary>
    /// Why files dragged from the file manager can't be dropped on the app's windows, or null when they can. On Windows an
    /// app run as administrator doesn't get drops from Explorer, which runs as the normal user.
    /// </summary>
    string? FileDropBlockedReason { get; }

    // Folders

    string ConfigDirectory { get; }

    string CacheDirectory { get; }

    /// <summary>Where app.log goes.</summary>
    string LogDirectory { get; }

    // Command-line tools

    /// <summary>Folders searched for tools like ffmpeg, most preferred first: PATH, then the usual install folders.</summary>
    IEnumerable<string> ToolSearchDirectories();

    /// <summary>The file name of a command-line tool: "ffmpeg", or "ffmpeg.exe" on Windows.</summary>
    string ExecutableFileName(string tool);

    /// <summary>Shown when ffmpeg isn't found.</summary>
    string FfmpegMissingHint { get; }

    /// <summary>Where the app looks for ffmpeg, for the Settings placeholder.</summary>
    string FfmpegSearchDescription { get; }

    /// <summary>Quotes one argument of a command shown for copying into this platform's terminal.</summary>
    string QuoteCommandArgument(string argument);

    /// <summary>Prefix for a copyable command line: PowerShell needs <c>&amp; </c> before a quoted program path.</summary>
    string CommandLinePrefix { get; }

    // libmpv

    /// <summary>libmpv files to try, most preferred first, starting with <paramref name="explicitPath"/>.</summary>
    IEnumerable<string> LibMpvCandidates(string? explicitPath);

    /// <summary>Names for the system loader to resolve when none of the candidate files exists.</summary>
    IReadOnlyList<string> LibMpvLoaderNames { get; }

    string LibMpvInstallHint { get; }

    /// <summary>Where the app looks for libmpv, for the Settings placeholder.</summary>
    string LibMpvSearchDescription { get; }

    /// <summary>
    /// Environment variables the process must be started with for the libmpv at <paramref name="libMpvPath"/> to load
    /// (IINA's copy on macOS), or null when it loads as it is.
    /// </summary>
    IReadOnlyDictionary<string, string>? LibMpvRelaunchEnvironment(string libMpvPath);

    /// <summary>
    /// The processes an update waits for: this one, and on macOS the one that relaunched it for libmpv
    /// (<paramref name="relaunched"/>), which waits for this one and then exits too.
    /// </summary>
    IReadOnlyList<int> ProcessesToWaitFor(bool relaunched);

    // Running and pausing ffmpeg

    /// <summary>
    /// Starts <paramref name="executable"/> so that <see cref="Freeze"/> can pause it later. Its stdout lines go to
    /// <paramref name="output"/>, its stderr to <paramref name="error"/>, and <paramref name="started"/> gets its pid.
    /// </summary>
    CommandTask<CommandResult> StartPausable(string executable, IReadOnlyList<string> arguments, Action<int> started,
        Action<string> output, Action<string> error, CancellationToken ct);

    /// <summary>Freezes the process until <see cref="IFrozenProcess.Resume"/>. It never stays frozen after this app dies.</summary>
    IFrozenProcess Freeze(int pid);

    // Power

    /// <summary>
    /// Keeps the computer from going to sleep on its own until disposed; the display can still sleep. Best-effort:
    /// when the system refuses, the failure goes to the log and the handle does nothing.
    /// </summary>
    IDisposable KeepAwake(string reason);

    // Notifications

    /// <summary>Shows a desktop notification. Best-effort: failures are swallowed.</summary>
    Task NotifyAsync(string title, string body);

    // .mfvp files

    /// <summary>Whether this copy can register itself as the .mfvp handler: the packaged app, not <c>dotnet ManeFfmpeg.dll</c>.</summary>
    bool CanRegisterProjectFiles { get; }

    Task<bool> IsProjectFileHandlerAsync();

    Task RegisterProjectFilesAsync();

    /// <summary>Hands .mfvp files back to whatever opens JSON.</summary>
    Task UnregisterProjectFilesAsync();

    // File manager

    /// <summary>Shows <paramref name="path"/> in the file manager, selected where the platform allows.</summary>
    void RevealInFileManager(string path);

    // Updates

    /// <summary>The release asset's extension: ".zip", ".tar.gz".</summary>
    string UpdateAssetExtension { get; }

    Task UnpackUpdateAsync(string archive, string folder, CancellationToken ct);

    /// <summary>The package's top entry once unpacked: what an update stages.</summary>
    string UpdateStagedName { get; }

    /// <summary>Where the app's executable is in a staged package.</summary>
    string UpdateExecutableIn(string staged);

    /// <summary>What an update replaces when the app runs from <paramref name="processPath"/>; null when it isn't a package.</summary>
    string? UpdateInstalledPath(string processPath);

    /// <summary>Why the platform won't let the copy at <paramref name="installed"/> be replaced, or null.</summary>
    string? UpdateCannotReplaceReason(string installed);

    /// <summary>Writes the script that swaps the update in (see <see cref="UpdatePlan"/>) and returns its path.</summary>
    string WriteUpdateScript(string directory, UpdatePlan plan);

    /// <summary>Starts the script from <see cref="WriteUpdateScript"/> in the background; it outlives this process.</summary>
    void StartUpdateScript(string script);

    // Setup

    /// <summary>Installs missing tools (Homebrew, winget), or null where the app doesn't offer to.</summary>
    IToolInstaller? ToolInstaller { get; }

    // Windows

    /// <summary>
    /// Moves a window's top edge down by <paramref name="topDown"/> and grows its height by <paramref name="grow"/>
    /// in one native call, in points. False when the platform has no such call; the caller then sets both itself.
    /// </summary>
    bool TryChangeWindowFrame(nint handle, string? handleDescriptor, double topDown, double grow);
}

/// <summary>A process frozen by <see cref="IPlatform.Freeze"/>.</summary>
public interface IFrozenProcess
{
    /// <summary>Lets the process run again.</summary>
    void Resume();

    /// <summary>
    /// The command has ended, possibly while frozen (cancelled while paused): stops watching it. Processes it started that
    /// are still frozen are ended, since nothing would resume them.
    /// </summary>
    void Release();
}

/// <summary>What the update script does.</summary>
/// <param name="Installed">The copy to replace.</param>
/// <param name="Staged">The unpacked package (<see cref="IPlatform.UpdateStagedName"/>).</param>
/// <param name="WaitFor">Processes that must exit before anything is replaced.</param>
/// <param name="Relaunch">Start the app again once installed, opening <paramref name="Reopen"/>.</param>
/// <param name="Log">The script appends its output here.</param>
/// <param name="CleanUp">A folder to delete at the end (the downloaded update).</param>
public sealed record UpdatePlan(string Installed, string Staged, Version Version, IReadOnlyList<int> WaitFor, bool Relaunch,
    IReadOnlyList<string> Reopen, string Log, string? CleanUp);

/// <summary>
/// Installs missing tools with the platform's package manager, which may itself need installing first: Homebrew runs its
/// installer in Terminal, winget comes from the Microsoft Store.
/// </summary>
public interface IToolInstaller
{
    /// <summary>"Homebrew", "winget".</summary>
    string ManagerName { get; }

    /// <summary>The dialog's introduction.</summary>
    string Introduction { get; }

    /// <summary>What happens when the package manager is installed first.</summary>
    string ManagerSetupExplanation { get; }

    /// <summary>The command (or place) that installs the package manager, for display.</summary>
    string ManagerInstallCommand { get; }

    /// <summary>The package manager's executable, or null while it's missing.</summary>
    string? FindManager();

    /// <summary>Starts installing the package manager; returns what to tell the user while it runs.</summary>
    Task<string> StartManagerInstallAsync(string workDirectory);

    /// <summary>Waits for the package manager's installer and returns its exit code.</summary>
    Task<int> WaitForManagerInstallAsync(string workDirectory, CancellationToken ct);

    /// <summary>Why the package manager still isn't usable after its installer ended with <paramref name="code"/>.</summary>
    string DescribeManagerInstallFailure(int code);

    /// <summary>
    /// The package that provides <paramref name="component"/> ("ffmpeg", "libmpv"), or null when it doesn't need
    /// installing here (Windows builds bundle libmpv).
    /// </summary>
    string? PackageFor(string component);

    /// <summary>The install command for <paramref name="packages"/>, for display.</summary>
    string InstallCommand(IEnumerable<string> packages);

    /// <summary>Shown while packages install.</summary>
    string InstallingHint { get; }

    /// <summary>
    /// Installs <paramref name="packages"/> with <paramref name="manager"/>, passing each output line to
    /// <paramref name="output"/>; returns the exit code. Cancelling interrupts it like Ctrl-C, then kills it.
    /// </summary>
    Task<int> InstallAsync(string manager, IReadOnlyList<string> packages, Action<string> output, CancellationToken ct);
}
