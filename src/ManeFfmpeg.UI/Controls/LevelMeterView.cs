using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using ManeFfmpeg.Core.Mpv;

namespace ManeFfmpeg.UI.Controls;

/// <summary>
/// The level meters left of the timeline: one peak bar per channel of what the preview plays, with a peak-hold tick, a
/// dBFS scale and a red clip marker on top of each bar that stays lit once that channel reached full scale, until the
/// meters are clicked. The control is as wide as its channels need.
/// </summary>
public sealed class LevelMeterView : Control
{
    public static readonly StyledProperty<LevelMeter?> MeterProperty =
        AvaloniaProperty.Register<LevelMeterView, LevelMeter?>(nameof(Meter));

    // The bottom of the scale; anything quieter shows as an empty bar.
    internal const double FloorDb = -60;
    // How fast a bar falls after a peak, and how long the peak tick waits before it follows.
    private const double ReleaseDbPerSecond = 24;
    private static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(1.2);

    private const double Pad = MeterScale.Pad, ScaleWidth = MeterScale.ScaleWidth, ScaleGap = MeterScale.ScaleGap;
    private const double ClipHeight = 4, ClipGap = 3, LabelHeight = 11;
    private static readonly double[] Ticks = [0, -6, -12, -18, -24, -36, -48, -60];
    // The bar changes color at these levels: green below −18 dBFS, yellow up to −6, red above.
    private const double YellowDb = -18, RedDb = -6;

    private static readonly Typeface Font = new("Inter");
    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.Parse("#17181C"));
    private static readonly IBrush GreenBrush = new SolidColorBrush(Color.Parse("#3DDC84"));
    private static readonly IBrush YellowBrush = new SolidColorBrush(Color.Parse("#F5D547"));
    private static readonly IBrush RedBrush = new SolidColorBrush(Color.Parse("#FF5252"));
    private static readonly IBrush ClipOffBrush = new SolidColorBrush(Color.Parse("#4A2A2D"));
    private static readonly IBrush ClipOnBrush = new SolidColorBrush(Color.Parse("#FF3B30"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#80848E"));
    private static readonly IPen TickPen = new Pen(new SolidColorBrush(Color.Parse("#4A4C55")), 1);
    private static readonly IPen HoldPen = new Pen(new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), 1);

    private readonly DispatcherTimer _timer;
    private int _channels = 2;
    private double[] _shown = [];
    private double[] _hold = [];
    private long[] _holdUntil = [];
    private bool[] _clipped = [];
    private long _lastTick;

    static LevelMeterView()
    {
        AffectsMeasure<LevelMeterView>(MeterProperty);
    }

    public LevelMeterView()
    {
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => Tick());
        Resize(_channels);
    }

    public LevelMeter? Meter
    {
        get => GetValue(MeterProperty);
        set => SetValue(MeterProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MeterProperty)
            UpdateTimer();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
    }

    private void UpdateTimer()
    {
        _lastTick = Stopwatch.GetTimestamp();
        if (Meter is not null && VisualRoot is not null)
            _timer.Start();
        else
            _timer.Stop();
    }

    protected override Size MeasureOverride(Size availableSize) => new(MeterScale.WidthFor(_channels), 0);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!_clipped.Contains(true))
            return;
        Array.Fill(_clipped, false);
        InvalidateVisual();
        e.Handled = true;
    }

    private void Resize(int channels)
    {
        _channels = channels;
        _shown = Enumerable.Repeat(double.NegativeInfinity, channels).ToArray();
        _hold = Enumerable.Repeat(double.NegativeInfinity, channels).ToArray();
        _holdUntil = new long[channels];
        _clipped = new bool[channels];
        InvalidateMeasure();
    }

    /// <summary>Takes what was heard since the last tick: bars jump up to a peak and fall back at the release rate.</summary>
    private void Tick()
    {
        if (Meter is not { } meter)
            return;
        var now = Stopwatch.GetTimestamp();
        var seconds = (now - _lastTick) / (double)Stopwatch.Frequency;
        _lastTick = now;
        var reading = meter.Take();
        var channels = reading?.Channels ?? meter.Channels ?? _channels;
        var changed = false;
        if (channels != _channels && channels > 0)
        {
            Resize(channels);
            changed = true;
        }
        for (var i = 0; i < _channels; i++)
        {
            var level = reading is not null && i < reading.Channels ? reading.PeaksDb[i] : double.NegativeInfinity;
            var shown = _shown[i];
            var next = level >= shown ? level : Math.Max(level, shown - ReleaseDbPerSecond * seconds);
            if (next < FloorDb - 10)
                next = double.NegativeInfinity;
            // The tick rises with a new peak and stays there for the hold time; after that it falls with the bar.
            if (next >= _hold[i])
            {
                _holdUntil[i] = now + (long)(HoldTime.TotalSeconds * Stopwatch.Frequency);
                changed |= next != _hold[i];
                _hold[i] = next;
            }
            else if (now >= _holdUntil[i])
            {
                changed = true;
                _hold[i] = next;
            }
            if (next != shown)
            {
                _shown[i] = next;
                changed = true;
            }
            if (reading is not null && i < reading.Channels && reading.Clipped(i) && !_clipped[i])
            {
                _clipped[i] = true;
                changed = true;
            }
        }
        if (changed)
            InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        var top = Pad + 3;
        var barTop = top + ClipHeight + ClipGap;
        var barBottom = bounds.Height - Pad - LabelHeight;
        if (barBottom - barTop < 20)
            return;
        var barHeight = barBottom - barTop;
        double Y(double db) => barBottom - MeterScale.Fraction(db) * barHeight;

        // The scale: right-aligned numbers (dB below full scale) with a tick each.
        var scaleRight = Pad + ScaleWidth;
        var lastLabel = double.NegativeInfinity;
        foreach (var db in Ticks)
        {
            var y = Math.Round(Y(db)) + 0.5;
            if (y - lastLabel < 9)
                continue;
            lastLabel = y;
            context.DrawLine(TickPen, new Point(scaleRight - 2, y), new Point(scaleRight + 1, y));
            var text = Text(db == 0 ? "0" : (-db).ToString(CultureInfo.InvariantCulture));
            context.DrawText(text, new Point(scaleRight - 3 - text.Width, y - text.Height / 2));
        }

        var width = MeterScale.BarWidth(_channels);
        var x = scaleRight + ScaleGap;
        for (var i = 0; i < _channels; i++, x += width + MeterScale.BarGap(_channels))
        {
            context.FillRectangle(_clipped[i] ? ClipOnBrush : ClipOffBrush, new Rect(x, top, width, ClipHeight));
            context.FillRectangle(TrackBrush, new Rect(x, barTop, width, barHeight));
            var level = _shown[i];
            if (level > FloorDb - 10)
            {
                Segment(context, x, width, Y(Math.Min(level, YellowDb)), barBottom, GreenBrush);
                if (level > YellowDb)
                    Segment(context, x, width, Y(Math.Min(level, RedDb)), Y(YellowDb), YellowBrush);
                if (level > RedDb)
                    Segment(context, x, width, Y(level), Y(RedDb), RedBrush);
            }
            if (_hold[i] > FloorDb && _hold[i] > level + 0.5)
            {
                var y = Math.Round(Y(_hold[i])) + 0.5;
                context.DrawLine(HoldPen, new Point(x, y), new Point(x + width, y));
            }
            var name = Text(MeterScale.ChannelName(i, _channels));
            context.DrawText(name, new Point(x + (width - name.Width) / 2, barBottom + 2));
        }
    }

    /// <summary>What the meters show, for the debug automation.</summary>
    internal string Describe() => string.Join(" ", Enumerable.Range(0, _channels).Select(i =>
        $"{MeterScale.ChannelName(i, _channels)}={_shown[i].ToString("0.0", CultureInfo.InvariantCulture)}{(_clipped[i] ? "(clip)" : "")}"));

    private static void Segment(DrawingContext context, double x, double width, double from, double to, IBrush brush)
    {
        if (to > from)
            context.FillRectangle(brush, new Rect(x, from, width, to - from));
    }

    private static FormattedText Text(string text) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 8, TextBrush);
}

/// <summary>The level meters' scale and layout (no Avalonia state, so tests can use it).</summary>
public static class MeterScale
{
    public const double Pad = 5, ScaleWidth = 14, ScaleGap = 3;

    /// <summary>The width the meters take for this many channels.</summary>
    public static double WidthFor(int channels) =>
        Pad + ScaleWidth + ScaleGap + channels * BarWidth(channels) + (channels - 1) * BarGap(channels) + Pad;

    public static double BarWidth(int channels) => channels <= 8 ? 6 : 4;

    public static double BarGap(int channels) => channels <= 8 ? 2 : 1;

    /// <summary>
    /// Where a level sits on the bar, 0 (bottom) to 1 (0 dBFS): the IEC 60268-18 meter scale, which gives the loud end
    /// more room than a linear dB scale would.
    /// </summary>
    public static double Fraction(double db)
    {
        double percent = db switch
        {
            < -70 => 0,
            < -60 => (db + 70) * 0.25,
            < -50 => (db + 60) * 0.5 + 2.5,
            < -40 => (db + 50) * 0.75 + 7.5,
            < -30 => (db + 40) * 1.5 + 15,
            < -20 => (db + 30) * 2 + 30,
            < 0 => (db + 20) * 2.5 + 50,
            _ => 100,
        };
        return percent / 100;
    }

    /// <summary>The channel names under the bars: M for mono, L R for stereo, numbers otherwise.</summary>
    public static string ChannelName(int channel, int channels) => channels switch
    {
        1 => "M",
        2 => channel == 0 ? "L" : "R",
        _ => (channel + 1).ToString(CultureInfo.InvariantCulture),
    };
}
