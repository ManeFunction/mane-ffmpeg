#if DEBUG
#pragma warning disable CS0618 // Bitmap.Save(string) is fine for debug snapshots
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ManeFfmpeg.UI.Controls;
using ManeFfmpeg.UI.ViewModels;
using ManeFfmpeg.UI.Views;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.UI.Services;

/// <summary>
/// Development aid: when MANE_FFMPEG_SCRIPT points at a text file, runs its commands against the first
/// window (add, seek, split, export, snapshot…) and prints state, so the real UI can be checked end to end.
/// </summary>
internal static class DebugAutomation
{
    public static void StartIfRequested(MainWindow window)
    {
        if (Environment.GetEnvironmentVariable("MANE_FFMPEG_SCRIPT") is not { Length: > 0 } script || !File.Exists(script))
            return;
        var lines = File.ReadAllLines(script);
        Dispatcher.UIThread.Post(async () => await RunAsync(window, lines), DispatcherPriority.Background);
    }

    private static async Task RunAsync(MainWindow window, string[] lines)
    {
        var vm = window.ViewModel;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var space = line.IndexOf(' ');
            var cmd = space < 0 ? line : line[..space];
            var arg = space < 0 ? "" : line[(space + 1)..].Trim();
            Console.WriteLine($"[script] {line}");
            try
            {
                switch (cmd)
                {
                    case "wait":
                        await Task.Delay(TimeSpan.FromSeconds(double.Parse(arg, CultureInfo.InvariantCulture)));
                        break;
                    case "waitfor":
                        // waitfor ready|idle|loaded
                        for (var i = 0; i < 600; i++)
                        {
                            var done = arg switch
                            {
                                "ready" => vm.Sources.Count > 0 && vm.Sources.All(s => s.Status != Core.Media.SourceStatus.Analyzing),
                                "idle" => !vm.Export.IsExporting,
                                _ => true,
                            };
                            if (done)
                                break;
                            await Task.Delay(100);
                        }
                        break;
                    case "add":
                        await vm.AddMediaAsync(arg.Split('|'));
                        break;
                    case "open":
                        await WindowManager.Instance.OpenProjectAsync(arg, window);
                        break;
                    case "save":
                        await SaveToAsync(window, arg);
                        break;
                    case "play":
                        if (!vm.Preview.IsPlaying)
                            vm.Preview.PlayPauseCommand.Execute(null);
                        break;
                    case "pause":
                        if (vm.Preview.IsPlaying)
                            vm.Preview.PlayPauseCommand.Execute(null);
                        break;
                    case "seek":
                        vm.Preview.SeekTo(TimeSpan.FromSeconds(double.Parse(arg, CultureInfo.InvariantCulture)), false);
                        break;
                    case "split":
                        vm.SplitCommand.Execute(null);
                        break;
                    case "select":
                        vm.Timeline.Select(vm.State.Timeline.Clips[int.Parse(arg, CultureInfo.InvariantCulture)].Id, false, false);
                        break;
                    case "select-all":
                        vm.SelectAllCommand.Execute(null);
                        break;
                    case "move":
                        // move <insertIndex> (moves the selection)
                        vm.Timeline.MoveSelection(int.Parse(arg, CultureInfo.InvariantCulture));
                        break;
                    case "nudge":
                        vm.NudgeAudio(TimeSpan.FromMilliseconds(double.Parse(arg, CultureInfo.InvariantCulture)));
                        break;
                    case "delete":
                        vm.DeleteSelectionCommand.Execute(null);
                        break;
                    case "undo":
                        vm.UndoCommand.Execute(null);
                        break;
                    case "redo":
                        vm.RedoCommand.Execute(null);
                        break;
                    case "preset":
                        vm.Export.SelectedPreset = vm.Export.Presets.First(p => p.Name == arg);
                        break;
                    case "output":
                        vm.Export.OutputPath = arg;
                        break;
                    case "command":
                        vm.Export.ShowCommand = true;
                        await Task.Delay(1500);
                        Console.WriteLine(vm.Export.CommandPreview);
                        break;
                    case "export":
                        await vm.Export.ExportCommand.ExecuteAsync(null);
                        Console.WriteLine($"[export] {vm.Export.ExportStatus}\n{vm.Export.ExportLog}");
                        break;
                    case "newwindow":
                        WindowManager.Instance.OpenNew(window);
                        break;
                    case "window":
                        // window <index>: later commands target that window
                        window = WindowManager.Instance.Windows[int.Parse(arg, CultureInfo.InvariantCulture)];
                        vm = window.ViewModel;
                        break;
                    case "export-async":
                        _ = vm.Export.ExportCommand.ExecuteAsync(null);
                        break;
                    case "export-pause":
                        vm.Export.PauseExportCommand.Execute(null);
                        break;
                    case "export-resume":
                        vm.Export.ResumeExportCommand.Execute(null);
                        break;
                    case "export-progress":
                        Console.WriteLine($"[progress] {vm.Export.ExportProgressText} paused={vm.Export.IsExportPaused} status={vm.Export.ExportStatus}");
                        break;
                    case "drag":
                    {
                        // drag <ruler|video|audio> <fromSeconds> <toSeconds> [shift]
                        var parts = arg.Split(' ');
                        await DragAsync(window, parts[0], double.Parse(parts[1], CultureInfo.InvariantCulture),
                            double.Parse(parts[2], CultureInfo.InvariantCulture), parts.Length > 3 && parts[3] == "shift");
                        break;
                    }
                    case "focus":
                        // focus <x:Name of a control in the window>
                        (window.FindControl<Avalonia.Controls.Control>(arg) ?? throw new InvalidOperationException($"no control {arg}")).Focus();
                        break;
                    case "click":
                    {
                        // click <x:Name of a button in the window>: runs its command, as a click would
                        var button = window.FindControl<Button>(arg) ?? throw new InvalidOperationException($"no button {arg}");
                        button.Command?.Execute(button.CommandParameter);
                        break;
                    }
                    case "keypress":
                    {
                        // Like a real key press: KeyDown then KeyUp, both sent to the focused element.
                        var key = Enum.Parse<Key>(arg);
                        var target = window.FocusManager?.GetFocusedElement() as Avalonia.Interactivity.Interactive ?? window;
                        Console.WriteLine($"[keypress] target={target.GetType().Name}");
                        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = target });
                        await Task.Delay(120);
                        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, Source = target });
                        break;
                    }
                    case "text":
                    {
                        // text <x:Name of a text box> <text>: replaces its text, as typing would (bindings aren't pushed)
                        var parts = arg.Split(' ', 2);
                        var box = window.FindControl<TextBox>(parts[0]) ?? throw new InvalidOperationException($"no text box {parts[0]}");
                        box.Text = parts.Length > 1 ? parts[1] : "";
                        break;
                    }
                    case "focused":
                    {
                        var focused = window.FocusManager?.GetFocusedElement();
                        Console.WriteLine($"[focused] {focused?.GetType().Name ?? "nothing"} {(focused as Control)?.Name}");
                        break;
                    }
                    case "key":
                        window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Enum.Parse<Key>(arg), Source = window });
                        break;
                    case "preview-source":
                        vm.Preview.PreviewSourceCommand.Execute(vm.Sources[int.Parse(arg, CultureInfo.InvariantCulture)]);
                        break;
                    case "source-seek":
                        vm.Preview.Player?.Command("seek", arg, "absolute+exact");
                        break;
                    case "compact":
                        vm.Preview.IsCompact = arg == "on";
                        break;
                    case "size":
                        Console.WriteLine($"[size] {window.Width:0}x{window.Height:0} at {window.Position.X},{window.Position.Y} min={window.MinHeight}");
                        break;
                    case "theme":
                        App.ApplyTheme(Enum.Parse<Core.Persistence.ThemeChoice>(arg, ignoreCase: true));
                        break;
                    case "bounds":
                    {
                        var t = window.TimelineView.TranslatePoint(default, window) ?? default;
                        var v = window.Video.TranslatePoint(default, window) ?? default;
                        Console.WriteLine($"[bounds] {arg} timeline y={t.Y:0.#} h={window.TimelineView.Bounds.Height:0.#} video y={v.Y:0.#} h={window.Video.Bounds.Height:0.#} exporting={vm.Export.IsExporting}");
                        break;
                    }
                    case "snap":
                        vm.SnapToKeyframes = arg == "on";
                        break;
                    case "presets-keys":
                    {
                        // presets-keys <Key> [<Key>…]: sends Cmd/Ctrl+<Key> presses to the arguments box of the presets window
                        var dialog = new PresetsWindow();
                        dialog.Show();
                        await Task.Delay(800);
                        var box = dialog.FindControl<TextBox>("ArgumentsBox")!;
                        box.Focus();
                        var modifier = EditMenu.CommandModifier;
                        foreach (var k in arg.Split(' '))
                        {
                            box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Enum.Parse<Key>(k), KeyModifiers = modifier, Source = box });
                            await Task.Delay(400);
                            Console.WriteLine($"[presets-keys] after Cmd+{k}: '{box.Text}' sel={box.SelectionStart}-{box.SelectionEnd}");
                        }
                        dialog.Close();
                        break;
                    }
                    case "presets-menu":
                    {
                        // presets-menu <Item>[,<Item>…]: clicks Edit menu items of the presets window with its arguments box focused
                        var dialog = new PresetsWindow();
                        dialog.Show();
                        await Task.Delay(800);
                        var box = dialog.FindControl<TextBox>("ArgumentsBox")!;
                        box.Focus();
                        var edit = NativeMenu.GetMenu(dialog)!.Items.OfType<NativeMenuItem>().First(i => i.Header == "Edit").Menu!;
                        foreach (var header in arg.Split(','))
                        {
                            var item = edit.Items.OfType<NativeMenuItem>().First(i => i.Header == header);
                            var methods = typeof(NativeMenuItem).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                            var click = methods.First(m => m.Name.Contains("Click") && m.GetParameters().Length == 0 && !m.Name.StartsWith("get_") && !m.Name.StartsWith("add_") && !m.Name.StartsWith("remove_"));
                            click.Invoke(item, null);
                            await Task.Delay(400);
                            Console.WriteLine($"[presets-menu] after {header} ({click.Name}): '{box.Text}' sel={box.SelectionStart}-{box.SelectionEnd}");
                        }
                        dialog.Close();
                        break;
                    }
                    case "popup-esc":
                    {
                        // popup-esc presets|settings|ask|about [focus]: opens the popup, presses Esc, reports if it closed
                        var parts = arg.Split(' ');
                        Avalonia.Controls.Window popup;
                        Task? pending = null;
                        switch (parts[0])
                        {
                            case "presets":
                                popup = new PresetsWindow();
                                popup.Show(window);
                                break;
                            case "settings":
                                popup = new SettingsWindow();
                                popup.Show(window);
                                break;
                            default:
                                pending = parts[0] == "ask"
                                    ? Dialogs.AskAsync(window, "Save changes?", "Test", "Save", "Don't save", "Cancel").ContinueWith(t => Console.WriteLine($"[popup-esc] ask result {t.Result}"), TaskScheduler.FromCurrentSynchronizationContext())
                                    : Dialogs.InfoAsync(window, "About", "Test");
                                await Task.Delay(800);
                                popup = window.OwnedWindows.Last();
                                break;
                        }
                        await Task.Delay(800);
                        if (parts.Length > 1 && popup.FindControl<Control>(parts[1]) is { } inner)
                            inner.Focus();
                        var closed = false;
                        popup.Closed += (_, _) => closed = true;
                        var target = popup.FocusManager?.GetFocusedElement() as Avalonia.Interactivity.Interactive ?? popup;
                        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, Source = target });
                        await Task.Delay(500);
                        Console.WriteLine($"[popup-esc] {arg}: focus={target.GetType().Name} closed={closed}");
                        if (!closed)
                            popup.Close();
                        if (pending is not null)
                            await pending;
                        break;
                    }
                    case "dropdown-esc":
                    {
                        // Opens the settings theme dropdown, presses Esc twice: first closes the dropdown, then the window.
                        var popup = new SettingsWindow();
                        popup.Show(window);
                        await Task.Delay(800);
                        var combo = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(popup).OfType<ComboBox>().First();
                        combo.Focus();
                        combo.IsDropDownOpen = true;
                        await Task.Delay(400);
                        var closed = false;
                        popup.Closed += (_, _) => closed = true;
                        for (var i = 1; i <= 2; i++)
                        {
                            var target = popup.FocusManager?.GetFocusedElement() as Avalonia.Interactivity.Interactive ?? popup;
                            target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, Source = target });
                            await Task.Delay(400);
                            Console.WriteLine($"[dropdown-esc] Esc #{i}: dropdownOpen={combo.IsDropDownOpen} windowClosed={closed}");
                        }
                        if (!closed)
                            popup.Close();
                        break;
                    }
                    case "presets-snapshot":
                        await SnapshotWindowAsync(new PresetsWindow(), arg);
                        break;
                    case "settings-snapshot":
                        await SnapshotWindowAsync(new SettingsWindow(), arg);
                        break;
                    case "setup-snapshot":
                    {
                        // setup-snapshot nobrew|<fake brew> <png>: the install dialog offering everything. With a fake brew
                        // script it clicks Install and also snapshots while brew runs (<png>.running.png).
                        var parts = arg.Split(' ', 2);
                        var brew = parts[0] == "nobrew" ? null : parts[0];
                        var setup = new SetupViewModel(SetupViewModel.AllComponents(), () => brew, _ => Task.CompletedTask);
                        var dialog = new SetupWindow(setup);
                        dialog.Show(window);
                        await Task.Delay(800);
                        if (brew is not null)
                        {
                            _ = setup.ContinueCommand.ExecuteAsync(null);
                            await Task.Delay(1500);
                            SaveSnapshot(dialog, parts[1] + ".running.png");
                            while (setup.IsInstalling)
                                await Task.Delay(100);
                            await Task.Delay(500);
                        }
                        SaveSnapshot(dialog, parts[1]);
                        Console.WriteLine($"[setup] step={setup.Step} status='{setup.Status}'");
                        dialog.Close();
                        break;
                    }
                    case "snapshot":
                        await Task.Delay(300);
                        Snapshot(window, arg);
                        break;
                    case "state":
                        PrintState(window);
                        break;
                    case "quit":
                        foreach (var w in WindowManager.Instance.Windows.ToList())
                            w.ForceClose();
                        return;
                    default:
                        Console.WriteLine($"[script] unknown command {cmd}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[script] {cmd} failed: {ex}");
            }
        }
    }

    private static async Task SaveToAsync(MainWindow window, string path)
    {
        var project = new Project { State = window.ViewModel.State, Layout = window.CaptureLayout() };
        await Core.Persistence.ProjectFile.SaveAsync(project, path);
    }

    private static void Snapshot(MainWindow window, string path)
    {
        var size = new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height);
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(window);
        bitmap.Save(path);
        window.Video.SaveFrame(path + ".frame.png");
        Console.WriteLine($"[snapshot] {path}");
    }

    /// <summary>Synthetic mouse drag on the timeline, in timeline seconds (x) and track rows (y).</summary>
    private static async Task DragAsync(MainWindow window, string row, double from, double to, bool shift)
    {
        var control = window.TimelineView;
        var vm = window.ViewModel.Timeline;
        var y = row switch { "ruler" => TimelineLayout.RulerCenter, "video" => TimelineLayout.VideoCenter, _ => TimelineLayout.AudioCenter };
        Point At(double t) => new(TimelineLayout.X(vm.Zoom, vm.Scroll, TimeSpan.FromSeconds(t)), y);
        var origin = control.TranslatePoint(new Point(0, 0), window) ?? default;
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        var mods = shift ? KeyModifiers.Shift : KeyModifiers.None;
        var raw = shift ? RawInputModifiers.Shift : RawInputModifiers.None;
        ulong ts = 1;
        control.RaiseEvent(new PointerPressedEventArgs(control, pointer, window, origin + At(from), ts++,
            new PointerPointProperties(raw | RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), mods));
        for (var i = 1; i <= 10; i++)
        {
            var t = from + (to - from) * i / 10;
            control.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, control, pointer, window, origin + At(t), ts++,
                new PointerPointProperties(raw | RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other), mods));
            await Task.Delay(16);
        }
        control.RaiseEvent(new PointerReleasedEventArgs(control, pointer, window, origin + At(to), ts,
            new PointerPointProperties(raw, PointerUpdateKind.LeftButtonReleased), mods, MouseButton.Left));
    }

    private static async Task SnapshotWindowAsync(Avalonia.Controls.Window dialog, string path)
    {
        dialog.Show();
        await Task.Delay(800);
        SaveSnapshot(dialog, path);
        dialog.Close();
    }

    private static void SaveSnapshot(Avalonia.Controls.Window dialog, string path)
    {
        var size = new PixelSize((int)dialog.Bounds.Width, (int)dialog.Bounds.Height);
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(dialog);
        bitmap.Save(path);
        Console.WriteLine($"[snapshot] {path}");
    }

    private static void PrintState(MainWindow window)
    {
        var vm = window.ViewModel;
        Console.WriteLine($"[state] title={vm.Title} snap={vm.SnapToKeyframes} playing={vm.Preview.IsPlaying} playhead={vm.Timeline.Playhead} zoom={vm.Timeline.Zoom:0.##} " +
                          $"preview='{vm.Preview.PreviewMessage}' status='{vm.StatusMessage}' mpv={vm.Preview.Player is not null} " +
                          $"time-pos={vm.Preview.Player?.GetString("time-pos")} vid={vm.Preview.Player?.GetString("vid")} aid={vm.Preview.Player?.GetString("aid")} " +
                          $"path={vm.Preview.Player?.GetString("path")?[..Math.Min(80, vm.Preview.Player?.GetString("path")?.Length ?? 0)]}");
        Console.WriteLine($"[state] video counters={window.Video.Counters} center={window.Video.CenterPixel()}");
        foreach (var s in vm.Sources)
            Console.WriteLine($"[state]   source {s.Name} {s.Status} {s.Details}");
        foreach (var c in vm.State.Timeline.Clips)
            Console.WriteLine($"[state]   clip {c.Id} {vm.State.Source(c.SourceId)?.DisplayName} {c.In}–{c.Out} offset={c.AudioOffset.TotalMilliseconds}ms{(vm.Timeline.Selection.Contains(c.Id) ? " *" : "")}");
    }
}
#endif
