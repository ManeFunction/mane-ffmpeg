using System.Globalization;
using System.Text;
using ManeFfmpeg.Core.Media;
using ManeFfmpeg.Core.Model;
using ManeFfmpeg.Core.Mpv;

namespace ManeFfmpeg.Core.Ffmpeg;

/// <summary>Audio alone, for audio presets: one clip's audio copied as it is, or the timeline's audio encoded.</summary>
public static partial class ExportPlanner
{
    // ---- Audio only ------------------------------------------------------------------------------------

    /// <summary>
    /// The file extension (without the dot) that holds audio in <paramref name="codec"/> as it is, for an ffprobe codec
    /// name or an ffmpeg encoder name; null (ffmpeg's default encoder) gets .m4a. Matroska audio (.mka) carries the rest.
    /// </summary>
    public static string AudioExtension(string? codec) => codec switch
    {
        null or "aac" or "libfdk_aac" or "aac_at" or "alac" or "alac_at" => "m4a",
        "mp3" or "libmp3lame" or "libshine" => "mp3",
        "opus" or "libopus" => "opus",
        "vorbis" or "libvorbis" => "ogg",
        "flac" => "flac",
        "ac3" or "eac3" or "dts" => codec,
        "pcm_u8" or "pcm_s16le" or "pcm_s24le" or "pcm_s32le" or "pcm_f32le" or "pcm_f64le" or "pcm_alaw" or "pcm_mulaw" => "wav",
        "pcm_s16be" or "pcm_s24be" or "pcm_s32be" => "aiff",
        _ => "mka",
    };

    /// <summary>
    /// The extension an audio preset's file gets: its own, or else the one for the codec it writes (the copied audio's,
    /// or the encoder's). Null while the timeline can't be planned yet, for example before its media is analyzed.
    /// </summary>
    public static string? AudioExtensionFor(ProjectState state, IReadOnlyDictionary<string, MediaInfo> media, Preset preset)
    {
        if (preset.Extension?.Trim().TrimStart('.') is { Length: > 0 } own)
            return own;
        try
        {
            var ctx = Prepare(new ExportJob { State = state, Media = media, Preset = preset, OutputPath = "", WorkDirectory = "" });
            return AudioExtension(CopiedAudio(ctx) is { } copied ? copied.Item.Audio!.Codec : EncodedAudioCodec(ctx));
        }
        catch (Exception ex) when (ex is ExportPlanException or FormatException)
        {
            return null;
        }
    }

    /// <summary>The encoder the preset's audio goes through when it isn't copied: its own, or its fallback for "-c:a copy".</summary>
    private static string? EncodedAudioCodec(Context ctx) =>
        ctx.Args.AudioCopy ? PresetArguments.Parse(ctx.Job.Preset.AudioFallbackArguments).AudioCodec : ctx.Args.AudioCodec;

    private static ExportPlan AudioOnly(Context ctx)
    {
        if (!ctx.AnyAudio)
            throw new ExportPlanException("The timeline has no audio to save.");
        // The output's name may have been chosen for other audio than the timeline now has (the copied codec, or the
        // fallback once something stops the copy), so check that the file can hold what is written.
        var extension = Path.GetExtension(ctx.Out).TrimStart('.');
        string? reason = null, copiedCodec = null;
        if (CopiedAudio(ctx) is { } copied)
        {
            copiedCodec = copied.Item.Audio!.Codec;
            if (AudioContainer.Accepts(extension, copiedCodec) != false)
                return AudioCopy(ctx, copied.Item, copied.Placement);
            reason = $"a .{extension} file can't hold {copiedCodec} audio as it is";
        }
        var encoder = EncodedAudioCodec(ctx);
        if (AudioContainer.Accepts(extension, encoder) == false)
            throw new ExportPlanException(
                $"A .{extension} file can't hold the {AudioContainer.Normalize(encoder!)} audio \"{ctx.Job.Preset.Name}\" writes here. " +
                $"Name the file .{AudioExtension(copiedCodec ?? encoder)}, or press Auto.");
        return AudioEncode(ctx, reason);
    }

    /// <summary>
    /// The clip whose audio can be saved without re-encoding: the only one on the timeline, playing its own audio, with
    /// all of it read from the file (no silence before or after), at its own volume and with nothing on A2. Null when the
    /// audio has to be encoded.
    /// </summary>
    private static (Item Item, AudioPlacement Placement)? CopiedAudio(Context ctx)
    {
        if (!ctx.Args.AudioCopy || ctx.CutBlocker is not null || ctx.Items is not [{ Heard: { } heard } item])
            return null;
        return heard.Layout(item.Clip) is AudioLayout.Placed { Placement: { IsSilent: false } placement }
            && placement.Lead <= Tolerance && placement.Tail <= Tolerance
            ? (item, placement)
            : null;
    }

    /// <summary>
    /// One clip's audio stream-copied straight into the output. Like <see cref="SingleClipAudioCopy"/>'s cut, it seeks a
    /// little early and cuts on the output side, so it starts at the first whole packet of the clip.
    /// </summary>
    private static ExportPlan AudioCopy(Context ctx, Item item, AudioPlacement placement)
    {
        var heard = item.Heard!;
        var (_, readFrom, readLength, _) = placement;
        var fromStart = readFrom <= Tolerance;
        var toEnd = readFrom + readLength >= heard.Info.Duration - Tolerance;
        var preSeek = readFrom > AudioPreSeek ? readFrom - AudioPreSeek : TimeSpan.Zero;

        var a = new List<string>(Prefix);
        if (preSeek > TimeSpan.Zero)
            a.AddRange(["-ss", TimeFormat.Seconds(preSeek)]);
        a.AddRange(["-i", heard.File.Path, "-map", $"0:{heard.Stream.Index}"]);
        a.AddRange(ctx.Args.OutputTokens);
        if (!fromStart)
            a.AddRange(["-ss", TimeFormat.Seconds(readFrom - preSeek)]);
        if (!toEnd)
            a.AddRange(["-t", TimeFormat.Seconds(readLength)]);
        a.Add(ctx.Out);

        var note = fromStart && toEnd
            ? "The clip's audio is copied whole, without re-encoding."
            : "The clip's audio is copied without re-encoding, cut at the audio packets nearest to its edges (a few milliseconds).";
        return new ExportPlan(ExportMode.AudioCopy, [new ExportStep("Saving audio", a, readLength, true)], [], readLength, [note]);
    }

    /// <summary>Every clip's audio (silence where a clip has none) joined in a filter graph, then encoded.</summary>
    /// <param name="reason">Why the audio isn't copied, when nothing on the timeline stops it.</param>
    private static ExportPlan AudioEncode(Context ctx, string? reason = null)
    {
        var notes = new List<string>();
        var inputs = new List<string>();
        var graph = new StringBuilder();
        var format = ctx.AudioFormat!.Value;
        var n = ctx.Items.Count;
        var input = 0;
        for (var i = 0; i < n; i++)
        {
            var item = ctx.Items[i];
            graph.Append(AudioChain(item, item.Clip.In, item.Clip.Duration, format, ref input, inputs)).Append($"[a{i}];");
        }
        AppendTrack(graph, ctx, [(TimeSpan.Zero, ctx.Job.State.Timeline.Duration)], format, ref input, inputs);
        AppendConcat(graph, n, video: false, audio: true, null, ctx.Args.AudioFilter, track: ctx.HasTrack);

        var a = new List<string>(Prefix);
        a.AddRange(inputs);
        a.AddRange(["-filter_complex", graph.ToString(), "-map", "[aout]"]);
        a.AddRange(AudioTokens(ctx, true, notes,
            AudioReencodeReason(ctx, otherwise: reason ?? (n > 1 ? "the timeline has several clips" : "the audio doesn't cover the whole clip"))));
        a.Add(ctx.Out);
        var duration = ctx.Items.Aggregate(TimeSpan.Zero, (s, it) => s + it.Clip.Duration);
        return new ExportPlan(ExportMode.AudioEncode, [new ExportStep("Saving audio", a, duration, true)], [], duration, notes);
    }
}
