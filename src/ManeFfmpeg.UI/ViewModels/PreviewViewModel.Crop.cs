using System.Globalization;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.UI.ViewModels;

/// <summary>A shape the crop can be held to: <see cref="Aspect"/> is width over height, 0 any shape, −1 the frame's own.</summary>
public sealed record CropAspect(string Name, double Aspect)
{
    public override string ToString() => Name;
}

/// <summary>Cropping the output on the preview: the crop mode, the shapes it can keep and the frame it applies to.</summary>
public sealed partial class PreviewViewModel
{
    public static IReadOnlyList<CropAspect> CropAspects { get; } =
    [
        new("Free", 0),
        new("Original", -1),
        new("16:9", 16.0 / 9),
        new("9:16", 9.0 / 16),
        new("4:3", 4.0 / 3),
        new("4:5", 4.0 / 5),
        new("1:1", 1),
    ];

    /// <summary>The crop rectangle is shown on the preview to be dragged; the whole frame is shown meanwhile.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownCrop), nameof(PreviewTitle))]
    public partial bool IsCropping { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AspectLock))]
    public partial CropAspect SelectedCropAspect { get; set; } = CropAspects[0];

    /// <summary>Width over height, as shown, of the picture on the preview now (0 while unknown).</summary>
    [ObservableProperty]
    public partial double PictureAspect { get; private set; }

    /// <summary>Width over height, as shown, of the output frame: the first clip's, which the others are fitted to.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AspectLock), nameof(CanCrop))]
    public partial double FrameAspect { get; private set; }

    /// <summary>The output frame in pixels.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewTitle))]
    public partial PixelSize FramePixels { get; private set; }

    /// <summary>The committed crop; null keeps the whole frame.</summary>
    public CropRect? Crop => State.Crop;

    /// <summary>What the preview shows of the frame: the crop on the timeline, except while it is being edited.</summary>
    public CropRect? ShownCrop => Mode == PreviewMode.Timeline && !IsCropping ? State.Crop : null;

    /// <summary>The shape a drag keeps (width over height), or 0 for any.</summary>
    public double AspectLock => SelectedCropAspect.Aspect < 0 ? FrameAspect : SelectedCropAspect.Aspect;

    /// <summary>There is a timeline picture to crop.</summary>
    public bool CanCrop => Mode == PreviewMode.Timeline && _owner.HasClips && !IsCompact && FrameAspect > 0;

    public bool HasCrop => State.Crop is not null;

    /// <summary>The crop in output pixels, as "1280 × 720"; null without one.</summary>
    public string? CropSizeText => State.Crop is { } crop && FramePixels is { Width: > 0, Height: > 0 } frame
        ? crop.ToPixels(frame.Width, frame.Height) is var p ? string.Create(CultureInfo.InvariantCulture, $"{p.Width} × {p.Height}") : null
        : null;

    partial void OnSelectedCropAspectChanged(CropAspect value)
    {
        // Picking a shape reshapes the crop at once; "Free" leaves it as it is.
        if (AspectLock > 0 && FrameAspect > 0)
            CommitCrop((State.Crop ?? CropRect.Full).WithAspect(AspectLock, FrameAspect));
    }

    [RelayCommand]
    private void ToggleCrop()
    {
        if (IsCropping)
            IsCropping = false;
        else if (CanCrop)
            IsCropping = true;
    }

    [RelayCommand]
    private void FinishCrop() => IsCropping = false;

    [RelayCommand]
    private void ResetCrop()
    {
        SelectedCropAspect = CropAspects[0];
        CommitCrop(null);
    }

    /// <summary>Crops the output to <paramref name="crop"/> as one undo step (null or the whole frame removes the crop).</summary>
    public void CommitCrop(CropRect? crop)
    {
        _owner.Commit(State.WithCrop(crop));
        if (State.Crop is null)
            _owner.StatusMessage = "Crop removed: the whole frame is exported.";
        else if (CropSizeText is { } size)
            _owner.StatusMessage = $"Exports crop to {size}.";
    }

    /// <summary>
    /// Works out the frame and the picture shown: called when the edit, the analysis, the playhead or the preview
    /// mode changes. Leaves crop mode when there's nothing to crop any more.
    /// </summary>
    private void UpdateFrame()
    {
        var frame = Mode == PreviewMode.Source
            ? PreviewedSource?.Info?.Video
            : State.Timeline.Clips is { Count: > 0 } clips ? Catalog.Info(clips[0].SourceId)?.Video : null;
        FrameAspect = frame?.DisplayAspect ?? 0;
        FramePixels = frame is null ? default : new PixelSize(frame.DisplayWidth, frame.DisplayHeight);
        UpdatePicture();
        OnCropChanged();
        if (IsCropping && !CanCrop)
            IsCropping = false;
    }

    /// <summary>The shape of the picture shown now: the previewed video's, or that of the clip under the playhead.</summary>
    private void UpdatePicture()
    {
        var picture = Mode == PreviewMode.Source
            ? PreviewedSource?.Info?.Video
            : State.Timeline.Locate(Timeline.Playhead) is { } at ? Catalog.Info(State.Timeline.Clips[at.Index].SourceId)?.Video : null;
        PictureAspect = picture?.DisplayAspect ?? FrameAspect;
    }

    private void OnCropChanged()
    {
        OnPropertyChanged(nameof(Crop));
        OnPropertyChanged(nameof(ShownCrop));
        OnPropertyChanged(nameof(HasCrop));
        OnPropertyChanged(nameof(CropSizeText));
        OnPropertyChanged(nameof(CanCrop));
        OnPropertyChanged(nameof(PreviewTitle));
    }
}
