using System.Globalization;
using System.Text;
using ManeFfmpeg.Core.Media;
using ManeFfmpeg.Core.Model;
using ManeFfmpeg.Core.Mpv;

namespace ManeFfmpeg.Core.Ffmpeg;

/// <summary>The A2 track, built over the timeline as one stream that is mixed into the clips' audio.</summary>
public static partial class ExportPlanner
{
    /// <summary>",volume=…" for a clip's gain (its own times its track's).</summary>
    private static string VolumeFilter(double gain) =>
        string.Create(CultureInfo.InvariantCulture, $",volume={Math.Clamp(gain, 0, Gain.MaxCombined):0.######}");

    /// <summary>
    /// Appends the A2 track over the timeline ranges, joined in order, as [atrack]; nothing when A2 has no clips. Every
    /// piece is cut to the samples between its start and end in the joined track, so it never drifts against the clips.
    /// </summary>
    private static void AppendTrack(StringBuilder graph, Context ctx, IReadOnlyList<(TimeSpan From, TimeSpan To)> ranges,
        (int Rate, string Layout) format, ref int nextInput, List<string> inputs)
    {
        if (!ctx.HasTrack)
            return;
        var clips = ctx.Track.Select(t => t.Clip).ToList();
        var heard = ctx.Track.ToDictionary(t => t.Clip.Id, t => t.Heard);
        var fmt = $"aformat=sample_rates={format.Rate}:channel_layouts={format.Layout}";
        var labels = new StringBuilder();
        var count = 0;
        var at = TimeSpan.Zero;
        foreach (var (from, to) in ranges)
        foreach (var span in TrackLayout.Spans(clips, from, to, c => heard[c.Id].End))
        {
            var samples = Samples(at + span.Length, format.Rate) - Samples(at, format.Rate);
            at += span.Length;
            if (samples <= 0)
                continue;
            if (span.Clip is not { } clip)
            {
                graph.Append(CultureInfo.InvariantCulture, $"anullsrc=r={format.Rate}:cl={format.Layout},atrim=end_sample={samples}[t{count}];");
            }
            else
            {
                var file = heard[clip.Id];
                var trim = Input(inputs, file.File.Path, file.Info, span.ReadFrom, span.Length);
                graph.Append($"[{nextInput}:{file.Stream.Index}]");
                if (trim.Length > 0)
                    graph.Append($"atrim={trim}duration={TimeFormat.Seconds(span.Length)},asetpts=PTS-STARTPTS,");
                graph.Append(fmt);
                if (!Gain.IsUnity(clip.Volume))
                    graph.Append(VolumeFilter(clip.Volume));
                graph.Append(CultureInfo.InvariantCulture, $",apad=whole_len={samples},atrim=end_sample={samples}[t{count}];");
                nextInput++;
            }
            labels.Append($"[t{count}]");
            count++;
        }
        if (count == 0)
            graph.Append(CultureInfo.InvariantCulture, $"anullsrc=r={format.Rate}:cl={format.Layout},atrim=end_sample=1[t0];");
        graph.Append(count == 0 ? "[t0]" : labels).Append($"concat=n={Math.Max(count, 1)}:v=0:a=1,asetpts=N/SR/TB[atrack];");
    }
}
