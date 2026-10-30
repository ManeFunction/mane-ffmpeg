using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using ManeFfmpeg.UI.Services;
using ManeFfmpeg.UI.ViewModels;
using ManeFfmpeg.UI.Views;
using ManeFfmpeg.Core.Desktop;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.UI;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
            var services = AppServices.Instance;
            ApplyTheme(services.Settings.Current.Theme);
            services.LoadMpv();
            AppLog.Write(Core.Mpv.MpvLibrary.IsLoaded
                ? $"libmpv {Core.Mpv.MpvLibrary.ApiVersion} from {Core.Mpv.MpvLibrary.LoadedPath}"
                : $"libmpv not loaded: {Core.Mpv.MpvLibrary.LoadError}");
            if (NativeMenu.GetMenu(this)?.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == "Settings…") is { } settingsItem)
                settingsItem.Gesture = new KeyGesture(Key.OemComma, KeyModifiers.Meta);

            var first = WindowManager.Instance.OpenNew();
            desktop.MainWindow = first;
            _ = StartAsync(desktop.Args ?? [], first);

            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
                activatable.Activated += OnActivated;
            desktop.Exit += (_, _) => Updater.Instance.ApplyPendingOnExit();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task StartAsync(string[] args, MainWindow first)
    {
        // Looked up while the startup dialogs are open; offered after them.
        var update = Updater.Instance.CheckAsync();
        await AppServices.Instance.LocateToolsAsync();
        AppLog.Write(AppServices.Instance.Tools is { } tools ? $"ffmpeg: {tools.VersionText} ({tools.Ffmpeg})" : $"ffmpeg: {AppServices.Instance.ToolsError}");
        // Before opening files, so they can be read with what gets installed.
        if (SetupViewModel.ShouldAskAtStartup)
            await new SetupWindow().ShowDialog(first);
        await OfferProjectFileAssociationAsync(first);
        // macOS turns file arguments into "open document" events (OnActivated) as well; open them only once.
        var files = OperatingSystem.IsMacOS() ? [] : args.Where(File.Exists).ToList();
        if (files.Count > 0)
            await WindowManager.Instance.OpenFilesAsync(files, first);
        if (await update is { } offer)
            await Updater.Instance.OfferAsync(offer);
#if DEBUG
        DebugAutomation.StartIfRequested(first);
#endif
    }

    private static async Task OfferProjectFileAssociationAsync(Window owner)
    {
        var settings = AppServices.Instance.Settings;
        if (!settings.Current.AskToRegisterProjectFiles || !ProjectFileAssociation.IsSupported
            || await ProjectFileAssociation.IsRegisteredAsync())
            return;
        AppLog.Write($"Offering to open .{ProjectFile.Extension} files with this app");
        var doNotShow = new CheckBox { Content = "Do not show anymore" };
        var choice = await Dialogs.AskAsync(owner, $"Open .{ProjectFile.Extension} projects with Mane FFmpeg?",
            "Projects don't open in Mane FFmpeg on double-click yet. You can change this later in Settings.",
            doNotShow, "Register", "Not Now");
        if (doNotShow.IsChecked == true)
            settings.Update(s => s with { AskToRegisterProjectFiles = false });
        if (choice != 0)
            return;
        try
        {
            await ProjectFileAssociation.RegisterAsync();
        }
        catch (Exception ex)
        {
            AppLog.Write($"Registering .{ProjectFile.Extension} files failed: {ex.Message}");
            await Dialogs.InfoAsync(owner, "Couldn't register project files", ex.Message);
        }
    }

    /// <summary>macOS "open document" events (double-clicked .mfvp files, files dropped on the Dock icon).</summary>
    private void OnActivated(object? sender, ActivatedEventArgs e)
    {
        AppLog.Write($"Activated: {e.Kind} ({e.GetType().Name})");
        if (e is not FileActivatedEventArgs files)
            return;
        var paths = files.Files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0)
            _ = WindowManager.Instance.OpenFilesAsync(paths, WindowManager.Instance.Windows.LastOrDefault());
    }

    public static void ApplyTheme(ThemeChoice theme)
    {
        if (Current is { } app)
            app.RequestedThemeVariant = theme switch
            {
                ThemeChoice.Light => ThemeVariant.Light,
                ThemeChoice.System => ThemeVariant.Default,
                _ => ThemeVariant.Dark,
            };
    }

    private void OnAboutClicked(object? sender, EventArgs e)
    {
        if (WindowManager.Instance.Windows.LastOrDefault() is { } window)
            _ = Dialogs.InfoAsync(window, "Mane FFmpeg",
                $"A small video editor on top of your system ffmpeg.\n\nVersion {typeof(App).Assembly.GetName().Version?.ToString(3)}\n" +
                $"ffmpeg: {AppServices.Instance.Tools?.VersionText ?? "not found"}\n" +
                $"libmpv: {Core.Mpv.MpvLibrary.LoadedPath ?? "not found"}\n\n" +
                "Powered by Ilia Petrov-Komotskii (Mane Function)",
                "github.com/ManeFunction/mane-ffmpeg", "https://github.com/ManeFunction/mane-ffmpeg");
    }

    private void OnSettingsClicked(object? sender, EventArgs e) =>
        WindowManager.Instance.Windows.LastOrDefault()?.ShowSettings();
}
