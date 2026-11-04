namespace ManeFfmpeg.Core.Model;

/// <summary>Which of a project's lists a file is in: the videos clips are cut from, or the audio files that replace clips' sound.</summary>
public enum FileKind
{
    Video,
    Audio,
}

/// <summary>What sets the two kinds of file apart.</summary>
public static class FileKindRules
{
    /// <summary>Why a probed file can't be used as this kind (it lacks the stream it needs), or null when it can.</summary>
    public static string? MissingStream(this FileKind kind, MediaInfo info) => kind switch
    {
        FileKind.Audio => info.AudioStreams.Length == 0 ? "No audio stream" : null,
        _ => info.Video is null ? "No video stream" : null,
    };

    /// <summary>Only videos are cut, so only they have keyframes worth scanning.</summary>
    public static bool HasKeyframes(this FileKind kind) => kind == FileKind.Video;
}
