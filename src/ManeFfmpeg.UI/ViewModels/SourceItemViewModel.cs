using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using ManeFfmpeg.Core.Media;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.UI.ViewModels;

/// <summary>A row in the Files panel: a video on the Video tab, or an audio file on the Audio tab.</summary>
public sealed partial class SourceItemViewModel(SourceMedia source, int colorIndex, FileKind kind) : ViewModelBase
{
    public static readonly Color[] Palette =
    [
        Color.Parse("#4F8EF7"), Color.Parse("#43B581"), Color.Parse("#F5A623"), Color.Parse("#B46EF5"),
        Color.Parse("#F25F5C"), Color.Parse("#2EC4B6"), Color.Parse("#E84E9D"), Color.Parse("#9CCC65"),
    ];

    /// <summary>Audio files' colors: softer, and starting on a hue the first video doesn't use.</summary>
    public static readonly Color[] AudioPalette =
    [
        Color.Parse("#D9C2FF"), Color.Parse("#FFD6A0"), Color.Parse("#A8E6C1"), Color.Parse("#FFB3B0"),
        Color.Parse("#A9C7FF"), Color.Parse("#F9B8DA"), Color.Parse("#9FE5DD"), Color.Parse("#DDEBA6"),
    ];

    public static Color AudioColor(int colorIndex) => AudioPalette[colorIndex % AudioPalette.Length];

    /// <summary>A video (Video tab) or an imported audio file (Audio tab).</summary>
    public FileKind Kind { get; } = kind;

    public SourceMedia Source { get; private set; } = source;

    public string Id => Source.Id;

    public string Name => Source.DisplayName;

    public string Path => Source.Path;

    public int ColorIndex { get; } = colorIndex;

    public Color Color => Kind == FileKind.Audio ? AudioColor(ColorIndex) : Palette[ColorIndex % Palette.Length];

    public IBrush Brush => new SolidColorBrush(Color);

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
            if (Kind == FileKind.Audio)
            {
                parts.Add(info.Audio(Source.AudioStreamIndex) is { } stream ? stream.Describe() : "no audio");
                return string.Join(" · ", parts);
            }
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
        // An audio file's details show its chosen stream.
        if (Kind == FileKind.Audio)
            OnPropertyChanged(nameof(Details));
    }
}
