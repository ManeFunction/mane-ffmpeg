using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManeFfmpeg.App.Services;
using ManeFfmpeg.Core;
using ManeFfmpeg.Core.Mpv;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.App.ViewModels;

public enum SetupStep
{
    /// <summary>Homebrew is missing; its installer is offered first.</summary>
    NeedsHomebrew,
    /// <summary>Homebrew's installer runs in Terminal.</summary>
    WaitingForHomebrew,
    Ready,
    Installing,
    Installed,
    Failed,
}

/// <summary>A missing tool and the Homebrew formula that provides it.</summary>
public sealed partial class SetupComponent(string name, string formula, string purpose) : ObservableObject
{
    public string Name { get; } = name;
    public string Formula { get; } = formula;
    public string Purpose { get; } = purpose;

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = true;
}

/// <summary>
/// Installs missing components (ffmpeg, libmpv) with Homebrew on macOS, and Homebrew itself first when it's missing.
/// Homebrew's installer runs in Terminal; <c>brew install</c> runs here with its output shown.
/// </summary>
public sealed partial class SetupViewModel : ViewModelBase, IDisposable
{
    private const int LogLines = 300;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly AppServices _app = AppServices.Instance;
    private readonly Func<string?> _findBrew;
    private readonly Func<string, Task> _openInTerminal;
    private readonly string _workDirectory;
    private readonly Queue<string> _log = new();
    private CancellationTokenSource? _cts;
    private string? _brew;

    /// <param name="components">What to offer; <see cref="MissingComponents"/> by default.</param>
    /// <param name="findBrew">Finds brew; tests and debug automation replace it and the next two.</param>
    /// <param name="openInTerminal">Runs Homebrew's installer script in a terminal.</param>
    /// <param name="workDirectory">Where the installer script and its status file go.</param>
    public SetupViewModel(IEnumerable<SetupComponent>? components = null, Func<string?>? findBrew = null,
        Func<string, Task>? openInTerminal = null, string? workDirectory = null)
    {
        _findBrew = findBrew ?? Homebrew.Find;
        _openInTerminal = openInTerminal ?? Homebrew.OpenInTerminalAsync;
        _workDirectory = workDirectory ?? Path.Combine(AppPaths.TempDirectory, "homebrew");
        Components = [.. components ?? MissingComponents()];
        foreach (var component in Components)
            component.PropertyChanged += (_, _) => OnSelectionChanged();
        _brew = _findBrew();
        Step = _brew is null ? SetupStep.NeedsHomebrew : SetupStep.Ready;
        AskAtStartup = _app.Settings.Current.AskToInstallAtStartup;
        AppLog.Write($"Setup: offering {string.Join(", ", Components.Select(c => c.Name))}; brew: {_brew ?? "not found"}");
    }

    /// <summary>Whether something this dialog installs is missing. Homebrew installs are offered on macOS only.</summary>
    public static bool IsNeeded => OperatingSystem.IsMacOS() && MissingComponents().Any();

    public static bool ShouldAskAtStartup => IsNeeded && AppServices.Instance.Settings.Current.AskToInstallAtStartup;

    /// <summary>Everything the dialog can install.</summary>
    public static IEnumerable<SetupComponent> AllComponents() =>
    [
        new SetupComponent("ffmpeg", "ffmpeg", "Reads, analyzes and exports your videos. Required."),
        new SetupComponent("libmpv", "mpv", "Plays the preview. Editing and exporting work without it."),
    ];

    public static IEnumerable<SetupComponent> MissingComponents() => AllComponents().Where(c => c.Formula == "ffmpeg"
        ? AppServices.Instance.Tools is null
        : !MpvLibrary.IsLoaded);

    public IReadOnlyList<SetupComponent> Components { get; }

    /// <summary>Closes the dialog ("Done", "Not Now").</summary>
    public Action? RequestClose { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHomebrew), nameof(CanEditComponents), nameof(IsInstalling), nameof(IsInstalled),
        nameof(PrimaryText), nameof(SecondaryText), nameof(HasSecondary))]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    public partial SetupStep Step { get; private set; }

    [ObservableProperty]
    public partial string? Status { get; private set; }

    [ObservableProperty]
    public partial bool StatusIsError { get; private set; }

    /// <summary>brew's recent output; null until an install starts.</summary>
    [ObservableProperty]
    public partial string? Log { get; private set; }

    [ObservableProperty]
    public partial bool AskAtStartup { get; set; }

    partial void OnAskAtStartupChanged(bool value)
    {
        if (_app.Settings.Current.AskToInstallAtStartup != value)
            _app.Settings.Update(s => s with { AskToInstallAtStartup = value });
    }

    public bool ShowHomebrew => Step is SetupStep.NeedsHomebrew or SetupStep.WaitingForHomebrew;

    public bool CanEditComponents => Step is SetupStep.NeedsHomebrew or SetupStep.Ready or SetupStep.Failed;

    public bool IsInstalling => Step == SetupStep.Installing;

    public bool IsInstalled => Step == SetupStep.Installed;

    public string HomebrewCommand => Homebrew.InstallerCommand;

    public string InstallCommand => $"brew install {string.Join(' ', Selected.Select(c => c.Formula))}";

    public string PrimaryText => Step switch
    {
        SetupStep.NeedsHomebrew => "Install Homebrew…",
        SetupStep.WaitingForHomebrew => "Waiting for Homebrew…",
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
        SetupStep.WaitingForHomebrew or SetupStep.Installing => false,
        SetupStep.Installed => true,
        _ => Selected.Any(),
    };

    [RelayCommand(CanExecute = nameof(CanContinue))]
    private async Task Continue()
    {
        switch (Step)
        {
            case SetupStep.NeedsHomebrew:
                await InstallHomebrewAsync();
                break;
            case SetupStep.Installed:
                RequestClose?.Invoke();
                break;
            default:
                await InstallComponentsAsync();
                break;
        }
    }

    /// <summary>"Not Now" and "Close" close the dialog; "Stop" stops brew.</summary>
    [RelayCommand]
    private void Dismiss()
    {
        if (IsInstalling)
            _cts?.Cancel();
        else
            RequestClose?.Invoke();
    }

    private async Task InstallHomebrewAsync()
    {
        ShowStatus(null);
        try
        {
            await _openInTerminal(Homebrew.WriteInstallerScript(_workDirectory));
        }
        catch (Exception ex)
        {
            ShowStatus($"Couldn't open Terminal: {ex.Message}", error: true);
            return;
        }
        AppLog.Write("Setup: Homebrew's installer opened in Terminal");
        Step = SetupStep.WaitingForHomebrew;
        ShowStatus("Follow the steps in Terminal. This window continues once Homebrew is installed.");

        int code;
        _cts = new CancellationTokenSource();
        try
        {
            code = await WaitForInstallerAsync(_cts.Token);
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
        AppLog.Write($"Setup: Homebrew's installer exited with {code}");
        _brew = code == 0 ? _findBrew() : null;
        if (_brew is not null)
        {
            Step = SetupStep.Ready;
            ShowStatus("Homebrew is installed. Choose Install to continue.");
            return;
        }
        Step = SetupStep.NeedsHomebrew;
        ShowStatus(code switch
        {
            0 => "Homebrew's installer finished, but brew wasn't found in /opt/homebrew or /usr/local.",
            129 or 130 or 143 => "Homebrew's installer was stopped before it finished.",
            _ => $"Homebrew's installer failed (exit code {code}). Terminal shows what went wrong.",
        }, error: true);
    }

    private async Task<int> WaitForInstallerAsync(CancellationToken ct)
    {
        while (true)
        {
            if (Homebrew.ReadInstallerStatus(_workDirectory) is { } code)
                return code;
            await Task.Delay(PollInterval, ct);
        }
    }

    private async Task InstallComponentsAsync()
    {
        var chosen = Selected.ToList();
        var names = string.Join(" and ", chosen.Select(c => c.Name));
        Step = SetupStep.Installing;
        lock (_log)
            _log.Clear();
        Log = "";
        ShowStatus($"Installing {names}. This usually takes a few minutes; on older macOS versions Homebrew builds " +
                   "from source, which can take an hour or more.");
        AppLog.Write($"Setup: {InstallCommand}");

        int code;
        _cts = new CancellationTokenSource();
        IProgress<string> progress = new Progress<string>(AppendLog);
        try
        {
            code = await Homebrew.InstallAsync(_brew!, [.. chosen.Select(c => c.Formula)], progress.Report, _cts.Token);
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
            ShowStatus($"Couldn't run Homebrew: {ex.Message}", error: true);
            return;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
        }
        AppLog.Write($"Setup: brew exited with {code}");
        if (code != 0)
        {
            Step = SetupStep.Failed;
            ShowStatus($"Homebrew couldn't install {names} (exit code {code}). Its output above shows why.", error: true);
            return;
        }

        // libmpv first: the ToolsChanged raised when ffmpeg is located again also starts the open windows' previews.
        _app.LoadMpv();
        await _app.LocateToolsAsync();
        var missing = MissingComponents().Select(c => c.Formula).ToHashSet();
        var unusable = chosen.Where(c => missing.Contains(c.Formula)).ToList();
        if (unusable.Count == 0)
        {
            Step = SetupStep.Installed;
            var ready = chosen.Select(c => c.Formula == "ffmpeg" && _app.Tools?.Version is { } v ? $"ffmpeg {v}" : c.Name);
            ShowStatus($"✓ {string.Join(" and ", ready)} {(chosen.Count == 1 ? "is" : "are")} ready.");
            return;
        }
        Step = SetupStep.Failed;
        var mpvError = unusable.Any(c => c.Formula == "mpv") ? $" {MpvLibrary.LoadError}" : "";
        ShowStatus($"Homebrew installed {string.Join(" and ", unusable.Select(c => c.Name))}, but the app still can't use it. " +
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

    /// <summary>Stops waiting for Homebrew's installer, or stops brew.</summary>
    public void Dispose() => _cts?.Cancel();
}
