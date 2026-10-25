using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManeFfmpeg.App.Services;
using ManeFfmpeg.Core.Mpv;
using ManeFfmpeg.Core.Notifications;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.App.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly AppServices _app = AppServices.Instance;

    public SettingsViewModel()
    {
        var s = _app.Settings.Current;
        FfmpegPath = s.FfmpegPath ?? "";
        FfprobePath = s.FfprobePath ?? "";
        LibMpvPath = s.LibMpvPath ?? "";
        HardwareDecoding = s.HardwareDecoding;
        Notifications = s.Notifications;
        Theme = s.Theme;
        RefreshStatus();
    }

    public IReadOnlyList<ThemeChoice> Themes { get; } = Enum.GetValues<ThemeChoice>();

    [ObservableProperty]
    public partial string FfmpegPath { get; set; }

    [ObservableProperty]
    public partial string FfprobePath { get; set; }

    [ObservableProperty]
    public partial string LibMpvPath { get; set; }

    [ObservableProperty]
    public partial bool HardwareDecoding { get; set; }

    [ObservableProperty]
    public partial bool Notifications { get; set; }

    [ObservableProperty]
    public partial ThemeChoice Theme { get; set; }

    [ObservableProperty]
    public partial string FfmpegStatus { get; private set; } = "";

    [ObservableProperty]
    public partial string MpvStatus { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    /// <summary>Something can be installed with Homebrew (see <see cref="SetupViewModel"/>).</summary>
    [ObservableProperty]
    public partial bool CanInstall { get; private set; }

    partial void OnThemeChanged(ThemeChoice value) => App.ApplyTheme(value);

    public void RefreshStatus()
    {
        CanInstall = SetupViewModel.IsNeeded;
        FfmpegStatus = _app.Tools is { } tools
            ? $"{tools.VersionText}\n{tools.Ffmpeg}\n{tools.Ffprobe}"
            : _app.ToolsError ?? "Not found";
        MpvStatus = MpvLibrary.IsLoaded
            ? $"libmpv {MpvLibrary.ApiVersion} (client API)\n{MpvLibrary.LoadedPath}"
            : $"{MpvLibrary.LoadError}\n{MpvLibrary.InstallHint}";
    }

    [RelayCommand]
    private async Task Apply()
    {
        IsBusy = true;
        var mpvChanged = (_app.Settings.Current.LibMpvPath ?? "") != LibMpvPath.Trim();
        var hwChanged = _app.Settings.Current.HardwareDecoding != HardwareDecoding;
        _app.Settings.Update(s => s with
        {
            FfmpegPath = NullIfEmpty(FfmpegPath),
            FfprobePath = NullIfEmpty(FfprobePath),
            LibMpvPath = NullIfEmpty(LibMpvPath),
            HardwareDecoding = HardwareDecoding,
            Notifications = Notifications,
            Theme = Theme,
        });
        await _app.LocateToolsAsync();
        RefreshStatus();
        if (mpvChanged || hwChanged)
            MpvStatus += "\nRestart the app to use the new player settings.";
        IsBusy = false;
    }

    [RelayCommand]
    private Task TestNotification() => SystemNotifier.NotifyAsync("Mane FFmpeg", "Notifications work. You'll see one when an export finishes.");

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
