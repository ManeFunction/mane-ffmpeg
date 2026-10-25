using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManeFfmpeg.App.Services;
using ManeFfmpeg.Core.Editing;
using ManeFfmpeg.Core.Model;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.App.ViewModels;

/// <summary>What the view model needs from its window: pickers, prompts and layout.</summary>
public interface IMainWindowView
{
    Task<IReadOnlyList<string>> PickMediaAsync();
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

/// <summary>One open project: its edit history, sources, preview player and export.</summary>
public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly AppServices _app = AppServices.Instance;
    private readonly Dictionary<string, MediaInfo> _media = [];
    private ProjectState _savedState = ProjectState.Empty;
    private ExportSettings _savedExport = new();

    public MainWindowViewModel()
    {
        History = new UndoStack<ProjectState>(ProjectState.Empty);
        History.Changed += OnHistoryChanged;
        Timeline = new TimelineViewModel(this);
        _app.ToolsChanged += OnToolsChanged;
        Sources.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoSources));
        InitExport();
        InitPreview();
    }

    public IMainWindowView? View { get; set; }

    public UndoStack<ProjectState> History { get; }

    public ProjectState State => History.Current;

    public TimelineViewModel Timeline { get; }

    public ObservableCollection<SourceItemViewModel> Sources { get; } = [];

    public IReadOnlyDictionary<string, MediaInfo> Media => _media;

    /// <summary>Layout read from the project file, applied by the window after loading.</summary>
    public ProjectLayout Layout { get; private set; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProjectName), nameof(Title))]
    public partial string? FilePath { get; set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    public string ProjectName => FilePath is null ? "Untitled" : Path.GetFileNameWithoutExtension(FilePath);

    public bool IsDirty => !State.Equals(_savedState) || CurrentExportSettings() != _savedExport;

    public string Title => $"{(IsDirty ? "• " : "")}{ProjectName} — Mane FFmpeg";

    public bool IsEmpty => FilePath is null && State.Sources.Count == 0;

    public bool HasClips => State.Timeline.Clips.Count > 0;

    public bool HasNoSources => Sources.Count == 0;

    public string? ToolsError => _app.ToolsError;

    public void Commit(ProjectState next) => History.Commit(next);

    /// <summary>Commits an edit of the timeline alone.</summary>
    public void CommitTimeline(Func<Timeline, Timeline> edit) => Commit(State with { Timeline = edit(State.Timeline) });

    /// <summary>Commits, re-fitting the zoom if the whole timeline was visible before (so appended clips show up).</summary>
    private void CommitKeepingFit(ProjectState next)
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
        ScheduleTimelineReload();
        InvalidatePlanPreview();
    }

    internal void RefreshDirty()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(Title));
    }

    private void OnToolsChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        OnPropertyChanged(nameof(ToolsError));
        // Sources that failed get another try; ready ones reload their analyses, which new tools don't have yet.
        foreach (var item in Sources)
        {
            if (item.Status != SourceStatus.Ready)
                _ = ProbeAsync(item);
            else if (_media.TryGetValue(item.Id, out var info))
                _ = LoadAnalysisAsync(item.Source, info);
        }
    });

    // ---- Sources ----------------------------------------------------------------------------------

    /// <summary>Keeps the media bin in step with the project state (after edits, undo, load).</summary>
    private void SyncSources()
    {
        var ids = State.Sources.Select(s => s.Id).ToHashSet();
        for (var i = Sources.Count - 1; i >= 0; i--)
            if (!ids.Contains(Sources[i].Id))
                Sources.RemoveAt(i);
        foreach (var source in State.Sources)
        {
            var item = Sources.FirstOrDefault(s => s.Id == source.Id);
            if (item is null)
            {
                var color = Enumerable.Range(0, int.MaxValue).First(c => Sources.All(s => s.ColorIndex != c));
                item = new SourceItemViewModel(source, color);
                item.AudioStreamChosen += OnAudioStreamChosen;
                Sources.Add(item);
                if (_media.TryGetValue(source.Id, out var known))
                {
                    item.Info = known;
                    item.Status = SourceStatus.Ready;
                    _ = LoadAnalysisAsync(source, known);
                }
                else
                {
                    _ = ProbeAsync(item);
                }
            }
            else if (item.Source != source)
            {
                var pathChanged = item.Path != source.Path;
                item.Update(source);
                if (pathChanged)
                    _ = ProbeAsync(item);
            }
        }
    }

    private async Task ProbeAsync(SourceItemViewModel item)
    {
        if (!File.Exists(item.Path))
        {
            item.Status = SourceStatus.Missing;
            _media.Remove(item.Id);
            Timeline.Invalidate();
            return;
        }
        item.Status = SourceStatus.Analyzing;
        try
        {
            var info = await _app.ProbeAsync(item.Path);
            _media[item.Id] = info;
            item.Info = info;
            item.Status = info.Video is null ? SourceStatus.Failed : SourceStatus.Ready;
            if (info.Video is null)
                item.Error = "No video stream";
            Timeline.Invalidate();
            ScheduleTimelineReload();
            InvalidatePlanPreview();
            _ = LoadAnalysisAsync(item.Source, info);
        }
        catch (Exception ex)
        {
            item.Status = SourceStatus.Failed;
            item.Error = ex.Message;
            Timeline.Invalidate();
        }
    }

    /// <summary>Waveform (always) and keyframes (when stream copy needs them) in the background.</summary>
    private async Task LoadAnalysisAsync(SourceMedia source, MediaInfo info)
    {
        try
        {
            var tasks = new List<Task>();
            if (info.Audio(source.AudioStreamIndex) is { } audio && _app.Waveforms is { } waveforms)
                tasks.Add(waveforms.GetAsync(source.Path, info, audio.Index));
            if (SnapToKeyframes && _app.Keyframes is { } keyframes)
                tasks.Add(keyframes.GetAsync(source.Path, info));
            foreach (var task in tasks)
            {
                await task;
                Timeline.Invalidate();
            }
        }
        catch (Exception)
        {
            // Waveforms and keyframes are optional helpers.
        }
    }

    private void OnAudioStreamChosen(SourceItemViewModel item, int index)
    {
        var sources = State.Sources.Replace(item.Source, item.Source with { AudioStreamIndex = index });
        Commit(State with { Sources = sources });
        if (_media.TryGetValue(item.Id, out var info))
            _ = LoadAnalysisAsync(State.Source(item.Id)!, info);
    }

    /// <summary>Adds files as sources and appends each as a whole clip.</summary>
    public async Task AddMediaAsync(IEnumerable<string> paths)
    {
        var files = paths.Select(Path.GetFullPath).Where(File.Exists).Distinct().ToList();
        if (files.Count == 0)
            return;
        if (_app.Tools is null)
        {
            StatusMessage = _app.ToolsError;
            return;
        }
        StatusMessage = files.Count == 1 ? $"Adding {Path.GetFileName(files[0])}…" : $"Adding {files.Count} files…";
        var probes = files.Select(async f =>
        {
            try
            {
                return (Path: f, Info: await _app.ProbeAsync(f), Error: (string?)null);
            }
            catch (Exception ex)
            {
                return (Path: f, Info: (MediaInfo?)null, Error: ex.Message);
            }
        }).ToList();
        var results = await Task.WhenAll(probes);

        var state = State;
        var failed = new List<string>();
        foreach (var (path, info, error) in results)
        {
            if (info?.Video is null)
            {
                failed.Add($"{Path.GetFileName(path)}: {error ?? "no video stream"}");
                continue;
            }
            var existing = state.Sources.Find(s => s.Path == path);
            var source = existing ?? NewSource(path);
            if (existing is null)
            {
                _media[source.Id] = info;
                state = state with { Sources = state.Sources.Add(source) };
            }
            state = state with { Timeline = TimelineEditor.Append(state.Timeline, Clip.Create(source.Id, TimeSpan.Zero, info.Duration)) };
        }
        CommitKeepingFit(state);
        StatusMessage = failed.Count == 0 ? null : "Skipped: " + string.Join("; ", failed);
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
        var used = State.Timeline.Clips.Count(c => c.SourceId == item.Id);
        if (used > 0)
        {
            var answer = await View.AskAsync("Remove video?",
                $"\"{item.Name}\" is used by {used} clip(s) on the timeline. They will be removed too (you can undo).",
                "Remove", "Cancel");
            if (answer != 0)
                return;
        }
        Commit(new ProjectState(State.Sources.Remove(item.Source), TimelineEditor.RemoveSource(State.Timeline, item.Id)));
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
        _media.Remove(item.Id);
        var updated = item.Source with { Path = file.FullName, FileSize = file.Length, LastWriteUtc = file.LastWriteTimeUtc };
        Commit(State with { Sources = State.Sources.Replace(item.Source, updated) });
    }

    // ---- Files ------------------------------------------------------------------------------------

    public async Task LoadProjectAsync(string path)
    {
        var project = await ProjectFile.LoadAsync(path);
        var preset = await ResolvePresetAsync(project.Export);
        _media.Clear();
        Sources.Clear();
        FilePath = path;
        Layout = project.Layout;
        ApplyExportSettings(project.Export, preset);
        _savedExport = CurrentExportSettings();
        _savedState = project.State;
        History.Reset(project.State);
        Timeline.Selection.Clear();
        Timeline.Zoom = project.Layout.TimelineZoom;
        Timeline.Scroll = project.Layout.TimelineScroll;
        Timeline.Playhead = project.Layout.Playhead;
        Volume = project.Layout.Volume;
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
            var export = CurrentExportSettings();
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
        DisposeExport();
        DisposePreview();
    }
}
