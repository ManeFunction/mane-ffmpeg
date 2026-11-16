using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManeFfmpeg.UI.Services;
using ManeFfmpeg.Core.Mpv;
using ManeFfmpeg.Core.Persistence;
using ManeFfmpeg.Core.Platforms;

namespace ManeFfmpeg.UI.ViewModels;

public enum SetupStep
{
    /// <summary>The package manager is missing; installing it is offered first.</summary>
    NeedsManager,
    /// <summary>The package manager's installer runs (in Terminal, or the Microsoft Store).</summary>
    WaitingForManager,
    Ready,
    Installing,
    Installed,
    Failed,
}

/// <summary>A missing tool and the package that provides it.</summary>
public sealed partial class SetupComponent(string name, string package, string purpose) : ObservableObject
{
    /// <summary>"ffmpeg" or "libmpv".</summary>
    public string Name { get; } = name;
    public string Package { get; } = package;
    public string Purpose { get; } = purpose;

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = true;
}

/// <summary>
/// Installs missing components (ffmpeg, libmpv) with the platform's package manager (<see cref="IToolInstaller"/>:
/// Homebrew on macOS, winget on Windows), and the package manager itself first when it's missing. The install runs here
/// with its output shown.
/// </summary>
public sealed partial class SetupViewModel : ViewModelBase, IDisposable
{
    private const int LogLines = 300;

    private readonly AppServices _app = AppServices.Instance;
    private readonly IToolInstaller _installer;
    private readonly string _workDirectory;
    private readonly Queue<string> _log = new();
    private CancellationTokenSource? _cts;
    private string? _manager;

    /// <param name="components">What to offer; <see cref="MissingComponents"/> by default.</param>
    /// <param name="installer">The package manager; the platform's by default. Tests and debug automation replace it.</param>
    /// <param name="workDirectory">Where the package manager's installer keeps its files.</param>
    public SetupViewModel(IEnumerable<SetupComponent>? components = null, IToolInstaller? installer = null, string? workDirectory = null)
    {
        _installer = installer ?? Platform.Current.ToolInstaller
            ?? throw new PlatformNotSupportedException("The app doesn't install tools on this system.");
        _workDirectory = workDirectory ?? Path.Combine(AppPaths.TempDirectory, "setup");
        Components = [.. components ?? MissingComponents(_installer)];
        foreach (var component in Components)
            component.PropertyChanged += (_, _) => OnSelectionChanged();
        _manager = _installer.FindManager();
        Step = _manager is null ? SetupStep.NeedsManager : SetupStep.Ready;
        AskAtStartup = _app.Settings.Current.AskToInstallAtStartup;
        AppLog.Write($"Setup: offering {string.Join(", ", Components.Select(c => c.Name))}; {_installer.ManagerName}: {_manager ?? "not found"}");
    }

    /// <summary>Whether something this dialog installs is missing, on a platform where it installs things.</summary>
    public static bool IsNeeded => Platform.Current.ToolInstaller is { } installer && MissingComponents(installer).Any();

    public static bool ShouldAskAtStartup => IsNeeded && AppServices.Instance.Settings.Current.AskToInstallAtStartup;

    /// <summary>Everything the dialog can install with <paramref name="installer"/>.</summary>
    public static IEnumerable<SetupComponent> AllComponents(IToolInstaller installer) =>
        new[]
        {
            ("ffmpeg", "Reads, analyzes and exports your videos. Required."),
            ("libmpv", "Plays the preview. Editing and exporting work without it."),
        }
        .Where(c => installer.PackageFor(c.Item1) is not null)
        .Select(c => new SetupComponent(c.Item1, installer.PackageFor(c.Item1)!, c.Item2));

    public static IEnumerable<SetupComponent> MissingComponents(IToolInstaller installer) =>
        AllComponents(installer).Where(c => !IsAvailable(c.Name));

    private static bool IsAvailable(string component) =>
        component == "ffmpeg" ? AppServices.Instance.Tools is not null : MpvLibrary.IsLoaded;

    public string ManagerName => _installer.ManagerName;

    public string Introduction => _installer.Introduction;

    public string ManagerTitle => $"{_installer.ManagerName.ToUpperInvariant()} ISN'T INSTALLED";

    public string ManagerExplanation => _installer.ManagerSetupExplanation;

    public IReadOnlyList<SetupComponent> Components { get; }

    /// <summary>Closes the dialog ("Done", "Not Now").</summary>
    public Action? RequestClose { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowManager), nameof(CanEditComponents), nameof(IsInstalling), nameof(IsInstalled),
        nameof(PrimaryText), nameof(SecondaryText), nameof(HasSecondary))]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    public partial SetupStep Step { get; private set; }

    [ObservableProperty]
    public partial string? Status { get; private set; }

    [ObservableProperty]
    public partial bool StatusIsError { get; private set; }

    /// <summary>The package manager's recent output; null until an install starts.</summary>
    [ObservableProperty]
    public partial string? Log { get; private set; }

    [ObservableProperty]
    public partial bool AskAtStartup { get; set; }

    partial void OnAskAtStartupChanged(bool value)
    {
        if (_app.Settings.Current.AskToInstallAtStartup != value)
            _app.Settings.Update(s => s with { AskToInstallAtStartup = value });
    }

    public bool ShowManager => Step is SetupStep.NeedsManager or SetupStep.WaitingForManager;

    public bool CanEditComponents => Step is SetupStep.NeedsManager or SetupStep.Ready or SetupStep.Failed;

    public bool IsInstalling => Step == SetupStep.Installing;

    public bool IsInstalled => Step == SetupStep.Installed;

    public string ManagerCommand => _installer.ManagerInstallCommand;

    public string InstallCommand => _installer.InstallCommand(Selected.Select(c => c.Package));

    public string PrimaryText => Step switch
    {
        SetupStep.NeedsManager => $"Install {_installer.ManagerName}…",
        SetupStep.WaitingForManager => $"Waiting for {_installer.ManagerName}…",
        SetupStep.Installing => "Installing…",
        SetupStep.Installed => "Done",
        SetupStep.Failed => "Try Again",
        _ => "Install",
    };

    public string SecondaryText => Step switch
    {
        SetupStep.Installing => "Stop",
        SetupStep.Failed => "Close",
        _ => "Not Now",
    };

    public bool HasSecondary => Step != SetupStep.Installed;

    private IEnumerable<SetupComponent> Selected => Components.Where(c => c.IsSelected);

    private bool CanContinue() => Step switch
    {
        SetupStep.WaitingForManager or SetupStep.Installing => false,
        SetupStep.Installed => true,
        _ => Selected.Any(),
    };

    [RelayCommand(CanExecute = nameof(CanContinue))]
    private async Task Continue()
    {
        switch (Step)
        {
            case SetupStep.NeedsManager:
                await InstallManagerAsync();
                break;
            case SetupStep.Installed:
                RequestClose?.Invoke();
                break;
            default:
                await InstallComponentsAsync();
                break;
        }
    }

    /// <summary>"Not Now" and "Close" close the dialog; "Stop" stops the install.</summary>
    [RelayCommand]
    private void Dismiss()
    {
        if (IsInstalling)
            _cts?.Cancel();
        else
            RequestClose?.Invoke();
    }

    private async Task InstallManagerAsync()
    {
        var name = _installer.ManagerName;
        ShowStatus(null);
        string waiting;
        try
        {
            waiting = await _installer.StartManagerInstallAsync(_workDirectory);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, error: true);
            return;
        }
        AppLog.Write($"Setup: {name}'s installer started");
        Step = SetupStep.WaitingForManager;
        ShowStatus(waiting);

        int code;
        _cts = new CancellationTokenSource();
        try
        {
            code = await _installer.WaitForManagerInstallAsync(_workDirectory, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
        }
        AppLog.Write($"Setup: {name}'s installer exited with {code}");
        _manager = code == 0 ? _installer.FindManager() : null;
        if (_manager is not null)
        {
            Step = SetupStep.Ready;
            ShowStatus($"{name} is installed. Choose Install to continue.");
            return;
        }
        Step = SetupStep.NeedsManager;
        ShowStatus(_installer.DescribeManagerInstallFailure(code), error: true);
    }

    private async Task InstallComponentsAsync()
    {
        var chosen = Selected.ToList();
        var names = string.Join(" and ", chosen.Select(c => c.Name));
        Step = SetupStep.Installing;
        lock (_log)
            _log.Clear();
        Log = "";
        ShowStatus($"Installing {names}. {_installer.InstallingHint}");
        AppLog.Write($"Setup: {InstallCommand}");

        int code;
        _cts = new CancellationTokenSource();
        IProgress<string> progress = new Progress<string>(AppendLog);
        try
        {
            code = await _installer.InstallAsync(_manager!, [.. chosen.Select(c => c.Package)], progress.Report, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            Step = SetupStep.Failed;
            ShowStatus("Installation stopped.");
            return;
        }
        catch (Exception ex)
        {
            Step = SetupStep.Failed;
            ShowStatus($"Couldn't run {_installer.ManagerName}: {ex.Message}", error: true);
            return;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
        }
        AppLog.Write($"Setup: {_installer.ManagerName} exited with {code}");
        if (code != 0)
        {
            Step = SetupStep.Failed;
            ShowStatus($"{_installer.ManagerName} couldn't install {names} (exit code {code}). Its output above shows why.", error: true);
            return;
        }

        // libmpv first: the ToolsChanged raised when ffmpeg is located again also starts the open windows' previews.
        _app.LoadMpv();
        await _app.LocateToolsAsync();
        var unusable = chosen.Where(c => !IsAvailable(c.Name)).ToList();
        if (unusable.Count == 0)
        {
            Step = SetupStep.Installed;
            var ready = chosen.Select(c => c.Name == "ffmpeg" && _app.Tools?.Version is { } v ? $"ffmpeg {v}" : c.Name);
            ShowStatus($"✓ {string.Join(" and ", ready)} {(chosen.Count == 1 ? "is" : "are")} ready.");
            return;
        }
        Step = SetupStep.Failed;
        var mpvError = unusable.Any(c => c.Name == "libmpv") ? $" {MpvLibrary.LoadError}" : "";
        ShowStatus($"{_installer.ManagerName} installed {string.Join(" and ", unusable.Select(c => c.Name))}, but the app still can't use it. " +
                   $"Check the paths in Settings.{mpvError}", error: true);
    }

    private void AppendLog(string line)
    {
        lock (_log)
        {
            _log.Enqueue(line);
            if (_log.Count > LogLines)
                _log.Dequeue();
            Log = string.Join('\n', _log);
        }
    }

    private void ShowStatus(string? text, bool error = false)
    {
        Status = text;
        StatusIsError = error;
    }

    private void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(InstallCommand));
        ContinueCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Stops waiting for the package manager's installer, or stops the install.</summary>
    public void Dispose() => _cts?.Cancel();
}
