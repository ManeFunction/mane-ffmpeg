using System.Text;
using ManeFfmpeg.Core.Media;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.Core.Mpv;

/// <summary>
/// Describes the edited timeline to mpv as an EDL with parallel streams: the video cuts, the audio cuts slipped by
/// each clip's offset (with silence wherever the slipped audio runs past its file), and, when A2 has clips, the A2
/// track padded with silence to the timeline's length. Tracks are titled so the player can pick video from the first
/// stream and audio from the others; <see cref="TimelineMix"/> mixes A1 and A2 and applies the clips' volumes.
/// </summary>
public static class TimelineEdl
{
    public const string VideoTitle = "mane-video";
    public const string AudioTitle = "mane-audio";
    public const string TrackTitle = "mane-a2";

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

            // The heard file: an audio file dropped on the clip, or the clip's own video. One that isn't analyzed
            // yet plays silence until the catalog reports it and the preview reloads.
            if (state.HeardAudio(clip, media.GetValueOrDefault) is not { } heard)
            {
                Silence(audio, silencePath, clip.Duration);
                continue;
            }
            anyAudio = true;
            foreach (var span in heard.Layout(clip).Spans())
            {
                if (span.ReadFrom is { } readFrom)
                    Segment(audio, heard.File.Path, readFrom + heard.Info.StartTime, span.Length);
                else
                    Silence(audio, silencePath, span.Length);
            }
        }

        if (count == 0)
            return null;
        var edl = new StringBuilder("edl://!new_stream;!track_meta,title=").Append(VideoTitle).Append(';').Append(video);
        var track = Track(state, media, silencePath);
        // A2 is mixed into A1, so A1 is there (silent) even when no clip has sound of its own.
        if (anyAudio || track is not null)
            edl.Append("!new_stream;!track_meta,title=").Append(AudioTitle).Append(';').Append(audio);
        if (track is not null)
            edl.Append("!new_stream;!track_meta,title=").Append(TrackTitle).Append(';').Append(track);
        return edl.ToString().TrimEnd(';');
    }

    /// <summary>The A2 track over the whole timeline, or null when it has no clips.</summary>
    private static StringBuilder? Track(ProjectState state, IReadOnlyDictionary<string, MediaInfo> media, string silencePath)
    {
        if (state.AudioTrack.Count == 0)
            return null;
        var sb = new StringBuilder();
        HeardAudio? Heard(TrackClip clip) => state.HeardAudio(clip, media.GetValueOrDefault);
        foreach (var span in TrackLayout.Spans(state.AudioTrack, TimeSpan.Zero, state.Timeline.Duration, c => Heard(c)?.End))
        {
            // A file that isn't analyzed yet plays silence until the catalog reports it and the preview reloads.
            if (span.Clip is { } clip && Heard(clip) is { } heard)
                Segment(sb, heard.File.Path, span.ReadFrom + heard.Info.StartTime, span.Length);
            else
                Silence(sb, silencePath, span.Length);
        }
        return sb;
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

    // Pieces of the silence file, kept a minute short of its end.
    private static readonly TimeSpan SilenceLength = SilenceAsset.Length - TimeSpan.FromMinutes(1);
}
