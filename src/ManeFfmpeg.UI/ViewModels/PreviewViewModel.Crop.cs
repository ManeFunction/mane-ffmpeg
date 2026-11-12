using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.UI.ViewModels;

/// <summary>A shape the crop can be held to.</summary>
/// <param name="Ratio">Width over height as shown; null for any shape, or for the frame's own.</param>
/// <param name="IsFrame">The frame's own shape, whatever it is.</param>
public sealed record CropAspect(string Name, double? Ratio, bool IsFrame = false)
{
    /// <summary>The width over height it keeps in a frame of <paramref name="frameAspect"/>, or null for any shape.</summary>
    public double? In(double frameAspect) => IsFrame ? frameAspect > 0 ? frameAspect : null : Ratio;

    public override string ToString() => Name;
}

/// <summary>Cropping the output on the preview: the crop mode, the shapes it can keep and the frame it applies to.</summary>
public sealed partial class PreviewViewModel
{
    public static IReadOnlyList<CropAspect> CropAspects { get; } =
    [
        new("Free", null),
        new("Original", null, IsFrame: true),
        new("16:9", 16.0 / 9),
        new("9:16", 9.0 / 16),
        new("4:3", 4.0 / 3),
        new("4:5", 4.0 / 5),
        new("1:1", 1),
    ];

    /// <summary>The crop rectangle is shown on the preview to be dragged; the whole frame is shown meanwhile.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownCrop), nameof(PreviewTitle), nameof(ClosePreviewTip))]
    public partial bool IsCropping { get; private set; }

    // The edit and the shape as crop mode was entered: what cancelling the crop goes back to.
    private ProjectState? _cropStart;
    private CropAspect _cropStartAspect = CropAspects[0];

    // Putting the shape back on cancel mustn't reshape the crop.
    private bool _restoringAspect;

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
    [NotifyPropertyChangedFor(nameof(CropSizeText), nameof(PreviewTitle))]
    public partial PixelSize FramePixels { get; private set; }

    // The crop last announced, so property changes are raised only when it changes.
    private CropRect? _announcedCrop;

    /// <summary>The committed crop; null keeps the whole frame.</summary>
    public CropRect? Crop => State.Crop;

    /// <summary>What the preview shows of the frame: the crop on the timeline, except while it is being edited.</summary>
    public CropRect? ShownCrop => Mode == PreviewMode.Timeline && !IsCropping ? State.Crop : null;

    /// <summary>The shape a drag keeps (width over height), or 0 for any.</summary>
    public double AspectLock => SelectedCropAspect.In(FrameAspect) ?? 0;

    /// <summary>There is a timeline picture to crop.</summary>
    public bool CanCrop => Mode == PreviewMode.Timeline && _owner.HasClips && !IsCompact && FrameAspect > 0;

    public bool HasCrop => State.Crop is not null;

    /// <summary>The crop in output pixels, as "1280 × 720"; null without one.</summary>
    public string? CropSizeText => State.Crop is { } crop && FramePixels is { Width: > 0, Height: > 0 } frame
        ? crop.ToPixels(frame.Width, frame.Height).SizeText
        : null;

    partial void OnSelectedCropAspectChanged(CropAspect value)
    {
        // Picking a shape reshapes the crop at once; "Free" leaves it as it is.
        if (!_restoringAspect && AspectLock > 0 && FrameAspect > 0)
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

    partial void OnIsCroppingChanged(bool value)
    {
        _cropStart = value ? State : null;
        _cropStartAspect = SelectedCropAspect;
    }

    [RelayCommand]
    private void FinishCrop() => IsCropping = false;

    /// <summary>
    /// Leaves crop mode, putting back the crop and shape it was entered with. The crop steps made meanwhile leave the
    /// undo history, the steps before stay; if other edits were made meanwhile, they are kept and only the crop goes back.
    /// </summary>
    [RelayCommand]
    private void CancelCrop()
    {
        if (!IsCropping || _cropStart is not { } start)
            return;
        var aspect = _cropStartAspect;
        var changed = !ReferenceEquals(State, start);
        if (changed && !(State.WithCrop(start.Crop).Equals(start) && _owner.History.RewindTo(start)))
            CommitCrop(start.Crop);
        _restoringAspect = true;
        SelectedCropAspect = aspect;
        _restoringAspect = false;
        IsCropping = false;
        if (changed)
            _owner.StatusMessage = "Crop changes cancelled.";
    }

    /// <summary>The preview's close button: cancels the crop while cropping, otherwise hides the preview.</summary>
    [RelayCommand]
    private void ClosePreview()
    {
        if (IsCropping)
            CancelCrop();
        else
            IsCompact = !IsCompact;
    }

    public string ClosePreviewTip => IsCropping ? "Cancel the crop changes" : "Hide the video preview (⇧⌘C)";

    [RelayCommand(CanExecute = nameof(HasCrop))]
    private void ResetCrop()
    {
        SelectedCropAspect = CropAspects[0];
        CommitCrop(null);
    }

    /// <summary>Crops the output to <paramref name="crop"/> as one undo step (null or the whole frame removes the crop).</summary>
    public void CommitCrop(CropRect? crop)
    {
        var before = State;
        _owner.Commit(State.WithCrop(crop));
        if (ReferenceEquals(State, before))
            return;
        if (State.Crop is null)
            _owner.StatusMessage = "Crop removed: the whole frame is exported.";
        else if (CropSizeText is { } size)
            _owner.StatusMessage = $"Exports crop to {size}.";
    }

    /// <summary>The edit or the analysis of its files changed: the timeline to play, the frame and the crop.</summary>
    private void OnProjectChanged()
    {
        ScheduleTimelineReload();
        // Clips may have come or gone.
        OnPropertyChanged(nameof(CanCrop));
        UpdateFrame();
        if (!Equals(_announcedCrop, State.Crop))
        {
            _announcedCrop = State.Crop;
            OnCropChanged();
        }
    }

    /// <summary>
    /// Works out the frame and the picture shown: called when the edit, the analysis or the preview mode changes.
    /// Leaves crop mode when there's nothing to crop any more.
    /// </summary>
    private void UpdateFrame()
    {
        // The frame the export would make: the same one the export crops, so the sizes shown are the sizes exported.
        IReadOnlyList<VideoStreamInfo?> videos = Mode == PreviewMode.Source
            ? [PreviewedSource?.Info?.Video]
            : [.. State.Timeline.Clips.Select(c => Catalog.Info(c.SourceId)?.Video)];
        var frame = videos is [{ } first, ..] ? OutputFrame.Of([first, .. videos.Skip(1).OfType<VideoStreamInfo>()]) : null;
        FrameAspect = frame?.Aspect ?? 0;
        FramePixels = frame is null ? default : new PixelSize(frame.Width, frame.Height);
        UpdatePicture();
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
        ResetCropCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CropSizeText));
        OnPropertyChanged(nameof(PreviewTitle));
    }
}
