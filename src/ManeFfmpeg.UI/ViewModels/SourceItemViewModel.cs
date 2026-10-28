using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using ManeFfmpeg.Core.Media;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.UI.ViewModels;

/// <summary>A row in the media bin.</summary>
public sealed partial class SourceItemViewModel(SourceMedia source, int colorIndex) : ViewModelBase
{
    public static readonly Color[] Palette =
    [
        Color.Parse("#4F8EF7"), Color.Parse("#43B581"), Color.Parse("#F5A623"), Color.Parse("#B46EF5"),
        Color.Parse("#F25F5C"), Color.Parse("#2EC4B6"), Color.Parse("#E84E9D"), Color.Parse("#9CCC65"),
    ];

    public SourceMedia Source { get; private set; } = source;

    public string Id => Source.Id;

    public string Name => Source.DisplayName;

    public string Path => Source.Path;

    public int ColorIndex { get; } = colorIndex;

    public IBrush Brush => new SolidColorBrush(Palette[ColorIndex % Palette.Length]);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Details), nameof(AudioChoices), nameof(HasAudioChoice), nameof(StatusText), nameof(IsProblem))]
    public partial MediaInfo? Info { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsProblem))]
    public partial SourceStatus Status { get; set; } = SourceStatus.Analyzing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string? Error { get; set; }

    public bool IsProblem => Status is SourceStatus.Missing or SourceStatus.Failed;

    public string StatusText => Status switch
    {
        SourceStatus.Analyzing => "Analyzing…",
        SourceStatus.Missing => "File not found — use Relink",
        SourceStatus.Failed => Error ?? "Can't read this file",
        _ => "",
    };

    public string Details
    {
        get
        {
            if (Info is not { } info)
                return "";
            var parts = new List<string> { TimeFormat.Display(info.Duration) };
            if (info.Video is { } v)
            {
                parts.Add($"{v.DisplayWidth}×{v.DisplayHeight}");
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{v.FrameRate.Value:0.##} fps"));
                parts.Add(v.Codec);
            }
            parts.Add(info.AudioStreams.Length == 0 ? "no audio" : info.AudioStreams[0].Codec);
            return string.Join(" · ", parts);
        }
    }

    public IReadOnlyList<string> AudioChoices =>
        Info?.AudioStreams.Select((a, i) => $"#{i + 1} {a.Describe()}").ToList() ?? [];

    public bool HasAudioChoice => Info is { AudioStreams.Length: > 1 };

    /// <summary>Raised when the user picks another audio stream in the bin.</summary>
    public event Action<SourceItemViewModel, int>? AudioStreamChosen;

    public int AudioStreamIndex
    {
        get => Source.AudioStreamIndex;
        set
        {
            if (value < 0 || value == Source.AudioStreamIndex)
                return;
            AudioStreamChosen?.Invoke(this, value);
        }
    }

    public void Update(SourceMedia source)
    {
        var pathChanged = source.Path != Source.Path;
        Source = source;
        OnPropertyChanged(nameof(AudioStreamIndex));
        if (pathChanged)
        {
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(Path));
        }
    }
}
