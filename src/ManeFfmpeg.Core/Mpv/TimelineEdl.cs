using System.Text;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.Core.Mpv;

/// <summary>
/// Describes the edited timeline to mpv as an EDL with two parallel streams: the video cuts, and the
/// audio cuts slipped by each clip's offset, with silence wherever the slipped audio runs past its file.
/// Tracks are titled so the player can pick video from the first stream and audio from the second.
/// </summary>
public static class TimelineEdl
{
    public const string VideoTitle = "mane-video";
    public const string AudioTitle = "mane-audio";

    private static readonly TimeSpan Epsilon = TimeSpan.FromMilliseconds(1);

    /// <summary>Returns an "edl://" URL, or null when nothing on the timeline can be played.</summary>
    public static string? Build(ProjectState state, IReadOnlyDictionary<string, MediaInfo> media, string silencePath)
    {
        var video = new StringBuilder();
        var audio = new StringBuilder();
        var anyAudio = false;
        var count = 0;

        foreach (var clip in state.Timeline.Clips)
        {
            if (state.Source(clip.SourceId) is not { } source || !media.TryGetValue(source.Id, out var info) || info.Video is null)
                return null;
            count++;
            // EDL segment starts are file timestamps, which begin at the container start time, not at 0.
            Segment(video, source.Path, clip.In + info.StartTime, clip.Duration);

            var stream = info.Audio(source.AudioStreamIndex);
            if (stream is null)
            {
                Silence(audio, silencePath, clip.Duration);
                continue;
            }
            anyAudio = true;
            var start = clip.AudioIn;
            var lead = start < TimeSpan.Zero ? -start : TimeSpan.Zero;
            var readFrom = start < TimeSpan.Zero ? TimeSpan.Zero : start;
            var audioEnd = stream.Duration ?? info.Duration;
            var readEnd = clip.AudioOut < audioEnd ? clip.AudioOut : audioEnd;
            var readLength = readEnd - readFrom;
            if (readLength <= Epsilon)
            {
                Silence(audio, silencePath, clip.Duration);
                continue;
            }
            Silence(audio, silencePath, lead);
            Segment(audio, source.Path, readFrom + info.StartTime, readLength);
            Silence(audio, silencePath, clip.Duration - lead - readLength);
        }

        if (count == 0)
            return null;
        var edl = new StringBuilder("edl://!new_stream;!track_meta,title=").Append(VideoTitle).Append(';').Append(video);
        if (anyAudio)
            edl.Append("!new_stream;!track_meta,title=").Append(AudioTitle).Append(';').Append(audio);
        return edl.ToString().TrimEnd(';');
    }

    private static void Segment(StringBuilder sb, string path, TimeSpan start, TimeSpan length) =>
        sb.Append('%').Append(Encoding.UTF8.GetByteCount(path)).Append('%').Append(path)
          .Append(',').Append(TimeFormat.Seconds(start)).Append(',').Append(TimeFormat.Seconds(length)).Append(';');

    private static void Silence(StringBuilder sb, string silencePath, TimeSpan length)
    {
        while (length > Epsilon)
        {
            var piece = length < SilenceLength ? length : SilenceLength;
            Segment(sb, silencePath, TimeSpan.Zero, piece);
            length -= piece;
        }
    }

    private static readonly TimeSpan SilenceLength = TimeSpan.FromMinutes(9);
}
