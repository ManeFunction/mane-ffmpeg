using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ManeFfmpeg.Core.Model;
using ManeFfmpeg.UI.ViewModels;

namespace ManeFfmpeg.UI.Controls;

/// <summary>
/// Drawn over the video preview while cropping: dims what is cut off and lets the kept rectangle be moved (drag
/// inside), resized (drag an edge or corner) or drawn anew (drag outside it). <see cref="AspectLock"/>, or Shift,
/// keeps its shape. Each finished drag raises <see cref="Committed"/>; <see cref="Crop"/> is only read.
/// </summary>
public sealed class CropOverlay : Control
{
    public static readonly StyledProperty<double> PictureAspectProperty =
        MpvVideoView.PictureAspectProperty.AddOwner<CropOverlay>();

    public static readonly StyledProperty<double> FrameAspectProperty =
        MpvVideoView.FrameAspectProperty.AddOwner<CropOverlay>();

    /// <summary>The crop as committed; null is the whole frame.</summary>
    public static readonly StyledProperty<CropRect?> CropProperty =
        MpvVideoView.CropProperty.AddOwner<CropOverlay>();

    /// <summary>Width over height the crop keeps while dragging; 0 for any shape.</summary>
    public static readonly StyledProperty<double> AspectLockProperty =
        AvaloniaProperty.Register<CropOverlay, double>(nameof(AspectLock));

    /// <summary>The output frame in pixels, for the size shown on the rectangle.</summary>
    public static readonly StyledProperty<PixelSize> FramePixelsProperty =
        AvaloniaProperty.Register<CropOverlay, PixelSize>(nameof(FramePixels));

    private const double HandleSize = 8;

    private static readonly IBrush Shade = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
    private static readonly IPen Border = new Pen(Brushes.White, 1.5);
    private static readonly IPen Thirds = new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1);
    private static readonly IPen HandleOutline = new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 1);
    private static readonly IBrush LabelBackground = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0));

    private readonly CropDragGesture _drag = new();

    static CropOverlay()
    {
        AffectsRender<CropOverlay>(PictureAspectProperty, FrameAspectProperty, CropProperty, FramePixelsProperty);
        FocusableProperty.OverrideDefaultValue<CropOverlay>(false);
    }

    public CropOverlay() => ClipToBounds = true;

    /// <summary>A drag finished: the crop it made, as fractions of the frame.</summary>
    public event Action<CropRect>? Committed;

    public double PictureAspect
    {
        get => GetValue(PictureAspectProperty);
        set => SetValue(PictureAspectProperty, value);
    }

    public double FrameAspect
    {
        get => GetValue(FrameAspectProperty);
        set => SetValue(FrameAspectProperty, value);
    }

    public CropRect? Crop
    {
        get => GetValue(CropProperty);
        set => SetValue(CropProperty, value);
    }

    public double AspectLock
    {
        get => GetValue(AspectLockProperty);
        set => SetValue(AspectLockProperty, value);
    }

    public PixelSize FramePixels
    {
        get => GetValue(FramePixelsProperty);
        set => SetValue(FramePixelsProperty, value);
    }

    /// <summary>The output frame on screen: where the video view shows it while nothing is cropped away.</summary>
    private Rect Frame => PreviewGeometry.Fit(FrameAspect > 0 ? FrameAspect : PictureAspect, new Rect(Bounds.Size));

    private Rect Shown => _drag.Live ?? PreviewGeometry.Inside(Frame, Crop ?? CropRect.Full);

    private bool CanEdit => (FrameAspect > 0 || PictureAspect > 0) && Bounds.Width > 0 && Bounds.Height > 0;

    // ---- Input (the drag itself is CropDragGesture) ----------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!CanEdit || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _drag.Press(e.GetPosition(this), Frame, Shown, AspectLock);
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        if (!_drag.IsDragging)
        {
            Cursor = CanEdit ? CursorFor(CropDragGesture.HitEdges(Shown, point), Shown.Contains(point)) : null;
            return;
        }
        _drag.Move(point, (e.KeyModifiers & KeyModifiers.Shift) != 0);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_drag.IsDragging)
            return;
        var crop = _drag.Release(e.GetPosition(this));
        e.Pointer.Capture(null);
        e.Handled = true;
        InvalidateVisual();
        if (crop is not null)
            Committed?.Invoke(crop);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _drag.Cancel();
        InvalidateVisual();
    }

    private static readonly Cursor Move = new(StandardCursorType.SizeAll);
    private static readonly Cursor Cross = new(StandardCursorType.Cross);
    private static readonly Cursor LeftRight = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor UpDown = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor TopLeft = new(StandardCursorType.TopLeftCorner);
    private static readonly Cursor TopRight = new(StandardCursorType.TopRightCorner);
    private static readonly Cursor BottomLeft = new(StandardCursorType.BottomLeftCorner);
    private static readonly Cursor BottomRight = new(StandardCursorType.BottomRightCorner);

    private static Cursor CursorFor(CropEdges edges, bool inside) => edges switch
    {
        CropEdges.Left | CropEdges.Top => TopLeft,
        CropEdges.Right | CropEdges.Top => TopRight,
        CropEdges.Left | CropEdges.Bottom => BottomLeft,
        CropEdges.Right | CropEdges.Bottom => BottomRight,
        CropEdges.Left or CropEdges.Right => LeftRight,
        CropEdges.Top or CropEdges.Bottom => UpDown,
        _ => inside ? Move : Cross,
    };

    // ---- Drawing ----------------------------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        if (!CanEdit)
            return;
        var bounds = new Rect(Bounds.Size);
        var rect = Shown;

        // Avalonia hit-tests only what was drawn: without this the inside of the rectangle (to move it, or grab an
        // edge from within) would pass the pointer through to the video view underneath.
        context.FillRectangle(Brushes.Transparent, bounds);

        // Everything outside the rectangle is dimmed: four bands around it.
        context.FillRectangle(Shade, new Rect(bounds.X, bounds.Y, bounds.Width, Math.Max(0, rect.Y - bounds.Y)));
        context.FillRectangle(Shade, new Rect(bounds.X, rect.Bottom, bounds.Width, Math.Max(0, bounds.Bottom - rect.Bottom)));
        context.FillRectangle(Shade, new Rect(bounds.X, rect.Y, Math.Max(0, rect.X - bounds.X), rect.Height));
        context.FillRectangle(Shade, new Rect(rect.Right, rect.Y, Math.Max(0, bounds.Right - rect.Right), rect.Height));

        for (var i = 1; i <= 2; i++)
        {
            var x = rect.X + rect.Width * i / 3;
            var y = rect.Y + rect.Height * i / 3;
            context.DrawLine(Thirds, new Point(x, rect.Y), new Point(x, rect.Bottom));
            context.DrawLine(Thirds, new Point(rect.X, y), new Point(rect.Right, y));
        }
        context.DrawRectangle(null, Border, rect);

        foreach (var x in new[] { rect.X, rect.Center.X, rect.Right })
        foreach (var y in new[] { rect.Y, rect.Center.Y, rect.Bottom })
        {
            if (x == rect.Center.X && y == rect.Center.Y)
                continue;
            context.DrawRectangle(Brushes.White, HandleOutline,
                new Rect(x - HandleSize / 2, y - HandleSize / 2, HandleSize, HandleSize));
        }

        DrawSize(context, rect);
    }

    /// <summary>The crop's size in output pixels, under the top edge inside the rectangle.</summary>
    private void DrawSize(DrawingContext context, Rect rect)
    {
        var frame = FramePixels;
        if (frame.Width <= 0 || frame.Height <= 0 || rect.Width < 70 || rect.Height < 40)
            return;
        var pixels = PreviewGeometry.ToCrop(Frame, rect).Clamped()?.ToPixels(frame.Width, frame.Height);
        if (pixels is not { } p)
            return;
        var text = new FormattedText(string.Create(CultureInfo.InvariantCulture, $"{p.Width} × {p.Height}"), CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, Typeface.Default, 12, Brushes.White);
        var box = new Rect(rect.Center.X - text.Width / 2 - 6, rect.Y + 10, text.Width + 12, text.Height + 4);
        context.DrawRectangle(LabelBackground, null, box, 4, 4);
        context.DrawText(text, new Point(box.X + 6, box.Y + 2));
    }
}
