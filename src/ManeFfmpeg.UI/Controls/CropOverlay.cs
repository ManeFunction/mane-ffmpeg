using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ManeFfmpeg.Core.Model;

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

    private const double Grab = 9;
    private const double HandleSize = 8;

    private static readonly IBrush Shade = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
    private static readonly IPen Border = new Pen(Brushes.White, 1.5);
    private static readonly IPen Thirds = new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1);
    private static readonly IPen HandleOutline = new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 1);
    private static readonly IBrush LabelBackground = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0));

    [Flags]
    private enum Edges
    {
        None = 0,
        Left = 1,
        Top = 2,
        Right = 4,
        Bottom = 8,
    }

    private enum DragKind
    {
        None,
        Move,
        Resize,
    }

    private DragKind _drag;
    private Edges _edges;
    private Point _pressPoint;
    private Rect _pressRect;
    private double _pressAspect;
    // The rectangle while it is dragged, in control coordinates; null shows the committed crop.
    private Rect? _live;

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

    private Rect Shown => _live ?? PreviewGeometry.Inside(Frame, Crop ?? CropRect.Full);

    private bool CanEdit => (FrameAspect > 0 || PictureAspect > 0) && Bounds.Width > 0 && Bounds.Height > 0;

    // ---- Input ------------------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!CanEdit || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var point = e.GetPosition(this);
        var rect = Shown;
        _pressPoint = point;
        _pressRect = rect;
        _pressAspect = rect.Height > 0 ? rect.Width / rect.Height : 1;
        _edges = HitEdges(rect, point);
        if (_edges != Edges.None)
            _drag = DragKind.Resize;
        else if (rect.Contains(point))
            _drag = DragKind.Move;
        else
        {
            // Outside the rectangle: draw a new one from here, as if dragging the corner of an empty one.
            var start = Clamp(point, Frame);
            _pressRect = new Rect(start, start);
            _edges = Edges.Right | Edges.Bottom;
            _drag = DragKind.Resize;
            _pressAspect = AspectLock > 0 ? AspectLock : rect.Height > 0 ? rect.Width / rect.Height : 1;
        }
        _live = rect;
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        if (_drag == DragKind.None)
        {
            Cursor = CanEdit ? CursorFor(HitEdges(Shown, point), Shown.Contains(point)) : null;
            return;
        }
        var keepShape = AspectLock > 0 || (e.KeyModifiers & KeyModifiers.Shift) != 0;
        var aspect = AspectLock > 0 ? AspectLock : _pressAspect;
        _live = _drag == DragKind.Move ? Moved(point) : Resized(point, keepShape ? aspect : 0);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == DragKind.None)
            return;
        var moved = Math.Abs(e.GetPosition(this).X - _pressPoint.X) + Math.Abs(e.GetPosition(this).Y - _pressPoint.Y) > 1;
        var result = _live;
        _drag = DragKind.None;
        _live = null;
        e.Pointer.Capture(null);
        e.Handled = true;
        InvalidateVisual();
        // A click without a drag changes nothing (and a click outside doesn't collapse the crop to a dot).
        if (moved && result is { } rect)
            Committed?.Invoke(PreviewGeometry.ToCrop(Frame, rect));
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _drag = DragKind.None;
        _live = null;
        InvalidateVisual();
    }

    private Rect Moved(Point point)
    {
        var frame = Frame;
        var x = Within(_pressRect.X + point.X - _pressPoint.X, frame.X, frame.Right - _pressRect.Width);
        var y = Within(_pressRect.Y + point.Y - _pressPoint.Y, frame.Y, frame.Bottom - _pressRect.Height);
        return new Rect(x, y, _pressRect.Width, _pressRect.Height);
    }

    /// <summary>
    /// The pressed rectangle with the dragged edges moved to <paramref name="point"/> and the others held, kept inside
    /// the frame and at least the minimum size; with an <paramref name="aspect"/>, of that shape.
    /// </summary>
    private Rect Resized(Point point, double aspect)
    {
        var frame = Frame;
        var p = Clamp(point, frame);
        var r = _pressRect;
        var minW = CropRect.MinSize * frame.Width;
        var minH = CropRect.MinSize * frame.Height;
        var horizontal = (_edges & (Edges.Left | Edges.Right)) != 0;
        var vertical = (_edges & (Edges.Top | Edges.Bottom)) != 0;

        // The fixed side on each dragged axis, and which way the dragged side lies from it.
        var anchorX = (_edges & Edges.Left) != 0 ? r.Right : r.X;
        var anchorY = (_edges & Edges.Top) != 0 ? r.Bottom : r.Y;
        var dirX = horizontal ? (p.X >= anchorX ? 1 : -1) : 0;
        var dirY = vertical ? (p.Y >= anchorY ? 1 : -1) : 0;
        var width = horizontal ? Math.Abs(p.X - anchorX) : r.Width;
        var height = vertical ? Math.Abs(p.Y - anchorY) : r.Height;

        // Room on each axis: from the anchor to the frame edge it grows towards, or around the centre it keeps.
        var roomX = dirX > 0 ? frame.Right - anchorX : dirX < 0 ? anchorX - frame.X : 2 * Math.Min(r.Center.X - frame.X, frame.Right - r.Center.X);
        var roomY = dirY > 0 ? frame.Bottom - anchorY : dirY < 0 ? anchorY - frame.Y : 2 * Math.Min(r.Center.Y - frame.Y, frame.Bottom - r.Center.Y);

        if (aspect > 0)
        {
            // Follow whichever dragged side went furthest; an edge drag sizes the other axis from it.
            if (horizontal && vertical)
            {
                if (width < height * aspect)
                    width = height * aspect;
                else
                    height = width / aspect;
            }
            else if (horizontal)
                height = width / aspect;
            else
                width = height * aspect;
            var fit = Math.Min(1, Math.Min(roomX / Math.Max(width, 1e-9), roomY / Math.Max(height, 1e-9)));
            width *= fit;
            height *= fit;
            var grow = Math.Max(1, Math.Max(minW / Math.Max(width, 1e-9), minH / Math.Max(height, 1e-9)));
            width *= grow;
            height *= grow;
        }
        width = Within(width, Math.Min(minW, roomX), Math.Max(roomX, minW));
        height = Within(height, Math.Min(minH, roomY), Math.Max(roomY, minH));

        var x = dirX > 0 ? anchorX : dirX < 0 ? anchorX - width : r.Center.X - width / 2;
        var y = dirY > 0 ? anchorY : dirY < 0 ? anchorY - height : r.Center.Y - height / 2;
        x = Within(x, frame.X, frame.Right - width);
        y = Within(y, frame.Y, frame.Bottom - height);
        return new Rect(x, y, width, height);
    }

    /// <summary>
    /// Like Math.Clamp, but a <paramref name="max"/> below <paramref name="min"/> (a rectangle as big as the frame, give
    /// or take rounding) gives <paramref name="min"/> instead of throwing.
    /// </summary>
    private static double Within(double value, double min, double max) => Math.Max(min, Math.Min(value, max));

    private static Point Clamp(Point point, Rect area) =>
        new(Math.Clamp(point.X, area.X, area.Right), Math.Clamp(point.Y, area.Y, area.Bottom));

    /// <summary>Which edges a point grabs: near a corner both, near an edge one, else none.</summary>
    private static Edges HitEdges(Rect rect, Point p)
    {
        var inX = p.X >= rect.X - Grab && p.X <= rect.Right + Grab;
        var inY = p.Y >= rect.Y - Grab && p.Y <= rect.Bottom + Grab;
        if (!inX || !inY)
            return Edges.None;
        // A small rectangle still leaves room in the middle to move it.
        var grabX = Math.Min(Grab, rect.Width / 4);
        var grabY = Math.Min(Grab, rect.Height / 4);
        var edges = Edges.None;
        if (Math.Abs(p.X - rect.X) <= grabX || (p.X < rect.X && p.X >= rect.X - Grab))
            edges |= Edges.Left;
        else if (Math.Abs(p.X - rect.Right) <= grabX || (p.X > rect.Right && p.X <= rect.Right + Grab))
            edges |= Edges.Right;
        if (Math.Abs(p.Y - rect.Y) <= grabY || (p.Y < rect.Y && p.Y >= rect.Y - Grab))
            edges |= Edges.Top;
        else if (Math.Abs(p.Y - rect.Bottom) <= grabY || (p.Y > rect.Bottom && p.Y <= rect.Bottom + Grab))
            edges |= Edges.Bottom;
        return edges;
    }

    private static readonly Cursor Move = new(StandardCursorType.SizeAll);
    private static readonly Cursor Cross = new(StandardCursorType.Cross);
    private static readonly Cursor LeftRight = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor UpDown = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor TopLeft = new(StandardCursorType.TopLeftCorner);
    private static readonly Cursor TopRight = new(StandardCursorType.TopRightCorner);
    private static readonly Cursor BottomLeft = new(StandardCursorType.BottomLeftCorner);
    private static readonly Cursor BottomRight = new(StandardCursorType.BottomRightCorner);

    private static Cursor CursorFor(Edges edges, bool inside) => edges switch
    {
        Edges.Left | Edges.Top => TopLeft,
        Edges.Right | Edges.Top => TopRight,
        Edges.Left | Edges.Bottom => BottomLeft,
        Edges.Right | Edges.Bottom => BottomRight,
        Edges.Left or Edges.Right => LeftRight,
        Edges.Top or Edges.Bottom => UpDown,
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
