using ManeFfmpeg.Core.Media;
using ManeFfmpeg.Core.Model;

namespace ManeFfmpeg.Core.Ffmpeg;

/// <summary>
/// An ffmpeg command that writes the timeline's mixed audio to stdout as raw interleaved 32-bit floats
/// (<paramref name="Channels"/> per frame, at <paramref name="SampleRate"/>), starting at timeline time 0.
/// </summary>
public sealed record AudioScanPlan(IReadOnlyList<string> Arguments, int SampleRate, int Channels, TimeSpan Duration);

/// <summary>The timeline's audio as it would be exported, for measuring it (the clipping scan).</summary>
public static partial class ExportPlanner
{
    private static readonly Preset ScanPreset = new()
    {
        Id = "internal-audio-scan", Name = "audio scan", Kind = PresetKind.Audio, Arguments = "-c:a pcm_f32le",
    };

    /// <summary>
    /// The export's own audio graph (clip, A1/A2 and A2 clip volumes, the A2 mix), decoded as float so that anything
    /// past full scale stays visible instead of being clipped by an encoder. Video streams are never read. The
    /// preset's own audio filter isn't applied: the scan is about the volumes set in the editor. Null when the
    /// timeline has no audio.
    /// </summary>
    /// <exception cref="ExportPlanException">The timeline is empty or its media isn't analyzed yet.</exception>
    public static AudioScanPlan? AudioScan(ProjectState state, IReadOnlyDictionary<string, MediaInfo> media)
    {
        var ctx = Prepare(new ExportJob { State = state, Media = media, Preset = ScanPreset, OutputPath = "", WorkDirectory = "" });
        if (!ctx.AnyAudio)
            return null;
        var inputs = new List<string>();
        var graph = AudioGraph(ctx, inputs, audioFilter: null);
        var (rate, _) = ctx.AudioFormat!.Value;
        var channels = ctx.Items.Select(i => i.Audio).Concat(ctx.Track.Select(t => t.Heard.Stream))
            .OfType<AudioStreamInfo>().Max(a => a.Channels);

        var a = new List<string>(FfmpegTools.Quiet) { "-nostats" };
        // Only audio is decoded: telling every input to skip its other streams spares the demuxer most of a video file.
        for (var i = 0; i < inputs.Count; i++)
        {
            if (inputs[i] == "-i")
            {
                a.AddRange(["-vn", "-sn", "-dn", "-i", inputs[++i]]);
                continue;
            }
            a.Add(inputs[i]);
        }
        a.AddRange(["-filter_complex", graph, "-map", "[aout]", "-ac", channels.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-c:a", "pcm_f32le", "-f", "f32le", "pipe:1"]);
        return new AudioScanPlan(a, rate, Math.Max(1, channels), state.Timeline.Duration);
    }
}
