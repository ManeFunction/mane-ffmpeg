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

    /// <summary>The preset with defaults for fields a hand-written or damaged file left empty.</summary>
    public Preset WithDefaults() => this with
    {
        Extension = Extension ?? "",
        AudioFallbackArguments = string.IsNullOrWhiteSpace(AudioFallbackArguments) ? DefaultAudioFallback : AudioFallbackArguments,
    };

    /// <summary>
    /// Archival HEVC: CRF 23 in 10-bit with x265 tuned to keep detail (aq-mode 3, psy-rdoq, no SAO), so moving
    /// objects don't smear. About 1.4x the size of <see cref="Harsh"/> with clearly better detail in motion.
    /// </summary>
    public static readonly Preset ForStorage = new()
    {
        Id = "builtin-for-storage",
        Name = "for storage",
        Arguments = "-c:v libx265 -crf 23 -preset slow -pix_fmt yuv420p10le -x265-params aq-mode=3:psy-rdoq=1.0:no-sao=1 -c:a copy",
        Extension = "mp4",
    };

    /// <summary>The original, smaller but softer "for storage" settings.</summary>
    public static readonly Preset Harsh = new()
    {
        Id = "builtin-harsh",
        Name = "harsh",
        Arguments = ForStorageV1Arguments,
        Extension = "mp4",
    };

    /// <summary>
    /// Everyday video for messengers and social media: H.264 High in 8-bit 4:2:0 and AAC play everywhere (phones,
    /// browsers, TVs). CRF 20 stays well above the bitrates the platforms re-encode to, and the MP4 index goes
    /// first so it starts playing before it has fully downloaded.
    /// </summary>
    public static readonly Preset Share = new()
    {
        Id = "builtin-share",
        Name = "share",
        Arguments = "-c:v libx264 -crf 20 -preset medium -profile:v high -pix_fmt yuv420p -c:a aac -b:a 192k -movflags +faststart",
        Extension = "mp4",
    };

    public static readonly Preset PlaneCopy = new()
    {
        Id = "builtin-plane-copy",
        Name = "plane copy",
        Arguments = "-c:v copy -c:a copy",
        Extension = "",
    };

    public static IReadOnlyList<Preset> BuiltIns { get; } = [ForStorage, Harsh, Share, PlaneCopy];

    /// <summary>"for storage" arguments before version 2 of the presets file.</summary>
    public const string ForStorageV1Arguments = "-c:v libx265 -crf 28 -preset slow -c:a copy";
}
