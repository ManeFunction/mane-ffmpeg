using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using ManeFfmpeg.Core.Persistence;
using ManeFfmpeg.Core.Updates;
using ManeFfmpeg.UI.ViewModels;
using ManeFfmpeg.UI.Views;

namespace ManeFfmpeg.UI.Services;

/// <summary>
/// Offers a newer GitHub release once per launch, or whenever the user asks from the menu. A downloaded update installs
/// when the app restarts for it, or when the app quits if the user chose "Install on Quit".
/// </summary>
public sealed class Updater
{
    /// <summary>Debug builds look for updates only with this set, pretending to be the given version (e.g. 0.8.0).</summary>
    private const string PretendVersionVariable = "MANE_FFMPEG_PRETEND_VERSION";

    private readonly HttpClient _http = UpdateChecker.CreateHttpClient();
    private readonly string? _installed = UpdateInstaller.InstalledPath();
    private PreparedUpdate? _pending;
    private bool _applied;

    public static Updater Instance { get; } = new();

    private static Version CurrentVersion =>
#if DEBUG
        AppVersion.Parse(Environment.GetEnvironmentVariable(PretendVersionVariable) ?? "") ?? AppVersion.Current;
#else
        AppVersion.Current;
#endif

    /// <summary>
    /// True from the start of a check until its outcome is dealt with: no newer release, or the update window closed.
    /// Only one check/offer runs at a time. Raised and read on the UI thread.
    /// </summary>
    public bool IsBusy { get; private set; }

    public event Action? BusyChanged;

    private void SetBusy(bool busy)
    {
        if (IsBusy == busy)
            return;
        IsBusy = busy;
        BusyChanged?.Invoke();
    }

    /// <summary>
    /// Looks for a newer release and, if there is one, shows the update window. The updater is busy from the start of
    /// the check until the window is closed (or there is nothing to offer), and never longer: this one call owns it.
    /// Does nothing when another check is already running, or when automatic checks are off and the user didn't ask.
    /// </summary>
    /// <param name="manual">The user asked: look even when automatic checks are off, and offer a release they skipped.</param>
    /// <param name="offerAfter">Work the window must wait for (the startup dialogs); the check itself runs meanwhile.</param>
    public async Task CheckAndOfferAsync(bool manual = false, Task? offerAfter = null)
    {
        if (IsBusy)
            return;
        var settings = AppServices.Instance.Settings.Current;
#if DEBUG
        if (Environment.GetEnvironmentVariable(PretendVersionVariable) is null)
            return;
#endif
        if (!manual && !settings.CheckForUpdates)
            return;
        SetBusy(true);
        try
        {
            var offer = await LookUpAsync(settings, manual);
            if (offerAfter is not null)
                await offerAfter;
            if (offer is not null)
                await ShowOfferAsync(offer);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<UpdateOffer?> LookUpAsync(AppSettings settings, bool manual)
    {
        UpdateInstaller.CleanUp();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var offer = await UpdateChecker.CheckAsync(_http, CurrentVersion, timeout.Token);
            if (offer is null)
            {
                AppLog.Write($"Update: {CurrentVersion.ToString(3)} is the latest release");
                return null;
            }
            if (!manual && settings.SkippedUpdateVersion == offer.Version.ToString(3))
            {
                AppLog.Write($"Update: {offer.Version.ToString(3)} is available, but the user skipped it");
                return null;
            }
            AppLog.Write($"Update: {offer.Version.ToString(3)} is available ({offer.Asset?.Name ?? "no download for this system"})");
            return offer;
        }
        catch (Exception ex)
        {
            AppLog.Write($"Update: couldn't check for updates: {ex.Message}");
            return null;
        }
    }

    /// <summary>Shows the update window for <paramref name="offer"/> and carries out what the user picks there.</summary>
    private async Task ShowOfferAsync(UpdateOffer offer)
    {
        if (WindowManager.Instance.Windows.LastOrDefault() is not { } owner)
            return;
        var vm = new UpdateViewModel(offer, CurrentVersion, _http, _installed);
        await new UpdateWindow(vm).ShowDialog(owner);

        if (vm.Choice == UpdateChoice.None && vm.DoNotRemind)
        {
            AppLog.Write($"Update: not reminding about {offer.Version.ToString(3)} again");
            AppServices.Instance.Settings.Update(s => s with { SkippedUpdateVersion = offer.Version.ToString(3) });
        }
        if (vm.Prepared is not { } prepared || vm.Choice == UpdateChoice.None)
        {
            UpdateInstaller.CleanUp();
            return;
        }
        _pending = prepared;
        if (vm.Choice == UpdateChoice.RestartNow)
            await RestartAsync();
    }

    /// <summary>
    /// Closes every window the way the user would (saving or stopping what they ask to), then quits so the update can
    /// replace the app and start it again with the saved projects. Nothing closes, and no export stops, until every
    /// window has agreed.
    /// </summary>
    private async Task RestartAsync()
    {
        var windows = WindowManager.Instance.Windows.ToList();
        foreach (var window in windows)
        {
            window.Activate();
            if (await window.ConfirmCloseAsync(stopExport: false))
                continue;
            AppLog.Write("Update: restart cancelled; installing on quit instead");
            await Dialogs.InfoAsync(window, "Update postponed",
                $"Mane FFmpeg {_pending!.Version.ToString(3)} will be installed when you quit the app.");
            return;
        }
        var reopen = windows.Select(w => w.ViewModel.FilePath).OfType<string>().Distinct().ToList();
        if (!TryApply(relaunch: true, reopen))
        {
            await Dialogs.InfoAsync(windows[0], "Couldn't install the update", $"See {UpdateInstaller.LogPath} for details.");
            return;
        }
        foreach (var window in windows)
        {
            window.StopExport();
            window.ForceClose();
        }
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    /// <summary>Installs a downloaded update after the app has exited ("Install on Quit"). Call when the app exits.</summary>
    public void ApplyPendingOnExit() => TryApply(relaunch: false, []);

    private bool TryApply(bool relaunch, IReadOnlyList<string> reopen)
    {
        if (_pending is null || _applied || _installed is null)
            return false;
        try
        {
            UpdateInstaller.Apply(_pending, _installed, ProcessesToWaitFor(), relaunch, reopen);
            _applied = true;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write($"Update: couldn't start installing: {ex}");
            return false;
        }
    }

    /// <summary>This process, and on macOS the one Program relaunched it from: it waits for this one and then exits too.</summary>
    private static List<int> ProcessesToWaitFor()
    {
        List<int> pids = [Environment.ProcessId];
        if (Environment.GetEnvironmentVariable(Program.RelaunchedVariable) == "1")
            pids.Add(UpdateInstaller.ParentProcessId());
        return pids;
    }
}
