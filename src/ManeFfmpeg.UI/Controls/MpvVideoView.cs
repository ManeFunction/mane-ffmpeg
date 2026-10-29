using System.Globalization;
using Vector = Avalonia.Vector;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ManeFfmpeg.Core.Mpv;

namespace ManeFfmpeg.UI.Controls;

/// <summary>
/// Shows an <see cref="MpvPlayer"/> using mpv's software renderer. A render thread draws each new frame
/// into a buffer of its own sized to the control's pixels (mpv scales and letterboxes), then swaps it with
/// the latest finished frame, which the UI thread copies into a <see cref="WriteableBitmap"/>. mpv draws
/// outside the lock the UI thread takes, so a slow frame never holds up clicks.
/// </summary>
public sealed class MpvVideoView : Control
{
    public static readonly StyledProperty<MpvPlayer?> PlayerProperty =
        AvaloniaProperty.Register<MpvVideoView, MpvPlayer?>(nameof(Player));

    public static readonly StyledProperty<string?> MessageProperty =
        AvaloniaProperty.Register<MpvVideoView, string?>(nameof(Message));

    private const int MaxPixels = 1920 * 1080;

    // Held by the render thread for a whole frame; guards _renderer and _drawing.
    private readonly object _renderLock = new();
    // Held briefly to swap or copy _latest.
    private readonly object _frameLock = new();
    private readonly AutoResetEvent _wake = new(false);
    private MpvSoftwareRenderer? _renderer;
    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _resized;
    private volatile int _targetWidth;
    private volatile int _targetHeight;
    private FrameBuffer _drawing = new();
    private FrameBuffer _latest = new();
    private int _presentPending;
    // Two bitmaps used alternately: redrawing the same instance can keep showing its first upload.
    private readonly WriteableBitmap?[] _bitmaps = new WriteableBitmap?[2];
    private int _front;
    private WriteableBitmap? Front => _bitmaps[_front];

#if DEBUG
    /// <summary>Diagnostics for the automation script: wake-ups, frames mpv rendered, frames shown.</summary>
    public (int Wakes, int Rendered, int Presented) Counters => (_wakes, _rendered, _presented);

    /// <summary>Total milliseconds spent in mpv's render call, waiting for the frame lock in Present, and copying.</summary>
    public (double Render, double PresentWait, double Copy) Timings =>
        (_renderTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency, _waitTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency,
            _copyTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency);

#pragma warning disable CS0618 // debug snapshot only
    public void SaveFrame(string path) => Front?.Save(path);
#pragma warning restore CS0618

    public unsafe string CenterPixel()
    {
        lock (_frameLock)
        {
            if (!_latest.Ready)
                return "none";
            var (w, h) = (_latest.Width, _latest.Height);
            var p = (byte*)_latest.Pixels + (h / 2 * w + w / 2) * 4;
            return $"{w}x{h} bgra=({p[0]},{p[1]},{p[2]},{p[3]})";
        }
    }
#endif

#if DEBUG
    private int _wakes;
    private int _rendered;
    private int _presented;
    private long _renderTicks;
    private long _waitTicks;
    private long _copyTicks;
#endif

    static MpvVideoView()
    {
        AffectsRender<MpvVideoView>(MessageProperty);
    }

    public MpvPlayer? Player
    {
        get => GetValue(PlayerProperty);
        set => SetValue(PlayerProperty, value);
    }

    public string? Message
    {
        get => GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PlayerProperty)
        {
            Detach();
            if (change.NewValue is MpvPlayer player)
                Attach(player);
            InvalidateVisual();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Detach();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Player is { } player && _renderer is null)
            Attach(player);
    }

    private void Attach(MpvPlayer player)
    {
        try
        {
            _renderer = new MpvSoftwareRenderer(player);
        }
        catch (MpvException ex)
        {
            Message = ex.Message;
            return;
        }
        _stop = false;
        _renderer.Invalidated += () => _wake.Set();
        _thread = new Thread(RenderLoop) { IsBackground = true, Name = "mpv render" };
        _thread.Start();
        UpdateTargetSize();
    }

    /// <summary>
    /// Stops rendering and frees the render context. Happens when the player is unbound, which its owner
    /// does before destroying it.
    /// </summary>
    private void Detach()
    {
        if (_renderer is null)
            return;
        _stop = true;
        _wake.Set();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        lock (_renderLock)
        {
            _renderer.Dispose();
            _renderer = null;
            lock (_frameLock)
            {
                _drawing.Free();
                _latest.Free();
            }
        }
        for (var i = 0; i < _bitmaps.Length; i++)
        {
            _bitmaps[i]?.Dispose();
            _bitmaps[i] = null;
        }
    }

    private void RenderLoop()
    {
        while (!_stop)
        {
            _wake.WaitOne();
            if (_stop)
                break;
#if DEBUG
            Interlocked.Increment(ref _wakes);
#endif
            lock (_renderLock)
            {
                if (_renderer is not { } renderer)
                    break;
                var resized = _resized;
                _resized = false;
                var newFrame = renderer.Update();
                if (!newFrame && !resized)
                    continue;
                int width = _targetWidth, height = _targetHeight;
                if (width <= 0 || height <= 0)
                    continue;
                _drawing.Resize(width, height);
#if DEBUG
                var renderStart = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
                if (!renderer.Render(width, height, width * 4, _drawing.Pixels))
                    continue;
#if DEBUG
                Interlocked.Add(ref _renderTicks, System.Diagnostics.Stopwatch.GetTimestamp() - renderStart);
                Interlocked.Increment(ref _rendered);
#endif
                lock (_frameLock)
                {
                    (_latest, _drawing) = (_drawing, _latest);
                    _latest.Ready = true;
                }
            }
            if (Interlocked.Exchange(ref _presentPending, 1) == 0)
                Dispatcher.UIThread.Post(Present, DispatcherPriority.Render);
        }
    }

    private unsafe void Present()
    {
        Interlocked.Exchange(ref _presentPending, 0);
#if DEBUG
        var waitStart = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        lock (_frameLock)
        {
#if DEBUG
            var copyStart = System.Diagnostics.Stopwatch.GetTimestamp();
            _waitTicks += copyStart - waitStart;
#endif
            if (!_latest.Ready)
                return;
            var size = new PixelSize(_latest.Width, _latest.Height);
            var back = 1 - _front;
            if (_bitmaps[back] is not { } bitmap || bitmap.PixelSize != size)
            {
                _bitmaps[back]?.Dispose();
                bitmap = _bitmaps[back] = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            }
            using (var target = bitmap.Lock())
            {
                var rowBytes = _latest.Width * 4;
                if (target.RowBytes == rowBytes)
                    Buffer.MemoryCopy((void*)_latest.Pixels, (void*)target.Address, (long)rowBytes * size.Height, (long)rowBytes * size.Height);
                else
                    for (var y = 0; y < size.Height; y++)
                        Buffer.MemoryCopy((byte*)_latest.Pixels + y * rowBytes, (byte*)target.Address + y * target.RowBytes, rowBytes, rowBytes);
            }
            _front = back;
#if DEBUG
            _copyTicks += System.Diagnostics.Stopwatch.GetTimestamp() - copyStart;
            _presented++;
#endif
        }
        InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateTargetSize();
    }

    private void UpdateTargetSize()
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var width = Bounds.Width * scaling;
        var height = Bounds.Height * scaling;
        if (width < 2 || height < 2)
            return;
        var area = width * height;
        if (area > MaxPixels)
        {
            var factor = Math.Sqrt(MaxPixels / area);
            width *= factor;
            height *= factor;
        }
        _targetWidth = (int)width / 2 * 2;
        _targetHeight = (int)height / 2 * 2;
        _resized = true;
        _wake.Set();
    }

    /// <summary>A native BGRA frame.</summary>
    private sealed class FrameBuffer
    {
        public nint Pixels { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }

        /// <summary>Holds a whole rendered frame (false until the first one and after a resize).</summary>
        public bool Ready { get; set; }

        public void Resize(int width, int height)
        {
            if (Pixels != 0 && width == Width && height == Height)
                return;
            Free();
            Pixels = Marshal.AllocHGlobal(width * height * 4);
            (Width, Height) = (width, height);
        }

        public void Free()
        {
            if (Pixels != 0)
                Marshal.FreeHGlobal(Pixels);
            Pixels = 0;
            Ready = false;
        }
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.Black, bounds);
        if (Front is { } frame && Player is not null && Message is null)
            context.DrawImage(frame, new Rect(frame.Size), bounds);
        if (Message is { Length: > 0 } message)
        {
            var text = new FormattedText(message, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Inter"), 14, new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)))
            {
                MaxTextWidth = Math.Max(100, bounds.Width - 40),
                TextAlignment = TextAlignment.Center,
            };
            context.DrawText(text, new Point((bounds.Width - text.MaxTextWidth) / 2, (bounds.Height - text.Height) / 2));
        }
    }
}
