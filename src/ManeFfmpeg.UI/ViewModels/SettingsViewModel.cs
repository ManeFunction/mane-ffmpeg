using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManeFfmpeg.UI.Services;
using ManeFfmpeg.Core.Desktop;
using ManeFfmpeg.Core.Mpv;
using ManeFfmpeg.Core.Notifications;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.UI.ViewModels;

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
        AskToRegisterProjectFiles = s.AskToRegisterProjectFiles;
        RefreshStatus();
        _ = RefreshProjectFilesAsync();
    }

    public IReadOnlyList<ThemeChoice> Themes { get; } = Enum.GetValues<ThemeChoice>();

    [ObservableProperty]
    public partial string FfmpegPath { get; set; }

    [ObservableProperty]
    public partial string FfprobePath { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayerRestartNeeded))]
    public partial string LibMpvPath { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayerRestartNeeded))]
    public partial bool HardwareDecoding { get; set; }

    /// <summary>The player reads these only at startup, so changing them needs a restart.</summary>
    public bool PlayerRestartNeeded =>
        (_app.Settings.Current.LibMpvPath ?? "") != LibMpvPath.Trim() || _app.Settings.Current.HardwareDecoding != HardwareDecoding;

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

    /// <summary>Whether this copy can make itself the app that opens .mfvp projects (see <see cref="ProjectFileAssociation"/>).</summary>
    public bool CanRegisterProjectFiles { get; } = ProjectFileAssociation.IsSupported;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProjectFilesAction))]
    public partial bool ProjectFilesRegistered { get; private set; }

    [ObservableProperty]
    public partial string ProjectFilesStatus { get; private set; } = "";

    public string ProjectFilesAction => ProjectFilesRegistered ? "Unregister" : "Register";

    /// <summary>The startup offer, which its "Do not show anymore" checkbox turns off.</summary>
    [ObservableProperty]
    public partial bool AskToRegisterProjectFiles { get; set; }

    partial void OnThemeChanged(ThemeChoice value) => App.ApplyTheme(value);

    /// <summary>The theme shows as soon as it's picked; closing without Apply goes back to the saved one.</summary>
    public void DiscardThemePreview() => App.ApplyTheme(_app.Settings.Current.Theme);

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

    /// <summary>Saves everything and looks for the tools again; the window closes when it's done.</summary>
    public async Task ApplyAsync()
    {
        IsBusy = true;
        _app.Settings.Update(s => s with
        {
            FfmpegPath = NullIfEmpty(FfmpegPath),
            FfprobePath = NullIfEmpty(FfprobePath),
            LibMpvPath = NullIfEmpty(LibMpvPath),
            HardwareDecoding = HardwareDecoding,
            Notifications = Notifications,
            Theme = Theme,
            AskToRegisterProjectFiles = AskToRegisterProjectFiles,
        });
        await _app.LocateToolsAsync();
        IsBusy = false;
    }

    /// <summary>Applies at once, like the other buttons here, not with Apply.</summary>
    [RelayCommand(CanExecute = nameof(CanRegisterProjectFiles))]
    private async Task ToggleProjectFiles()
    {
        try
        {
            if (ProjectFilesRegistered)
                await ProjectFileAssociation.UnregisterAsync();
            else
                await ProjectFileAssociation.RegisterAsync();
            await RefreshProjectFilesAsync();
        }
        catch (Exception ex)
        {
            AppLog.Write($"Changing the .{ProjectFile.Extension} association failed: {ex.Message}");
            ProjectFilesStatus = $"Couldn't change it: {ex.Message}";
        }
    }

    private async Task RefreshProjectFilesAsync()
    {
        if (!CanRegisterProjectFiles)
        {
            ProjectFilesStatus = $"Only the packaged app can open .{ProjectFile.Extension} projects on double-click.";
            return;
        }
        ProjectFilesRegistered = await ProjectFileAssociation.IsRegisteredAsync();
        ProjectFilesStatus = ProjectFilesRegistered
            ? $"Double-clicking a .{ProjectFile.Extension} project opens it in Mane FFmpeg."
            : $"Double-clicking a .{ProjectFile.Extension} project doesn't open it in Mane FFmpeg.";
    }

    [RelayCommand]
    private Task TestNotification() => SystemNotifier.NotifyAsync("Mane FFmpeg", "Notifications work. You'll see one when an export finishes.");

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
