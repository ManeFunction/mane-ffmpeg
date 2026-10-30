namespace ManeFfmpeg.Core.Updates;

/// <summary>Release versions as major.minor.patch, the form the tags (<c>v0.9.0</c>) and Directory.Build.props use.</summary>
public static class AppVersion
{
    /// <summary>This build's version.</summary>
    public static Version Current { get; } = Normalize(typeof(AppVersion).Assembly.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>
    /// A tag or changelog version: a leading "v" and any pre-release or build suffix ("-rc.1", "+abc") are ignored.
    /// Null when it isn't a version.
    /// </summary>
    public static Version? Parse(string text)
    {
        var value = text.Trim().TrimStart('v', 'V');
        var end = value.IndexOfAny(['-', '+']);
        if (end >= 0)
            value = value[..end];
        return Version.TryParse(value, out var version) ? Normalize(version) : null;
    }

    /// <summary>
    /// Three parts, so 0.9.0.0 (an assembly version) equals 0.9.0 (a tag): <see cref="Version"/> counts a missing part
    /// as lower than 0.
    /// </summary>
    public static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0));
}
