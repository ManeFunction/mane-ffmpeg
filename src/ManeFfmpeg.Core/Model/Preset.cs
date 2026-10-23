namespace ManeFfmpeg.Core.Model;

/// <summary>Export settings: raw ffmpeg output options plus a few things the editor needs to know.</summary>
public sealed record Preset
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>ffmpeg output options, e.g. "-c:v libx265 -crf 28 -preset slow -c:a copy".</summary>
    public required string Arguments { get; init; }

    /// <summary>Default output extension without the dot; empty means "same as the first source".</summary>
    public string Extension { get; init; } = "";

    /// <summary>Audio options used instead of "-c:a copy" when the edit forces audio through filters.</summary>
    public string AudioFallbackArguments { get; init; } = DefaultAudioFallback;

    public const string DefaultAudioFallback = "-c:a aac -b:a 256k";

    public static readonly Preset ForStorage = new()
    {
        Id = "builtin-for-storage",
        Name = "for storage",
        Arguments = "-c:v libx265 -crf 28 -preset slow -c:a copy",
        Extension = "mp4",
    };

    public static readonly Preset PlaneCopy = new()
    {
        Id = "builtin-plane-copy",
        Name = "plane copy",
        Arguments = "-c:v copy -c:a copy",
        Extension = "",
    };

    public static IReadOnlyList<Preset> BuiltIns { get; } = [ForStorage, PlaneCopy];
}
