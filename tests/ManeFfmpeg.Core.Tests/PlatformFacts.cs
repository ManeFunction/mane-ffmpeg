namespace ManeFfmpeg.Core.Tests;

/// <summary>Skips the test outside macOS.</summary>
public sealed class MacFactAttribute : FactAttribute
{
    public MacFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
            Skip = "macOS only";
    }
}

/// <summary>Skips the test on Windows: it runs sh scripts or uses Unix file modes.</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "macOS and Linux only";
    }
}

/// <summary>Skips the test outside Windows.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows only";
    }
}
