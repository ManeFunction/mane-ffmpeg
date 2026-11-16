namespace ManeFfmpeg.UI.Tests;

/// <summary>Skips the test on Windows: it runs sh scripts or uses Unix file modes.</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "macOS and Linux only";
    }
}
