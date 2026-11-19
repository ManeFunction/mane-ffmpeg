using System.Text.Json.Serialization;

namespace ManeFfmpeg.Core.Model;

/// <summary>What a preset makes: an edited video, or the timeline's audio alone.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PresetKind>))]
public enum PresetKind
{
    Video,
    Audio,
}

/// <summary>Export settings: raw ffmpeg output options plus a few things the editor needs to know.</summary>
public sealed record Preset
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>Video presets export the edit; audio presets save its audio alone. Files without the field hold video presets.</summary>
    public PresetKind Kind { get; init; }

    /// <summary>ffmpeg output options, e.g. "-c:v libx265 -crf 28 -preset slow -c:a copy".</summary>
    public required string Arguments { get; init; }

    /// <summary>
    /// Default output extension without the dot. Empty means "same as the first source" for video, and for audio the
    /// file type that holds the audio codec as it is (see <see cref="Ffmpeg.ExportPlanner.AudioExtension"/>).
    /// </summary>
    public string Extension { get; init; } = "";

    /// <summary>Audio options used instead of "-c:a copy" when the edit forces audio through filters.</summary>
    public string AudioFallbackArguments { get; init; } = DefaultAudioFallback;

    public const string DefaultAudioFallback = "-c:a aac -b:a 256k";

    /// <summary>Exports exactly as <paramref name="other"/> does: the same options, extension and audio fallback.</summary>
    public bool RunsLike(Preset other) =>
        Kind == other.Kind && Arguments.Trim() == other.Arguments.Trim() && (Extension ?? "") == (other.Extension ?? "")
        && AudioFallbackArguments.Trim() == other.AudioFallbackArguments.Trim();

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
        Name = "for storage (balanced)",
        Arguments = "-c:v libx265 -crf 23 -preset slow -pix_fmt yuv420p10le -x265-params aq-mode=3:psy-rdoq=1.0:no-sao=1 -c:a copy",
        Extension = "mp4",
    };

    /// <summary>The original, smaller but softer "for storage" settings.</summary>
    public static readonly Preset Harsh = new()
    {
        Id = "builtin-harsh",
        Name = "harsh compression",
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
        Name = "for share (compatibility)",
        Arguments = "-c:v libx264 -crf 20 -preset medium -profile:v high -pix_fmt yuv420p -c:a aac -b:a 192k -movflags +faststart",
        Extension = "mp4",
    };

    public static readonly Preset PlaneCopy = new()
    {
        Id = "builtin-plane-copy",
        Name = "copy (w/o encode)",
        Arguments = "-c:v copy -c:a copy",
        Extension = "",
    };

    /// <summary>Stream copy into an MP4, for sources in another container.</summary>
    public static readonly Preset CopyToMp4 = new()
    {
        Id = "builtin-copy-mp4",
        Name = "copy to mp4 (w/o encode)",
        Arguments = "-c:v copy -c:a copy",
        Extension = "mp4",
    };

    /// <summary>
    /// The timeline's audio exactly as it was recorded: one clip playing its own audio is cut without re-encoding, into
    /// the file type its codec belongs in (.m4a for AAC, .mp3, .flac…). Several clips, replaced audio or audio offsets
    /// that need silence are encoded losslessly to FLAC.
    /// </summary>
    public static readonly Preset ExtractAudio = new()
    {
        Id = "builtin-audio-original",
        Name = "extract audio (as is)",
        Kind = PresetKind.Audio,
        Arguments = "-c:a copy",
        Extension = "",
        AudioFallbackArguments = "-c:a flac",
    };

    /// <summary>AAC in an M4A: small, and plays everywhere Apple's and Android's players do.</summary>
    public static readonly Preset AudioM4a = new()
    {
        Id = "builtin-audio-m4a",
        Name = "m4a (AAC 256k)",
        Kind = PresetKind.Audio,
        Arguments = "-c:a aac -b:a 256k",
        Extension = "m4a",
    };

    /// <summary>MP3 at LAME's best variable quality (V0, about 245 kbit/s), for players that know nothing else.</summary>
    public static readonly Preset AudioMp3 = new()
    {
        Id = "builtin-audio-mp3",
        Name = "mp3 (V0)",
        Kind = PresetKind.Audio,
        Arguments = "-c:a libmp3lame -q:a 0",
        Extension = "mp3",
    };

    public static IReadOnlyList<Preset> BuiltIns { get; } = [ForStorage, Share, Harsh, PlaneCopy, CopyToMp4, ExtractAudio, AudioM4a, AudioMp3];

    /// <summary>"for storage" arguments before version 2 of the presets file.</summary>
    public const string ForStorageV1Arguments = "-c:v libx265 -crf 28 -preset slow -c:a copy";
}
