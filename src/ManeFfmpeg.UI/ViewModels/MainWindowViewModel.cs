using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManeFfmpeg.UI.Services;
using ManeFfmpeg.Core.Editing;
using ManeFfmpeg.Core.Media;
using ManeFfmpeg.Core.Model;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.UI.ViewModels;

/// <summary>What the view model needs from its window: pickers, prompts and layout.</summary>
public interface IMainWindowView
{
    Task<IReadOnlyList<string>> PickMediaAsync();
    Task<IReadOnlyList<string>> PickAudioAsync();
    Task<string?> PickProjectToOpenAsync();
    Task<string?> PickProjectSavePathAsync(string suggestedName);
    Task<string?> PickOutputPathAsync(string suggestedPath);
    Task<string?> PickFileAsync(string title);
    Task<int> AskAsync(string title, string message, params string[] buttons);
    ProjectLayout CaptureLayout();
    void ShowPresets();
    void ShowSettings();
    void RevealInFileManager(string path);
}

/// <summary>One open project: its edit history, sources and clip inspector, with the preview and export as children.</summary>
public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly AppServices _app = AppServices.Instance;
    private readonly SourceCatalog _catalog = new(AppServices.Instance);
    private ProjectState _savedState = ProjectState.Empty;
    private ExportSettings _savedExport = new();

    public MainWindowViewModel()
    {
        History = new UndoStack<ProjectState>(ProjectState.Empty);
        History.Changed += OnHistoryChanged;
        _catalog.Changed += OnSourceAnalyzed;
        Timeline = new TimelineViewModel(this);
        _app.ToolsChanged += OnToolsChanged;
        Sources.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoSources));
        AudioFiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoAudioFiles));
        Export = new ExportViewModel(this);
        Preview = new PreviewViewModel(this);
    }

    public IMainWindowView? View { get; set; }

    public UndoStack<ProjectState> History { get; }

    public ProjectState State => History.Current;

    public TimelineViewModel Timeline { get; }

    public PreviewViewModel Preview { get; }

    public ExportViewModel Export { get; }

    /// <summary>The project state changed (an edit, undo or redo, or loading); raised after the bin is in step.</summary>
    public event EventHandler? StateChanged;

    public ObservableCollection<SourceItemViewModel> Sources { get; } = [];

    /// <summary>The Audio tab: imported audio files that can replace clips' sound.</summary>
    public ObservableCollection<SourceItemViewModel> AudioFiles { get; } = [];

    /// <summary>The Files panel's tab: 0 is Video, 1 is Audio.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVideoTab), nameof(IsAudioTab))]
    public partial int FilesTab { get; set; }

    public bool IsVideoTab => FilesTab == 0;

    public bool IsAudioTab => FilesTab == 1;

    /// <summary>What's known about each source: status, probe result, waveform, keyframes.</summary>
    public SourceCatalog Catalog => _catalog;

    /// <summary>Probe results of the ready sources, by source id.</summary>
    public IReadOnlyDictionary<string, MediaInfo> Media => _catalog.Media;

    /// <summary>Layout read from the project file, applied by the window after loading.</summary>
    public ProjectLayout Layout { get; private set; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProjectName), nameof(Title))]
    public partial string? FilePath { get; set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    public string ProjectName => FilePath is null ? "Untitled" : Path.GetFileNameWithoutExtension(FilePath);

    public bool IsDirty => !State.Equals(_savedState) || Export.CurrentExportSettings() != _savedExport;

    public string Title => $"{(IsDirty ? "• " : "")}{ProjectName} — Mane FFmpeg";

    public bool IsEmpty => FilePath is null && State.Sources.Count == 0 && State.AudioFiles.Count == 0;

    public bool HasClips => State.Timeline.Clips.Count > 0;

    public bool HasNoSources => Sources.Count == 0;

    public bool HasNoAudioFiles => AudioFiles.Count == 0;

    public string? ToolsError => _app.ToolsError;

    public void Commit(ProjectState next) => History.Commit(next);

    /// <summary>Commits an edit of the timeline alone.</summary>
    public void CommitTimeline(Func<Timeline, Timeline> edit) => Commit(State with { Timeline = edit(State.Timeline) });

    /// <summary>Commits, re-fitting the zoom if the whole timeline was visible before (so appended clips show up).</summary>
    internal void CommitKeepingFit(ProjectState next)
    {
        var wasFit = Timeline.Zoom <= 0 || State.Timeline.Duration.TotalSeconds * Timeline.Zoom <= Timeline.ViewportWidth + 1;
        Commit(next);
        if (wasFit && HasClips)
            Timeline.Fit();
    }

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        SyncSources();
        Timeline.PruneSelection();
        Timeline.Invalidate();
        if (Timeline.Playhead > State.Timeline.Duration)
            Timeline.Playhead = State.Timeline.Duration;
        RefreshDirty();
        OnPropertyChanged(nameof(HasClips));
        OnPropertyChanged(nameof(TimelineDurationText));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        OnSelectionChanged();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void RefreshDirty()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(Title));
    }

    private void OnToolsChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        OnPropertyChanged(nameof(ToolsError));
        _catalog.Reanalyze();
        // libmpv may have just been installed.
        Preview.StartPlayer();
    });

    // ---- Sources ----------------------------------------------------------------------------------

    /// <summary>Keeps the Files panel and the catalog in step with the project state (after edits, undo, load).</summary>
    private void SyncSources()
    {
        SyncList(Sources, FileKind.Video);
        SyncList(AudioFiles, FileKind.Audio);
        _catalog.Sync(State.Sources, State.AudioFiles);
    }

    private void SyncList(ObservableCollection<SourceItemViewModel> items, FileKind kind)
    {
        var sources = State.Files(kind);
        var ids = sources.Select(s => s.Id).ToHashSet();
        for (var i = items.Count - 1; i >= 0; i--)
            if (!ids.Contains(items[i].Id))
                items.RemoveAt(i);
        foreach (var source in sources)
        {
            var item = items.FirstOrDefault(s => s.Id == source.Id);
            if (item is null)
            {
                var color = Enumerable.Range(0, int.MaxValue).First(c => items.All(s => s.ColorIndex != c));
                item = new SourceItemViewModel(source, color, kind);
                item.AudioStreamChosen += OnAudioStreamChosen;
                items.Add(item);
            }
            else if (item.Source != source)
            {
                item.Update(source);
            }
        }
    }

    /// <summary>A video or audio file in the Files panel, by id.</summary>
    public SourceItemViewModel? FileItem(string id) =>
        Sources.FirstOrDefault(s => s.Id == id) ?? AudioFiles.FirstOrDefault(s => s.Id == id);

    private void OnSourceAnalyzed(string sourceId)
    {
        if (FileItem(sourceId) is { } item)
        {
            item.Info = _catalog.Info(sourceId);
            item.Status = _catalog.Status(sourceId);
            item.Error = _catalog.Error(sourceId);
        }
        Timeline.Invalidate();
    }

    private void OnAudioStreamChosen(SourceItemViewModel item, int index) =>
        Commit(State.ReplaceFile(item.Kind, item.Source, item.Source with { AudioStreamIndex = index }));

    /// <summary>
    /// Adds files by what they hold: videos as sources, each appended as a whole clip; files with only sound to the
    /// Audio tab. With <paramref name="replaceAudioOf"/> (audio dropped on a clip), the first audio file replaces that
    /// clip's sound in the same undo step.
    /// </summary>
    public async Task AddMediaAsync(IEnumerable<string> paths, string? replaceAudioOf = null)
    {
        if (await ProbeFilesAsync(paths) is not { } results)
            return;
        var state = State;
        var failed = new List<string>();
        var audio = new List<string>();
        foreach (var (path, info, error) in results)
        {
            if (info is { Video: null, AudioStreams.Length: > 0 })
            {
                (state, var id) = AddAudioFile(state, path);
                audio.Add(id);
                continue;
            }
            if (info?.Video is null)
            {
                failed.Add($"{Path.GetFileName(path)}: {error ?? "no video or audio stream"}");
                continue;
            }
            var existing = state.Sources.Find(s => s.Path == path);
            var source = existing ?? NewSource(path);
            if (existing is null)
                state = state with { Sources = state.Sources.Add(source) };
            state = state with { Timeline = TimelineEditor.Append(state.Timeline, Clip.Create(source.Id, TimeSpan.Zero, info.Duration)) };
        }
        if (replaceAudioOf is not null && audio.Count > 0)
            state = state with { Timeline = TimelineEditor.ReplaceAudio(state.Timeline, replaceAudioOf, audio[0]) };
        CommitKeepingFit(state);
        if (audio.Count > 0)
            FilesTab = 1;
        StatusMessage = failed.Count > 0 ? "Skipped: " + string.Join("; ", failed)
            : replaceAudioOf is not null && audio.Count > 0 ? ReplacedMessage(audio[0])
            : audio.Count > 0 ? AudioAddedMessage(audio.Count)
            : null;
        if (replaceAudioOf is not null && audio.Count > 0)
            Timeline.SelectOnly(replaceAudioOf);
    }

    /// <summary>Adds every file that has sound to the Audio tab, videos too (their sound can replace a clip's).</summary>
    public async Task AddAudioAsync(IEnumerable<string> paths)
    {
        if (await ProbeFilesAsync(paths) is not { } results)
            return;
        var state = State;
        var failed = new List<string>();
        var added = 0;
        foreach (var (path, info, error) in results)
        {
            if (info is not { AudioStreams.Length: > 0 })
            {
                failed.Add($"{Path.GetFileName(path)}: {error ?? "no audio stream"}");
                continue;
            }
            (state, _) = AddAudioFile(state, path);
            added++;
        }
        Commit(state);
        if (added > 0)
            FilesTab = 1;
        StatusMessage = failed.Count > 0 ? "Skipped: " + string.Join("; ", failed) : added > 0 ? AudioAddedMessage(added) : null;
    }

    private static string AudioAddedMessage(int count) =>
        $"{(count == 1 ? "Audio file added" : $"{count} audio files added")}: drag one onto a clip to replace its sound.";

    /// <summary>The state with <paramref name="path"/> as an audio file (once), and its id.</summary>
    private static (ProjectState State, string Id) AddAudioFile(ProjectState state, string path)
    {
        if (state.AudioFiles.Find(s => s.Path == path) is { } existing)
            return (state, existing.Id);
        var file = NewSource(path);
        return (state with { AudioFiles = state.AudioFiles.Add(file) }, file.Id);
    }

    /// <summary>Probes the files that exist, once ffmpeg is known; null when there's nothing to add.</summary>
    private async Task<(string Path, MediaInfo? Info, string? Error)[]?> ProbeFilesAsync(IEnumerable<string> paths)
    {
        var files = paths.Select(Path.GetFullPath).Where(File.Exists).Distinct().ToList();
        if (files.Count == 0)
            return null;
        await _app.ToolsLocated;
        if (_app.Tools is null)
        {
            StatusMessage = _app.ToolsError;
            return null;
        }
        StatusMessage = files.Count == 1 ? $"Adding {Path.GetFileName(files[0])}…" : $"Adding {files.Count} files…";
        var probes = files.Select(async f =>
        {
            try
            {
                return (Path: f, Info: (MediaInfo?)await _app.ProbeAsync(f), Error: (string?)null);
            }
            catch (Exception ex)
            {
                return (Path: f, Info: (MediaInfo?)null, Error: ex.Message);
            }
        }).ToList();
        return await Task.WhenAll(probes);
    }

    private static SourceMedia NewSource(string path)
    {
        var info = new FileInfo(path);
        return new SourceMedia { Id = Ids.New(), Path = path, FileSize = info.Length, LastWriteUtc = info.LastWriteTimeUtc };
    }

    [RelayCommand]
    private async Task AddMedia()
    {
        if (View is null)
            return;
        var files = await View.PickMediaAsync();
        await AddMediaAsync(files);
    }

    [RelayCommand]
    private async Task AddAudio()
    {
        if (View is null)
            return;
        var files = await View.PickAudioAsync();
        await AddAudioAsync(files);
    }

    [RelayCommand]
    private void AppendSource(SourceItemViewModel? item)
    {
        if (item?.Info is not { Video: not null } info)
            return;
        CommitKeepingFit(State with { Timeline = TimelineEditor.Append(State.Timeline, Clip.Create(item.Id, TimeSpan.Zero, info.Duration)) });
    }

    [RelayCommand]
    private async Task RemoveSource(SourceItemViewModel? item)
    {
        if (item is null || View is null)
            return;
        if (item.Kind == FileKind.Audio)
        {
            var playing = State.Timeline.Clips.Count(c => c.ReplacedAudio?.FileId == item.Id);
            if (playing > 0 && await View.AskAsync("Remove audio file?",
                    $"\"{item.Name}\" plays on {playing} clip(s). They will go back to their own audio (you can undo).",
                    "Remove", "Cancel") != 0)
                return;
            Commit(State with { AudioFiles = State.AudioFiles.Remove(item.Source), Timeline = TimelineEditor.RemoveAudioFile(State.Timeline, item.Id) });
            return;
        }
        var used = State.Timeline.Clips.Count(c => c.SourceId == item.Id);
        if (used > 0)
        {
            var answer = await View.AskAsync("Remove video?",
                $"\"{item.Name}\" is used by {used} clip(s) on the timeline. They will be removed too (you can undo).",
                "Remove", "Cancel");
            if (answer != 0)
                return;
        }
        Commit(State with { Sources = State.Sources.Remove(item.Source), Timeline = TimelineEditor.RemoveSource(State.Timeline, item.Id) });
    }

    [RelayCommand]
    private async Task Relink(SourceItemViewModel? item)
    {
        if (item is null || View is null)
            return;
        var path = await View.PickFileAsync($"Locate \"{item.Name}\"");
        if (path is null)
            return;
        var file = new FileInfo(path);
        var updated = item.Source with { Path = file.FullName, FileSize = file.Length, LastWriteUtc = file.LastWriteTimeUtc };
        Commit(State.ReplaceFile(item.Kind, item.Source, updated));
    }

    // ---- Files ------------------------------------------------------------------------------------

    public async Task LoadProjectAsync(string path)
    {
        var project = await ProjectFile.LoadAsync(path);
        var preset = await Export.ResolvePresetAsync(project.Export);
        Sources.Clear();
        AudioFiles.Clear();
        FilesTab = 0;
        FilePath = path;
        Layout = project.Layout;
        Export.ApplyExportSettings(project.Export, preset);
        _savedExport = Export.CurrentExportSettings();
        _savedState = project.State;
        History.Reset(project.State);
        Timeline.ClearSelection();
        Timeline.Zoom = project.Layout.TimelineZoom;
        Timeline.Scroll = project.Layout.TimelineScroll;
        Timeline.Playhead = project.Layout.Playhead;
        Preview.Volume = project.Layout.Volume;
        RefreshDirty();
    }

    [RelayCommand]
    private void NewWindow() => WindowManager.Instance.OpenNew(WindowManager.Instance.Windows.FirstOrDefault(w => w.ViewModel == this));

    [RelayCommand]
    private async Task Open()
    {
        if (View?.PickProjectToOpenAsync() is not { } pick || await pick is not { } path)
            return;
        await WindowManager.Instance.OpenProjectAsync(path, WindowManager.Instance.Windows.FirstOrDefault(w => w.ViewModel == this));
    }

    [RelayCommand]
    private Task<bool> Save() => SaveAsync(saveAs: false);

    [RelayCommand]
    private Task<bool> SaveAs() => SaveAsync(saveAs: true);

    public async Task<bool> SaveAsync(bool saveAs)
    {
        if (View is null)
            return false;
        var path = FilePath;
        if (path is null || saveAs)
        {
            path = await View.PickProjectSavePathAsync(ProjectName == "Untitled" && Sources.Count > 0
                ? Path.GetFileNameWithoutExtension(Sources[0].Name)
                : ProjectName);
            if (path is null)
                return false;
            if (!string.Equals(Path.GetExtension(path), "." + ProjectFile.Extension, StringComparison.OrdinalIgnoreCase))
                path += "." + ProjectFile.Extension;
        }
        try
        {
            var export = Export.CurrentExportSettings();
            var project = new Project { State = State, Export = export, Layout = View.CaptureLayout() };
            await ProjectFile.SaveAsync(project, path);
            FilePath = path;
            _savedState = State;
            _savedExport = export;
            RefreshDirty();
            _app.Settings.AddRecentProject(path);
            StatusMessage = $"Saved {Path.GetFileName(path)}";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await View.AskAsync("Couldn't save the project", ex.Message, "OK");
            return false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => History.Undo();

    private bool CanUndo() => History.CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => History.Redo();

    private bool CanRedo() => History.CanRedo;

    [RelayCommand]
    private void ShowPresets() => View?.ShowPresets();

    [RelayCommand]
    private void ShowSettings() => View?.ShowSettings();

    public void Dispose()
    {
        _app.ToolsChanged -= OnToolsChanged;
        Export.Dispose();
        Preview.Dispose();
    }
}
