using Avalonia;
using Avalonia.Controls;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.UI.Controls;

/// <summary>
/// What the preview's controls draw by: the picture's shape, the output frame's and the crop. The video view and the
/// crop overlay each take these over, so neither depends on the other.
/// </summary>
public sealed class PreviewProperties
{
    private PreviewProperties()
    {
    }

    /// <summary>Width over height of the picture as shown (0 while unknown).</summary>
    public static readonly AttachedProperty<double> PictureAspectProperty =
        AvaloniaProperty.RegisterAttached<PreviewProperties, Control, double>("PictureAspect");

    /// <summary>Width over height of the output frame the picture is fitted into (0 = the picture's own).</summary>
    public static readonly AttachedProperty<double> FrameAspectProperty =
        AvaloniaProperty.RegisterAttached<PreviewProperties, Control, double>("FrameAspect");

    /// <summary>The part of the output frame to show or edit; null is all of it.</summary>
    public static readonly AttachedProperty<CropRect?> CropProperty =
        AvaloniaProperty.RegisterAttached<PreviewProperties, Control, CropRect?>("Crop");
}
