using System.Globalization;
using System.Numerics;
using Vector = Avalonia.Vector;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ManeFfmpeg.Core.Mpv;

namespace ManeFfmpeg.App.Controls;

/// <summary>
/// Shows an <see cref="MpvPlayer"/> using mpv's software renderer. A render thread draws each new frame
/// into a buffer sized to the control's pixels (mpv scales and letterboxes), and the UI thread copies it
/// into a <see cref="WriteableBitmap"/>.
/// </summary>
public sealed class MpvVideoView : Control
{
    public static readonly StyledProperty<MpvPlayer?> PlayerProperty =
        AvaloniaProperty.Register<MpvVideoView, MpvPlayer?>(nameof(Player));

    public static readonly StyledProperty<string?> MessageProperty =
        AvaloniaProperty.Register<MpvVideoView, string?>(nameof(Message));

    private const int MaxPixels = 1920 * 1080;

    private readonly object _bufferLock = new();
    private readonly AutoResetEvent _wake = new(false);
    private MpvSoftwareRenderer? _renderer;
    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _resized;
    private volatile int _targetWidth;
    private volatile int _targetHeight;
    private nint _buffer;
    private int _bufferWidth;
    private int _bufferHeight;
    private bool _bufferReady;
    private int _presentPending;
    // Two bitmaps used alternately: redrawing the same instance can keep showing its first upload.
    private readonly WriteableBitmap?[] _bitmaps = new WriteableBitmap?[2];
    private int _front;
    private WriteableBitmap? Front => _bitmaps[_front];

#if DEBUG
    /// <summary>Diagnostics for the automation script: wake-ups, frames mpv rendered, frames shown.</summary>
    public (int Wakes, int Rendered, int Presented) Counters => (_wakes, _rendered, _presented);

#pragma warning disable CS0618 // debug snapshot only
    public void SaveFrame(string path) => Front?.Save(path);
#pragma warning restore CS0618

    public unsafe string CenterPixel()
    {
        lock (_bufferLock)
        {
            if (_buffer == 0)
                return "none";
            var p = (byte*)_buffer + (_bufferHeight / 2 * _bufferWidth + _bufferWidth / 2) * 4;
            return $"{_bufferWidth}x{_bufferHeight} bgrx=({p[0]},{p[1]},{p[2]},{p[3]})";
        }
    }
#endif

    private int _wakes;
    private int _rendered;
    private int _presented;

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
        lock (_bufferLock)
        {
            _renderer.Dispose();
            _renderer = null;
            if (_buffer != 0)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = 0;
            }
            _bufferReady = false;
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
            Interlocked.Increment(ref _wakes);
            var renderer = _renderer;
            if (renderer is null)
                continue;
            var resized = _resized;
            _resized = false;
            var newFrame = renderer.Update();
            if (!newFrame && !resized)
                continue;
            int width = _targetWidth, height = _targetHeight;
            if (width <= 0 || height <= 0)
                continue;
            lock (_bufferLock)
            {
                if (_renderer is null)
                    break;
                if (_buffer == 0 || width != _bufferWidth || height != _bufferHeight)
                {
                    if (_buffer != 0)
                        Marshal.FreeHGlobal(_buffer);
                    _buffer = Marshal.AllocHGlobal(width * height * 4);
                    _bufferWidth = width;
                    _bufferHeight = height;
                }
                _bufferReady = renderer.Render(width, height, width * 4, _buffer);
                Interlocked.Increment(ref _rendered);
            }
            if (Interlocked.Exchange(ref _presentPending, 1) == 0)
                Dispatcher.UIThread.Post(Present, DispatcherPriority.Render);
        }
    }

    private unsafe void Present()
    {
        Interlocked.Exchange(ref _presentPending, 0);
        lock (_bufferLock)
        {
            if (!_bufferReady || _buffer == 0)
                return;
            var size = new PixelSize(_bufferWidth, _bufferHeight);
            var back = 1 - _front;
            if (_bitmaps[back] is not { } bitmap || bitmap.PixelSize != size)
            {
                _bitmaps[back]?.Dispose();
                bitmap = _bitmaps[back] = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            }
            using (var target = bitmap.Lock())
            {
                var rowBytes = _bufferWidth * 4;
                var opaque = new Vector<uint>(0xFF000000);
                for (var y = 0; y < _bufferHeight; y++)
                {
                    // mpv's "bgr0" leaves the 4th byte at 0; make it opaque alpha.
                    var src = new ReadOnlySpan<uint>((byte*)_buffer + y * rowBytes, _bufferWidth);
                    var dst = new Span<uint>((byte*)target.Address + y * target.RowBytes, _bufferWidth);
                    var i = 0;
                    for (; i <= src.Length - Vector<uint>.Count; i += Vector<uint>.Count)
                        (new Vector<uint>(src[i..]) | opaque).CopyTo(dst[i..]);
                    for (; i < src.Length; i++)
                        dst[i] = src[i] | 0xFF000000;
                }
            }
            _front = back;
            _presented++;
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
