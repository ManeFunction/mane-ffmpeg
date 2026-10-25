using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ManeFfmpeg.App.Services;
using ManeFfmpeg.App.ViewModels;
using ManeFfmpeg.Core.Model;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.App.Views;

public partial class MainWindow : Window, IMainWindowView
{
    private static readonly string[] VideoPatterns =
        ["*.mp4", "*.mov", "*.m4v", "*.mkv", "*.webm", "*.avi", "*.mts", "*.m2ts", "*.ts", "*.mpg", "*.mpeg", "*.wmv", "*.flv", "*.3gp", "*.mxf", "*.ogv"];

    private bool _closeConfirmed;

    // Keys consumed as shortcuts on key-down; their key-up is swallowed too, so a focused button or
    // checkbox doesn't also react to it (Space would otherwise start playback and click "pause" on release).
    private readonly HashSet<Key> _shortcutKeysDown = [];

    // Compact mode: height of the panel row without the preview, and the sizes to restore afterwards.
    private const double CompactTopHeight = 330;
    private const double NormalMinHeight = 640;
    private const double CompactMinHeight = 470;
    private double? _expandedHeight;
    private GridLength _expandedLeftWidth = new(300);
    private NativeMenuItem? _compactMenuItem;

    public MainWindow() : this(new MainWindowViewModel())
    {
    }

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        viewModel.View = this;
        BuildMenu();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnWindowKeyUp, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => _shortcutKeysDown.Clear();
        Closing += OnClosing;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.IsCompact))
                ApplyCompact(viewModel.IsCompact);
        };
        Closed += (_, _) => ViewModel.Dispose();
    }

    public MainWindowViewModel ViewModel { get; }

    private static KeyModifiers Cmd => EditMenu.CommandModifier;

    // ---- Menu -------------------------------------------------------------------------------------

    private void BuildMenu()
    {
        var vm = ViewModel;
        NativeMenuItem Item(string header, System.Windows.Input.ICommand command, Key? key = null, KeyModifiers modifiers = KeyModifiers.None,
            object? parameter = null)
        {
            var item = new NativeMenuItem(header) { Command = command, CommandParameter = parameter };
            if (key is { } k)
                item.Gesture = new KeyGesture(k, modifiers);
            return item;
        }

        var recent = new NativeMenu();
        var recentItem = new NativeMenuItem("Open Recent") { Menu = recent };
        void FillRecent()
        {
            recent.Items.Clear();
            foreach (var path in AppServices.Instance.Settings.Current.RecentProjects.Where(File.Exists))
            {
                var p = path;
                var item = new NativeMenuItem(Path.GetFileName(p)) { ToolTip = p };
                item.Click += async (_, _) => await WindowManager.Instance.OpenProjectAsync(p, this);
                recent.Items.Add(item);
            }
            if (recent.Items.Count == 0)
                recent.Items.Add(new NativeMenuItem("No recent projects") { IsEnabled = false });
        }
        FillRecent();
        AppServices.Instance.Settings.Changed += (_, _) => FillRecent();

        var file = new NativeMenu
        {
            Item("New Window", vm.NewWindowCommand, Key.N, Cmd),
            Item("Open Project…", vm.OpenCommand, Key.O, Cmd),
            recentItem,
            new NativeMenuItemSeparator(),
            Item("Add Videos…", vm.AddMediaCommand, Key.I, Cmd),
            new NativeMenuItemSeparator(),
            Item("Save Project", vm.SaveCommand, Key.S, Cmd),
            Item("Save Project As…", vm.SaveAsCommand, Key.S, Cmd | KeyModifiers.Shift),
            new NativeMenuItemSeparator(),
            Item("Export", vm.ExportCommand, Key.E, Cmd),
            Item("Cancel Export", vm.CancelExportCommand),
        };
        var closeItem = new NativeMenuItem("Close Window") { Gesture = new KeyGesture(Key.W, Cmd) };
        closeItem.Click += (_, _) => Close();
        file.Items.Add(new NativeMenuItemSeparator());
        file.Items.Add(closeItem);
        if (!OperatingSystem.IsMacOS())
        {
            var settings = new NativeMenuItem("Settings…");
            settings.Click += (_, _) => ShowSettings();
            file.Items.Add(settings);
        }

        // Undo/Redo/Select All act on the focused text field, otherwise on the timeline.
        var edit = EditMenu.Create(this,
            undo: () => vm.UndoCommand.Execute(null),
            redo: () => vm.RedoCommand.Execute(null),
            selectAll: () => vm.SelectAllCommand.Execute(null));
        foreach (var item in new NativeMenuItemBase[]
        {
            new NativeMenuItemSeparator(),
            Item("Split at Playhead", vm.SplitCommand),
            Item("Delete Selected Clips", vm.DeleteSelectionCommand),
            new NativeMenuItemSeparator(),
            Item("Audio 10 ms Earlier", vm.NudgeAudioCommand, parameter: "-10"),
            Item("Audio 10 ms Later", vm.NudgeAudioCommand, parameter: "10"),
            Item("Audio 1 Frame Earlier", vm.NudgeAudioFramesCommand, parameter: "-1"),
            Item("Audio 1 Frame Later", vm.NudgeAudioFramesCommand, parameter: "1"),
            Item("Reset Audio Offset", vm.ResetAudioOffsetCommand),
        })
            edit.Items.Add(item);

        _compactMenuItem = Item("Compact Mode (Hide Preview)", vm.ToggleCompactCommand, Key.C, Cmd | KeyModifiers.Shift);
        _compactMenuItem.ToggleType = MenuItemToggleType.CheckBox;
        var view = new NativeMenu
        {
            _compactMenuItem,
            new NativeMenuItemSeparator(),
            Item("Play / Pause", vm.PlayPauseCommand),
            Item("Zoom In", vm.ZoomInCommand, Key.OemPlus, Cmd),
            Item("Zoom Out", vm.ZoomOutCommand, Key.OemMinus, Cmd),
            Item("Fit Timeline", vm.ZoomFitCommand, Key.D0, Cmd),
        };

        var presets = new NativeMenu
        {
            Item("Edit Presets…", vm.ShowPresetsCommand),
        };

        NativeMenu.SetMenu(this, new NativeMenu
        {
            new NativeMenuItem("File") { Menu = file },
            new NativeMenuItem("Edit") { Menu = edit },
            new NativeMenuItem("View") { Menu = view },
            new NativeMenuItem("Presets") { Menu = presets },
        });
    }

    // ---- Keyboard ---------------------------------------------------------------------------------

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        // Single-key shortcuts must not fire while typing.
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox or NumericUpDown or ComboBox)
            return;
        if ((e.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control)) != 0)
            return;
        // Holding a toggle key auto-repeats key-down; only the first press counts.
        if (e.Key is Key.Space or Key.K && _shortcutKeysDown.Contains(e.Key))
        {
            e.Handled = true;
            return;
        }
        var vm = ViewModel;
        var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        var alt = (e.KeyModifiers & KeyModifiers.Alt) != 0;
        var handled = true;
        switch (e.Key)
        {
            case Key.Space:
            case Key.K:
                vm.PlayPauseCommand.Execute(null);
                break;
            case Key.Left when alt:
                if (shift)
                    vm.NudgeAudioFramesCommand.Execute("-1");
                else
                    vm.NudgeAudioCommand.Execute("-10");
                break;
            case Key.Right when alt:
                if (shift)
                    vm.NudgeAudioFramesCommand.Execute("1");
                else
                    vm.NudgeAudioCommand.Execute("10");
                break;
            case Key.Left:
                if (shift)
                    vm.JumpCommand.Execute("-1");
                else
                    vm.StepFrameCommand.Execute("-1");
                break;
            case Key.Right:
                if (shift)
                    vm.JumpCommand.Execute("1");
                else
                    vm.StepFrameCommand.Execute("1");
                break;
            case Key.J:
                vm.JumpCommand.Execute("-5");
                break;
            case Key.L:
                vm.JumpCommand.Execute("5");
                break;
            case Key.Up:
                vm.JumpToCutCommand.Execute("-1");
                break;
            case Key.Down:
                vm.JumpToCutCommand.Execute("1");
                break;
            case Key.Home:
                vm.GoToStartCommand.Execute(null);
                break;
            case Key.End:
                vm.GoToEndCommand.Execute(null);
                break;
            case Key.S:
            case Key.B:
                vm.SplitCommand.Execute(null);
                break;
            case Key.Delete:
            case Key.Back:
                vm.DeleteSelectionCommand.Execute(null);
                break;
            case Key.I when vm.IsSourceMode:
                vm.SetMarkInCommand.Execute(null);
                break;
            case Key.O when vm.IsSourceMode:
                vm.SetMarkOutCommand.Execute(null);
                break;
            case Key.Enter when vm.IsSourceMode:
                vm.AddMarkedRangeCommand.Execute(null);
                break;
            case Key.Escape:
                if (vm.IsSourceMode)
                    vm.BackToTimelineCommand.Execute(null);
                else
                    vm.Timeline.ClearSelection();
                break;
            case Key.OemPlus:
            case Key.Add:
                vm.ZoomInCommand.Execute(null);
                break;
            case Key.OemMinus:
            case Key.Subtract:
                vm.ZoomOutCommand.Execute(null);
                break;
            default:
                handled = false;
                break;
        }
        e.Handled = handled;
        if (handled)
            _shortcutKeysDown.Add(e.Key);
    }

    private void OnWindowKeyUp(object? sender, KeyEventArgs e)
    {
        if (_shortcutKeysDown.Remove(e.Key))
            e.Handled = true;
    }

    // ---- Drag and drop ----------------------------------------------------------------------------

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.TryGetFiles() is { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        e.Handled = true;
        if (paths is { Count: > 0 })
            await WindowManager.Instance.OpenFilesAsync(paths, this);
    }

    // ---- Media bin context menu -------------------------------------------------------------------

    private static SourceItemViewModel? ItemOf(object? sender) => (sender as Control)?.DataContext as SourceItemViewModel;

    private void OnPreviewSourceClicked(object? sender, RoutedEventArgs e) => ViewModel.PreviewSourceCommand.Execute(ItemOf(sender));

    private void OnAppendSourceClicked(object? sender, RoutedEventArgs e) => ViewModel.AppendSourceCommand.Execute(ItemOf(sender));

    private void OnRelinkSourceClicked(object? sender, RoutedEventArgs e) => ViewModel.RelinkCommand.Execute(ItemOf(sender));

    private void OnRemoveSourceClicked(object? sender, RoutedEventArgs e) => ViewModel.RemoveSourceCommand.Execute(ItemOf(sender));

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        SourcesList.DoubleTapped += (_, _) => ViewModel.PreviewSourceCommand.Execute(SourcesList.SelectedItem as SourceItemViewModel);
    }

    // ---- Closing ----------------------------------------------------------------------------------

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed)
            return;
        e.Cancel = true;
        if (ViewModel.IsExporting)
        {
            var stop = await AskAsync("An export is still running", "Stop the export and close this window?", "Stop and close", "Keep working");
            if (stop != 0)
                return;
            ViewModel.CancelExportCommand.Execute(null);
        }
        if (ViewModel.IsDirty && ViewModel.HasClips)
        {
            var answer = await AskAsync($"Save changes to “{ViewModel.ProjectName}”?", "Your edits will be lost if you don't save them.",
                "Save", "Don't save", "Cancel");
            if (answer == 2 || answer < 0)
                return;
            if (answer == 0 && !await ViewModel.SaveAsync(saveAs: false))
                return;
        }
        _closeConfirmed = true;
        Close();
    }

    /// <summary>Closes without asking (used when the app quits from automation).</summary>
    public void ForceClose()
    {
        _closeConfirmed = true;
        Close();
    }

    // ---- Compact mode -----------------------------------------------------------------------------

    /// <summary>
    /// Hides the preview, puts the video list and clip inspector side by side and shrinks the window to fit;
    /// turning it off restores the previous layout and height.
    /// </summary>
    private void ApplyCompact(bool compact)
    {
        if (_compactMenuItem is not null)
            _compactMenuItem.IsChecked = compact;
        if (compact == !PreviewPanel.IsVisible)
            return;

        var resize = WindowState == WindowState.Normal;
        var topHeight = TopGrid.Bounds.Height;
        if (compact)
        {
            _expandedLeftWidth = TopGrid.ColumnDefinitions[0].Width;
            if (resize)
                _expandedHeight = Height;
            TopGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            TopGrid.ColumnDefinitions[2].Width = new GridLength(0);
            PreviewSplitter.IsVisible = false;
            PreviewPanel.IsVisible = false;
            Grid.SetRowSpan(BinPanel, 2);
            Grid.SetRow(InspectorPanel, 0);
            Grid.SetColumn(InspectorPanel, 1);
            Grid.SetRowSpan(InspectorPanel, 2);
            InspectorPanel.Width = 330;
            InspectorPanel.Margin = new Thickness(8, 0, 0, 0);
            MinHeight = CompactMinHeight;
            if (resize && topHeight > CompactTopHeight)
                Height = Math.Max(CompactMinHeight, Height - topHeight + CompactTopHeight);
        }
        else
        {
            TopGrid.ColumnDefinitions[0].Width = _expandedLeftWidth;
            TopGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
            PreviewSplitter.IsVisible = true;
            PreviewPanel.IsVisible = true;
            Grid.SetRowSpan(BinPanel, 1);
            Grid.SetRow(InspectorPanel, 1);
            Grid.SetColumn(InspectorPanel, 0);
            Grid.SetRowSpan(InspectorPanel, 1);
            InspectorPanel.Width = double.NaN;
            InspectorPanel.Margin = new Thickness(0, 8, 0, 0);
            MinHeight = NormalMinHeight;
            if (resize)
                Height = Math.Max(NormalMinHeight, _expandedHeight ?? Height + 420);
        }
    }

    // ---- Layout -----------------------------------------------------------------------------------

    public void ApplyLayout()
    {
        var layout = ViewModel.Layout;
        if (layout.Window is { } w)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(w.X, w.Y);
            Width = Math.Max(MinWidth, w.Width);
            Height = Math.Max(MinHeight, w.Height);
            if (w.Maximized)
                WindowState = WindowState.Maximized;
        }
        TopGrid.ColumnDefinitions[0].Width = new GridLength(Math.Clamp(layout.LeftPaneWidth, 220, 600));
        if (layout.Compact)
        {
            // Bounds above are the full-size ones; shrink once the full layout has been measured.
            _expandedHeight = Height;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ViewModel.IsCompact = true, Avalonia.Threading.DispatcherPriority.Loaded);
        }
        else
        {
            ViewModel.IsCompact = false;
        }
    }

    public ProjectLayout CaptureLayout() => new()
    {
        Window = new WindowBounds(Position.X, Position.Y, Width,
            ViewModel.IsCompact ? _expandedHeight ?? Height : Height, WindowState == WindowState.Maximized),
        LeftPaneWidth = ViewModel.IsCompact ? _expandedLeftWidth.Value : TopGrid.ColumnDefinitions[0].ActualWidth,
        Compact = ViewModel.IsCompact,
        TimelineZoom = ViewModel.Timeline.Zoom,
        TimelineScroll = ViewModel.Timeline.Scroll,
        Playhead = ViewModel.Timeline.Playhead,
        Volume = ViewModel.Volume,
    };

    // ---- IMainWindowView --------------------------------------------------------------------------

    private async Task<IStorageFolder?> FolderAsync(string? path) =>
        path is not null && Directory.Exists(path) ? await StorageProvider.TryGetFolderFromPathAsync(path) : null;

    public async Task<IReadOnlyList<string>> PickMediaAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add videos",
            AllowMultiple = true,
            SuggestedStartLocation = await FolderAsync(AppServices.Instance.Settings.Current.LastMediaFolder),
            FileTypeFilter = [new FilePickerFileType("Videos") { Patterns = VideoPatterns }, FilePickerFileTypes.All],
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0)
            AppServices.Instance.Settings.Update(s => s with { LastMediaFolder = Path.GetDirectoryName(paths[0]) });
        return paths;
    }

    public async Task<string?> PickProjectToOpenAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open project",
            SuggestedStartLocation = await FolderAsync(AppServices.Instance.Settings.Current.LastProjectFolder),
            FileTypeFilter = [new FilePickerFileType("Mane FFmpeg project") { Patterns = ["*." + ProjectFile.Extension] }],
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> PickProjectSavePathAsync(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save project",
            SuggestedFileName = suggestedName + "." + ProjectFile.Extension,
            DefaultExtension = ProjectFile.Extension,
            SuggestedStartLocation = await FolderAsync(ProjectFolderHint()),
            FileTypeChoices = [new FilePickerFileType("Mane FFmpeg project") { Patterns = ["*." + ProjectFile.Extension] }],
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    private string? ProjectFolderHint()
    {
        if (ViewModel.FilePath is { } project)
            return Path.GetDirectoryName(project);
        if (AppServices.Instance.Settings.Current.LastProjectFolder is { } last)
            return last;
        return ViewModel.Sources.FirstOrDefault()?.Path is { } source ? Path.GetDirectoryName(source) : null;
    }

    public async Task<string?> PickOutputPathAsync(string suggestedPath)
    {
        var ext = Path.GetExtension(suggestedPath).TrimStart('.');
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export to",
            SuggestedFileName = Path.GetFileName(suggestedPath),
            DefaultExtension = ext,
            SuggestedStartLocation = await FolderAsync(Path.GetDirectoryName(suggestedPath)),
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickFileAsync(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public Task<int> AskAsync(string title, string message, params string[] buttons) => Dialogs.AskAsync(this, title, message, buttons);

    public void ShowPresets() => new PresetsWindow().ShowDialog(this);

    public void ShowSettings() => new SettingsWindow().ShowDialog(this);

    public void RevealInFileManager(string path)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("/usr/bin/open") { ArgumentList = { "-R", path }, UseShellExecute = false });
            else
                Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { Path.GetDirectoryName(path)! }, UseShellExecute = false });
        }
        catch (Exception)
        {
            // Nothing to do if no file manager is available.
        }
    }
}
