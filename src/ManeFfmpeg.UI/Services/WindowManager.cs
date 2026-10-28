using Avalonia;
using Avalonia.Controls;
using ManeFfmpeg.UI.ViewModels;
using ManeFfmpeg.UI.Views;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.UI.Services;

/// <summary>Opens project windows; every window is an independent project with its own player and export.</summary>
public sealed class WindowManager
{
    private readonly List<MainWindow> _windows = [];

    public static WindowManager Instance { get; } = new();

    public IReadOnlyList<MainWindow> Windows => _windows;

    public MainWindow OpenNew(MainWindow? cascadeFrom = null)
    {
        var window = new MainWindow(new MainWindowViewModel());
        if (cascadeFrom is not null)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = cascadeFrom.Position + new PixelPoint(32, 32);
        }
        _windows.Add(window);
        window.Closed += (_, _) => _windows.Remove(window);
        window.Show();
        return window;
    }

    /// <summary>
    /// Opens a project: activates its window if it's already open, reuses <paramref name="requester"/>
    /// when that window is still empty, and otherwise opens a new window.
    /// </summary>
    public async Task OpenProjectAsync(string path, MainWindow? requester)
    {
        var full = Path.GetFullPath(path);
        if (_windows.FirstOrDefault(w => w.ViewModel.FilePath == full) is { } existing)
        {
            existing.Activate();
            return;
        }
        var target = requester is { ViewModel.IsEmpty: true } ? requester : OpenNew(requester ?? _windows.LastOrDefault());
        try
        {
            await target.ViewModel.LoadProjectAsync(full);
            target.ApplyLayout();
            AppServices.Instance.Settings.AddRecentProject(full);
        }
        catch (Exception ex) when (ex is ProjectFileException or IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Open project {full} failed: {ex.Message}");
            await Dialogs.InfoAsync(target, "Couldn't open the project", ex.Message);
            if (target != requester && target.ViewModel.IsEmpty)
                target.Close();
        }
    }

    /// <summary>Opens dropped or passed files: project files open as projects, videos are added to the requester.</summary>
    public async Task OpenFilesAsync(IReadOnlyList<string> paths, MainWindow? requester)
    {
        // At launch, macOS can hand over files before ffmpeg has been found; projects would show their videos
        // as unreadable until then.
        await AppServices.Instance.ToolsLocated;
        foreach (var project in paths.Where(IsProject))
            await OpenProjectAsync(project, requester);
        var media = paths.Where(p => !IsProject(p) && File.Exists(p)).ToList();
        if (media.Count > 0)
        {
            var target = requester ?? _windows.LastOrDefault() ?? OpenNew();
            await target.ViewModel.AddMediaAsync(media);
        }
    }

    public static bool IsProject(string path) =>
        string.Equals(Path.GetExtension(path), "." + ProjectFile.Extension, StringComparison.OrdinalIgnoreCase);
}
