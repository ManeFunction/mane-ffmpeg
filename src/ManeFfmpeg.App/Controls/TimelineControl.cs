using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ManeFfmpeg.App.ViewModels;
using ManeFfmpeg.Core.Editing;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.App.Controls;

/// <summary>
/// The editing timeline: a ruler, the video row (V1) and the audio row (A1). Click to seek and select,
/// drag clips to reorder, drag clip edges to trim, drag on A1 to slip a clip's audio.
/// </summary>
public sealed class TimelineControl : Control
{
    public static readonly StyledProperty<TimelineViewModel?> TimelineProperty =
        AvaloniaProperty.Register<TimelineControl, TimelineViewModel?>(nameof(Timeline));

    private const double Header = 44;
    private const double RulerHeight = 26;
    private const double VideoTop = RulerHeight + 8;
    private const double TrackHeight = 62;
    private const double AudioTop = VideoTop + TrackHeight + 6;
    private const double EdgeGrab = 6;
    private const double DragThreshold = 4;

    private static readonly Typeface Font = new("Inter");
    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#17181C"));
    private static readonly IBrush RulerBackground = new SolidColorBrush(Color.Parse("#202127"));
    private static readonly IBrush TrackBackground = new SolidColorBrush(Color.Parse("#1D1E23"));
    private static readonly IBrush HeaderBrush = new SolidColorBrush(Color.Parse("#26272E"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#A9ADB7"));
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255));
    private static readonly IBrush WaveBrush = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255));
    private static readonly IBrush SilenceBrush = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0));
    private static readonly IBrush BadgeBrush = new SolidColorBrush(Color.Parse("#F5A623"));
    private static readonly IBrush MissingBrush = new SolidColorBrush(Color.Parse("#7A2E2E"));
    private static readonly IPen TickPen = new Pen(new SolidColorBrush(Color.Parse("#4A4C55")), 1);
    private static readonly IPen MinorTickPen = new Pen(new SolidColorBrush(Color.Parse("#34363D")), 1);
    private static readonly IPen SelectedPen = new Pen(Brushes.White, 2);
    private static readonly IPen ClipPen = new Pen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), 1);
    private static readonly IPen PlayheadPen = new Pen(new SolidColorBrush(Color.Parse("#FF5252")), 1.5);
    private static readonly IPen DropPen = new Pen(new SolidColorBrush(Color.Parse("#FFD54F")), 3);
    private static readonly IPen WavePen = new Pen(WaveBrush, 1);
    private static readonly IPen KeyframePen = new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 1);
    private static readonly IPen HatchPen = new Pen(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), 1);
    private static readonly Cursor ResizeCursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor SlipCursor = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private enum Drag { None, Scrub, PendingClip, Reorder, TrimIn, TrimOut, PendingSlip, Slip }

    private Drag _drag;
    private Point _press;
    private string? _clipId;
    private Clip? _clip;
    private Timeline? _gestureStart;
    private int _insertIndex = -1;
    private KeyModifiers _pressModifiers;

    public TimelineControl()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    public TimelineViewModel? Timeline
    {
        get => GetValue(TimelineProperty);
        set => SetValue(TimelineProperty, value);
    }

    private static KeyModifiers CommandModifier => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != TimelineProperty)
            return;
        if (change.OldValue is TimelineViewModel old)
            old.Invalidated -= OnInvalidated;
        if (change.NewValue is TimelineViewModel vm)
        {
            vm.Invalidated += OnInvalidated;
            vm.ViewportWidth = Math.Max(100, Bounds.Width - Header);
        }
        InvalidateVisual();
    }

    private void OnInvalidated()
    {
        if (Timeline is { Zoom: <= 0 } vm && vm.Timeline.Clips.Count > 0 && Bounds.Width > Header)
            vm.Fit();
        InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (Timeline is { } vm)
        {
            vm.ViewportWidth = Math.Max(100, Bounds.Width - Header);
            if (vm.Zoom <= 0 && vm.Timeline.Clips.Count > 0)
                vm.Fit();
        }
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(0, AudioTop + TrackHeight + 10);

    private double X(TimelineViewModel vm, TimeSpan t) => Header + (t.TotalSeconds - vm.Scroll) * vm.Zoom;

    private static TimeSpan T(TimelineViewModel vm, double x) =>
        TimeSpan.FromSeconds(vm.Scroll + (x - Header) / Math.Max(vm.Zoom, 1e-9));

    // ---- Drawing ----------------------------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        context.FillRectangle(Background, new Rect(size));
        if (Timeline is not { } vm)
            return;
        if (vm.Zoom <= 0)
        {
            DrawText(context, "Add videos to build a timeline. Drag clips to reorder, drag edges to trim, drag the audio row to fix sync.",
                new Point(Header + 12, AudioTop - 8), TextBrush, 13);
            return;
        }

        DrawRuler(context, vm, size);
        context.FillRectangle(TrackBackground, new Rect(Header, VideoTop, size.Width, TrackHeight));
        context.FillRectangle(TrackBackground, new Rect(Header, AudioTop, size.Width, TrackHeight));

        using (context.PushClip(new Rect(Header, 0, Math.Max(0, size.Width - Header), size.Height)))
        {
            var start = TimeSpan.Zero;
            var clips = vm.Timeline.Clips;
            for (var i = 0; i < clips.Count; i++)
            {
                var clip = clips[i];
                var x0 = X(vm, start);
                var x1 = X(vm, start + clip.Duration);
                if (x1 >= Header && x0 <= size.Width)
                    DrawClip(context, vm, clip, start, x0, x1, size.Width);
                start += clip.Duration;
            }

            if (_drag == Drag.Reorder && _insertIndex >= 0)
            {
                var x = X(vm, vm.Timeline.StartOf(_insertIndex));
                context.DrawLine(DropPen, new Point(x, VideoTop - 4), new Point(x, AudioTop + TrackHeight + 4));
            }

            var ph = X(vm, vm.Playhead);
            if (ph >= Header - 1 && ph <= size.Width + 1)
            {
                context.DrawLine(PlayheadPen, new Point(ph, RulerHeight - 6), new Point(ph, size.Height));
                var head = new StreamGeometry();
                using (var g = head.Open())
                {
                    g.BeginFigure(new Point(ph - 6, RulerHeight - 12), true);
                    g.LineTo(new Point(ph + 6, RulerHeight - 12));
                    g.LineTo(new Point(ph, RulerHeight - 4));
                    g.EndFigure(true);
                }
                context.DrawGeometry(PlayheadPen.Brush, null, head);
            }
        }

        context.FillRectangle(HeaderBrush, new Rect(0, 0, Header, size.Height));
        DrawText(context, "V1", new Point(14, VideoTop + TrackHeight / 2 - 8), TextBrush, 12);
        DrawText(context, "A1", new Point(14, AudioTop + TrackHeight / 2 - 8), TextBrush, 12);
    }

    private void DrawRuler(DrawingContext context, TimelineViewModel vm, Size size)
    {
        context.FillRectangle(RulerBackground, new Rect(0, 0, size.Width, RulerHeight));
        double[] steps = [0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200];
        var step = steps.FirstOrDefault(s => s * vm.Zoom >= 90, 7200);
        var ratio = step is 15 or 30 or 900 or 1800 ? 3 : step is 2 or 0.2 or 120 or 7200 ? 4 : 5;
        var minor = step / ratio;
        var last = vm.Scroll + (size.Width - Header) / vm.Zoom;
        var end = vm.Timeline.Duration.TotalSeconds;
        // Integer tick indexes: accumulating floating-point steps would drift and mislabel ticks.
        for (var k = (long)Math.Floor(vm.Scroll / minor); k * minor <= last; k++)
        {
            var t = k * minor;
            var x = Header + (t - vm.Scroll) * vm.Zoom;
            if (x < Header)
                continue;
            var major = k % ratio == 0;
            context.DrawLine(major ? TickPen : MinorTickPen, new Point(x, major ? 8 : 17), new Point(x, RulerHeight));
            if (major && t <= end + step)
                DrawText(context, RulerLabel(t, step), new Point(x + 4, 4), TextBrush, 10);
        }
    }

    private static string RulerLabel(double seconds, double step)
    {
        var t = TimeSpan.FromMilliseconds(Math.Round(seconds * 1000));
        if (step < 1)
            return t.ToString(t.TotalHours >= 1 ? @"h\:mm\:ss\.f" : @"m\:ss\.f", CultureInfo.InvariantCulture);
        return t.ToString(t.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
    }

    private void DrawClip(DrawingContext context, TimelineViewModel vm, Clip clip, TimeSpan start, double x0, double x1, double width)
    {
        var source = vm.State.Source(clip.SourceId);
        var color = SourceItemViewModel.Palette[vm.ColorIndex(clip.SourceId) % SourceItemViewModel.Palette.Length];
        var missing = source is null || vm.IsMissing(clip.SourceId);
        var selected = vm.Selection.Contains(clip.Id);
        var videoRect = new Rect(x0 + 1, VideoTop + 1, Math.Max(1, x1 - x0 - 2), TrackHeight - 2);
        var audioRect = new Rect(x0 + 1, AudioTop + 1, Math.Max(1, x1 - x0 - 2), TrackHeight - 2);
        var dragged = _drag == Drag.Reorder && selected;
        var alpha = dragged ? (byte)110 : (byte)230;

        // Video row
        context.DrawRectangle(missing ? MissingBrush : new SolidColorBrush(color, alpha / 255.0), ClipPen, videoRect, 4, 4);
        if (vm.ShowKeyframes && source is not null && vm.Keyframes(source) is { } keyframes)
        {
            foreach (var k in keyframes.Times)
            {
                if (k < clip.In || k > clip.Out)
                    continue;
                var x = X(vm, start + (k - clip.In));
                if (x < Header || x > width)
                    continue;
                context.DrawLine(KeyframePen, new Point(x, videoRect.Top + 1), new Point(x, videoRect.Top + 8));
            }
        }
        using (context.PushClip(videoRect))
        {
            var name = source?.DisplayName ?? "missing";
            DrawText(context, name, new Point(Math.Max(videoRect.Left, Header) + 6, videoRect.Top + 6), LabelBrush, 12, FontWeight.SemiBold);
            DrawText(context, $"{TimeFormat.Display(clip.In)} – {TimeFormat.Display(clip.Out)}  ·  {TimeFormat.Display(clip.Duration)}",
                new Point(Math.Max(videoRect.Left, Header) + 6, videoRect.Top + 24), LabelBrush, 10);
        }

        // Audio row
        var audioFill = new SolidColorBrush(Darken(color, 0.55), dragged ? 0.4 : 0.9);
        context.DrawRectangle(missing ? MissingBrush : audioFill, ClipPen, audioRect, 4, 4);
        if (source is not null && vm.Info(source.Id) is { } info)
        {
            using (context.PushClip(audioRect))
            {
                var audio = info.Audio(source.AudioStreamIndex);
                if (audio is null)
                {
                    DrawText(context, "no audio", new Point(Math.Max(audioRect.Left, Header) + 6, audioRect.Top + 22), TextBrush, 11);
                }
                else
                {
                    var audioEnd = audio.Duration ?? info.Duration;
                    DrawSilence(context, vm, clip, start, audioRect, audioEnd);
                    DrawWaveform(context, vm, clip, start, audioRect, vm.Waveform(source), width);
                }
                if (clip.AudioOffset != TimeSpan.Zero)
                    DrawBadge(context, $"♪ {TimeFormat.Offset(clip.AudioOffset)}", new Point(Math.Max(audioRect.Left, Header) + 5, audioRect.Top + 4));
            }
        }

        if (selected)
        {
            context.DrawRectangle(null, SelectedPen, videoRect.Deflate(1), 4, 4);
            context.DrawRectangle(null, SelectedPen, audioRect.Deflate(1), 4, 4);
        }
    }

    /// <summary>Hatches the parts of the clip where the slipped audio runs past the file (silence on export).</summary>
    private void DrawSilence(DrawingContext context, TimelineViewModel vm, Clip clip, TimeSpan start, Rect rect, TimeSpan audioEnd)
    {
        var lead = clip.AudioIn < TimeSpan.Zero ? -clip.AudioIn : TimeSpan.Zero;
        var tailStart = audioEnd - clip.AudioIn;
        void Hatch(double a, double b)
        {
            if (b - a < 1)
                return;
            var r = new Rect(a, rect.Top, b - a, rect.Height);
            context.FillRectangle(SilenceBrush, r);
            using (context.PushClip(r))
                for (var x = a - rect.Height; x < b; x += 8)
                    context.DrawLine(HatchPen, new Point(x, rect.Bottom), new Point(x + rect.Height, rect.Top));
        }
        if (lead > TimeSpan.Zero)
            Hatch(X(vm, start), X(vm, start + (lead < clip.Duration ? lead : clip.Duration)));
        if (tailStart < clip.Duration)
            Hatch(X(vm, start + (tailStart > TimeSpan.Zero ? tailStart : TimeSpan.Zero)), X(vm, start + clip.Duration));
    }

    private void DrawWaveform(DrawingContext context, TimelineViewModel vm, Clip clip, TimeSpan start, Rect rect,
        Core.Media.Waveform? waveform, double width)
    {
        if (waveform is null)
            return;
        var mid = rect.Top + rect.Height / 2 + 4;
        var half = rect.Height / 2 - 8;
        var left = Math.Max(rect.Left, Header);
        var right = Math.Min(rect.Right, width);
        var perPixel = TimeSpan.FromSeconds(1 / vm.Zoom);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            for (var x = Math.Floor(left); x < right; x++)
            {
                var local = T(vm, x) - start;
                var sourceTime = clip.AudioIn + local;
                if (sourceTime + perPixel < TimeSpan.Zero || sourceTime > waveform.Duration)
                    continue;
                var h = waveform.Max(sourceTime, sourceTime + perPixel) / 255.0 * half;
                if (h < 0.5)
                    h = 0.5;
                g.BeginFigure(new Point(x + 0.5, mid - h), false);
                g.LineTo(new Point(x + 0.5, mid + h));
                g.EndFigure(false);
            }
        }
        context.DrawGeometry(null, WavePen, geometry);
    }

    private static void DrawBadge(DrawingContext context, string text, Point at)
    {
        var formatted = Text(text, Brushes.Black, 10, FontWeight.SemiBold);
        var rect = new Rect(at.X, at.Y, formatted.Width + 10, formatted.Height + 2);
        context.DrawRectangle(BadgeBrush, null, rect, 6, 6);
        context.DrawText(formatted, new Point(at.X + 5, at.Y + 1));
    }

    private static Color Darken(Color c, double factor) =>
        Color.FromRgb((byte)(c.R * factor), (byte)(c.G * factor), (byte)(c.B * factor));

    private static FormattedText Text(string text, IBrush brush, double size, FontWeight weight = FontWeight.Normal) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(Font.FontFamily, FontStyle.Normal, weight), size, brush);

    private static void DrawText(DrawingContext context, string text, Point at, IBrush brush, double size, FontWeight weight = FontWeight.Normal) =>
        context.DrawText(Text(text, brush, size, weight), at);

    // ---- Hit testing ------------------------------------------------------------------------------

    private (int Index, Clip Clip, TimeSpan Start, double X0, double X1)? HitClip(TimelineViewModel vm, double x)
    {
        var start = TimeSpan.Zero;
        var clips = vm.Timeline.Clips;
        for (var i = 0; i < clips.Count; i++)
        {
            var x0 = X(vm, start);
            var x1 = X(vm, start + clips[i].Duration);
            if (x >= x0 && x <= x1)
                return (i, clips[i], start, x0, x1);
            start += clips[i].Duration;
        }
        return null;
    }

    private static bool InVideoRow(Point p) => p.Y >= VideoTop && p.Y < VideoTop + TrackHeight;

    private static bool InAudioRow(Point p) => p.Y >= AudioTop && p.Y < AudioTop + TrackHeight;

    private int InsertIndexAt(TimelineViewModel vm, double x)
    {
        var best = 0;
        var bestDistance = double.MaxValue;
        for (var k = 0; k <= vm.Timeline.Clips.Count; k++)
        {
            var d = Math.Abs(X(vm, vm.Timeline.StartOf(k)) - x);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = k;
            }
        }
        return best;
    }

    // ---- Input ------------------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Timeline is not { Zoom: > 0 } vm || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        Focus();
        var p = e.GetPosition(this);
        if (p.X < Header)
            return;
        _press = p;
        _pressModifiers = e.KeyModifiers;
        e.Pointer.Capture(this);
        e.Handled = true;

        var hit = p.Y >= VideoTop ? HitClip(vm, p.X) : null;
        if (hit is not { } h || !(InVideoRow(p) || InAudioRow(p)))
        {
            _drag = Drag.Scrub;
            if (p.Y >= VideoTop)
                vm.ClearSelection();
            vm.Seek(T(vm, p.X), scrubbing: true);
            return;
        }

        _clipId = h.Clip.Id;
        _clip = h.Clip;
        _gestureStart = vm.Timeline;
        if (InVideoRow(p) && p.X - h.X0 <= EdgeGrab && h.X1 - h.X0 > EdgeGrab * 3)
        {
            _drag = Drag.TrimIn;
            vm.BeginGesture();
        }
        else if (InVideoRow(p) && h.X1 - p.X <= EdgeGrab && h.X1 - h.X0 > EdgeGrab * 3)
        {
            _drag = Drag.TrimOut;
            vm.BeginGesture();
        }
        else
        {
            _drag = InVideoRow(p) ? Drag.PendingClip : Drag.PendingSlip;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Timeline is not { Zoom: > 0 } vm)
            return;
        var p = e.GetPosition(this);
        if (_drag == Drag.None)
        {
            UpdateCursor(vm, p);
            return;
        }
        var dx = p.X - _press.X;
        switch (_drag)
        {
            case Drag.Scrub:
                vm.Seek(T(vm, p.X), scrubbing: true);
                break;
            case Drag.PendingClip when Math.Abs(dx) > DragThreshold:
                if (!vm.Selection.Contains(_clipId!))
                    vm.Select(_clipId!, toggle: false, range: false);
                _drag = Drag.Reorder;
                _insertIndex = InsertIndexAt(vm, p.X);
                InvalidateVisual();
                break;
            case Drag.Reorder:
                _insertIndex = InsertIndexAt(vm, p.X);
                InvalidateVisual();
                break;
            case Drag.TrimIn:
            {
                var newIn = SnapSource(vm, _clip!, _clip!.In + TimeSpan.FromSeconds(dx / vm.Zoom));
                vm.PreviewTimeline(TimelineEditor.TrimIn(_gestureStart!, _clipId!, newIn));
                break;
            }
            case Drag.TrimOut:
            {
                var length = vm.Info(_clip!.SourceId)?.Duration ?? _clip.Out;
                var newOut = SnapSource(vm, _clip, _clip.Out + TimeSpan.FromSeconds(dx / vm.Zoom));
                vm.PreviewTimeline(TimelineEditor.TrimOut(_gestureStart!, _clipId!, newOut, length));
                break;
            }
            case Drag.PendingSlip when Math.Abs(dx) > DragThreshold:
                if (!vm.Selection.Contains(_clipId!))
                    vm.Select(_clipId!, toggle: false, range: false);
                vm.BeginGesture();
                _drag = Drag.Slip;
                break;
            case Drag.Slip:
            {
                var fine = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? 0.1 : 1;
                var ms = Math.Round(dx / vm.Zoom * 1000 * fine);
                var delta = TimeSpan.FromMilliseconds(ms);
                vm.PreviewTimeline(TimelineEditor.NudgeAudioOffset(_gestureStart!, vm.Selection, delta));
                vm.PreviewAudioDrag(delta);
                break;
            }
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (Timeline is not { } vm || _drag == Drag.None)
            return;
        var p = e.GetPosition(this);
        var command = (_pressModifiers & CommandModifier) != 0;
        var shift = (_pressModifiers & KeyModifiers.Shift) != 0;
        switch (_drag)
        {
            case Drag.Scrub:
                vm.EndScrub();
                break;
            case Drag.PendingClip:
            case Drag.PendingSlip:
                vm.Select(_clipId!, toggle: command, range: shift);
                if (!command && !shift)
                    vm.Seek(T(vm, p.X), scrubbing: false);
                break;
            case Drag.Reorder:
                vm.MoveSelection(_insertIndex);
                break;
            case Drag.TrimIn:
            case Drag.TrimOut:
                vm.EndGesture();
                break;
            case Drag.Slip:
                vm.EndGesture();
                vm.PreviewAudioDrag(TimeSpan.Zero);
                break;
        }
        Reset(e.Pointer);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_drag is Drag.TrimIn or Drag.TrimOut or Drag.Slip)
            Timeline?.EndGesture();
        if (_drag == Drag.Scrub)
            Timeline?.EndScrub();
        _drag = Drag.None;
        _insertIndex = -1;
        InvalidateVisual();
    }

    private void Reset(IPointer pointer)
    {
        _drag = Drag.None;
        _insertIndex = -1;
        _clip = null;
        _clipId = null;
        _gestureStart = null;
        pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Timeline is not { Zoom: > 0 } vm)
            return;
        var p = e.GetPosition(this);
        if ((e.KeyModifiers & CommandModifier) != 0 || (e.KeyModifiers & KeyModifiers.Alt) != 0)
        {
            vm.ZoomBy(Math.Pow(1.15, e.Delta.Y), Math.Max(0, p.X - Header));
        }
        else
        {
            var delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
            var maxScroll = Math.Max(0, vm.Timeline.Duration.TotalSeconds - vm.ViewportWidth / vm.Zoom * 0.5);
            vm.Scroll = Math.Clamp(vm.Scroll - delta * 60 / vm.Zoom, 0, maxScroll);
        }
        e.Handled = true;
    }

    private void UpdateCursor(TimelineViewModel vm, Point p)
    {
        if (p.Y >= VideoTop && HitClip(vm, p.X) is { } h)
        {
            if (InVideoRow(p) && h.X1 - h.X0 > EdgeGrab * 3 && (p.X - h.X0 <= EdgeGrab || h.X1 - p.X <= EdgeGrab))
            {
                Cursor = ResizeCursor;
                return;
            }
            if (InAudioRow(p))
            {
                Cursor = SlipCursor;
                return;
            }
            if (InVideoRow(p))
            {
                Cursor = HandCursor;
                return;
            }
        }
        Cursor = Cursor.Default;
    }

    /// <summary>Snaps a source time to its frame grid (trims land on whole frames).</summary>
    private static TimeSpan SnapSource(TimelineViewModel vm, Clip clip, TimeSpan time) =>
        vm.Info(clip.SourceId)?.Video is { } v ? TimeFormat.SnapToFrame(time, v.FrameRate, v.StartOffset) : time;
}
