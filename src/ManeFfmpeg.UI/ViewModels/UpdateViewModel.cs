using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManeFfmpeg.Core.Persistence;
using ManeFfmpeg.Core.Updates;

namespace ManeFfmpeg.UI.ViewModels;

public enum UpdateStep
{
    /// <summary>The release and its changelog are shown.</summary>
    Offered,
    Downloading,
    /// <summary>Downloaded and unpacked; restarting installs it.</summary>
    Ready,
    Failed,
}

/// <summary>What the user chose in the update window.</summary>
public enum UpdateChoice
{
    None,
    RestartNow,
    InstallOnQuit,
}

/// <summary>
/// Offers a newer release: shows what changed since this version, downloads it, and asks before the app restarts to
/// install it. A copy that can't replace itself sends the user to the release page instead.
/// </summary>
public sealed partial class UpdateViewModel : ViewModelBase, IDisposable
{
    private readonly HttpClient _http;
    private readonly string? _cannotInstall;
    private CancellationTokenSource? _cts;

    /// <param name="current">The version the offer was checked against (this build's).</param>
    /// <param name="installed">What the update replaces (<see cref="UpdateInstaller.InstalledPath"/>).</param>
    public UpdateViewModel(UpdateOffer offer, Version current, HttpClient http, string? installed)
    {
        Offer = offer;
        Current = current;
        _http = http;
        _cannotInstall = offer.Asset is null
            ? $"This release has no download for {UpdateInstaller.RuntimeId ?? "this system"}."
            : UpdateInstaller.CannotInstallReason(installed);
        Status = _cannotInstall;
    }

    public UpdateOffer Offer { get; }

    public Version Current { get; }

    public string Heading => $"Mane FFmpeg {Offer.Version.ToString(3)} is available";

    public string Subheading => $"You have {Current.ToString(3)}. What's new:";

    public string Changelog => Offer.Changelog ?? "See the release page for what's new.";

    public Action? RequestClose { get; set; }

    /// <summary>Opens the release page in the browser.</summary>
    public Func<Uri, Task>? OpenPage { get; set; }

    public UpdateChoice Choice { get; private set; }

    public PreparedUpdate? Prepared { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryText), nameof(SecondaryText), nameof(IsDownloading), nameof(IsReady), nameof(ShowDoNotRemind))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryCommand))]
    public partial UpdateStep Step { get; private set; } = UpdateStep.Offered;

    /// <summary>"Do not remind for this version": this release isn't offered again, a newer one is.</summary>
    [ObservableProperty]
    public partial bool DoNotRemind { get; set; }

    [ObservableProperty]
    public partial double Progress { get; private set; }

    [ObservableProperty]
    public partial string? Status { get; private set; }

    [ObservableProperty]
    public partial bool StatusIsError { get; private set; }

    public bool CanInstall => _cannotInstall is null;
    public bool IsDownloading => Step == UpdateStep.Downloading;
    public bool IsReady => Step == UpdateStep.Ready;
    public bool ShowDoNotRemind => Step is UpdateStep.Offered or UpdateStep.Failed;

    public string PrimaryText => Step switch
    {
        UpdateStep.Ready => "Restart Now",
        UpdateStep.Failed => "Open Download Page",
        _ => CanInstall ? "Update" : "Open Download Page",
    };

    public string SecondaryText => Step switch
    {
        UpdateStep.Downloading => "Cancel",
        UpdateStep.Ready => "Install on Quit",
        UpdateStep.Failed => "Close",
        _ => "Later",
    };

    private bool CanPrimary => Step != UpdateStep.Downloading;

    [RelayCommand(CanExecute = nameof(CanPrimary))]
    private async Task Primary()
    {
        if (Step == UpdateStep.Ready)
        {
            Close(UpdateChoice.RestartNow);
            return;
        }
        if (Step == UpdateStep.Failed || !CanInstall)
        {
            if (OpenPage is not null)
                await OpenPage(Offer.Page);
            return;
        }
        await DownloadAsync();
    }

    [RelayCommand]
    private void Secondary()
    {
        if (Step == UpdateStep.Downloading)
        {
            _cts?.Cancel();
            return;
        }
        Close(Step == UpdateStep.Ready ? UpdateChoice.InstallOnQuit : UpdateChoice.None);
    }

    private async Task DownloadAsync()
    {
        Step = UpdateStep.Downloading;
        StatusIsError = false;
        Status = "Downloading…";
        Progress = 0;
        _cts = new CancellationTokenSource();
        var progress = new Progress<double>(p =>
        {
            Progress = p * 100;
            Status = $"Downloading… {p * Offer.Asset!.Size / 1e6:0} of {Offer.Asset.Size / 1e6:0} MB";
        });
        try
        {
            Prepared = await UpdateInstaller.PrepareAsync(_http, Offer.Asset!, Offer.Version, progress, _cts.Token);
            Step = UpdateStep.Ready;
            Status = "The update is ready. Mane FFmpeg restarts to install it: each window asks to save its changes first, " +
                     "and saved projects open again after the restart.";
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            UpdateInstaller.CleanUp();
            Step = UpdateStep.Offered;
            Status = null;
        }
        catch (Exception ex)
        {
            AppLog.Write($"Update: downloading {Offer.Version.ToString(3)} failed: {ex}");
            UpdateInstaller.CleanUp();
            Step = UpdateStep.Failed;
            StatusIsError = true;
            Status = $"Couldn't download the update: {ex.Message}";
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
        }
    }

    private void Close(UpdateChoice choice)
    {
        Choice = choice;
        RequestClose?.Invoke();
    }

    /// <summary>Stops a download in progress (the window is closing).</summary>
    public void Dispose() => _cts?.Cancel();
}
