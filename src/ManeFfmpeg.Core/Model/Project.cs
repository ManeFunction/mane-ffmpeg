namespace ManeFfmpeg.Core.Model;

/// <summary>Everything stored in a .mfvp file.</summary>
public sealed record Project
{
    public ProjectState State { get; init; } = ProjectState.Empty;
    public ExportSettings Export { get; init; } = new();
    public ProjectLayout Layout { get; init; } = new();
}

public sealed record ExportSettings
{
    public string? PresetId { get; init; }

    /// <summary>Copy of the preset at save time, so the project still exports on a machine without it.</summary>
    public Preset? PresetSnapshot { get; init; }

    public string? OutputPath { get; init; }
}

/// <summary>Window and editor layout restored with the project.</summary>
public sealed record ProjectLayout
{
    public WindowBounds? Window { get; init; }
    public double LeftPaneWidth { get; init; } = 300;
    public double TimelineHeight { get; init; } = 250;

    /// <summary>Timeline zoom in pixels per second; 0 means "fit".</summary>
    public double TimelineZoom { get; init; }
    public double TimelineScroll { get; init; }
    public TimeSpan Playhead { get; init; }
    public double Volume { get; init; } = 100;

    /// <summary>Compact mode: video preview hidden. <see cref="Window"/> keeps the full-size bounds.</summary>
    public bool Compact { get; init; }
}

public sealed record WindowBounds(int X, int Y, double Width, double Height, bool Maximized);
