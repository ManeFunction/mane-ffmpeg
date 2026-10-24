using System.Text;

namespace ManeFfmpeg.Core.Ffmpeg;

/// <summary>
/// A preset's ffmpeg output options, split into tokens and classified: which codecs it picks,
/// whether it stream-copies, which filters it carries, and which options the editor manages itself.
/// </summary>
public sealed class PresetArguments
{
    // Options without a value; everything else that starts with '-' takes the next token.
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal)
    {
        "an", "vn", "sn", "dn", "y", "n", "shortest", "nostdin", "stdin", "hide_banner", "nostats", "stats",
        "copyts", "start_at_zero", "benchmark", "benchmark_all", "dump", "hex", "ignore_unknown", "copy_unknown",
        "accurate_seek", "noaccurate_seek", "autorotate", "noautorotate", "xerror", "debug_ts", "report",
        "fix_sub_duration", "bitexact",
    };

    // Options the editor sets itself; dropped from presets with a warning.
    private static readonly HashSet<string> Managed = new(StringComparer.Ordinal)
    {
        "i", "map", "ss", "t", "to", "sseof", "filter_complex", "lavfi", "filter_complex_script", "progress",
        "y", "n", "nostdin", "stdin", "stats_period", "itsoffset",
    };

    // Options the editor sets itself; dropped silently.
    private static readonly HashSet<string> Quiet = new(StringComparer.Ordinal)
    {
        "hide_banner", "loglevel", "v", "nostats", "stats",
    };

    private PresetArguments() { }

    public IReadOnlyList<string> Tokens { get; private init; } = [];

    /// <summary>The preset tokens minus managed options and minus -vf/-af (which go into the filter graph).</summary>
    public IReadOnlyList<string> OutputTokens { get; private init; } = [];

    public string? VideoCodec { get; private init; }
    public string? AudioCodec { get; private init; }
    public string? VideoFilter { get; private init; }
    public string? AudioFilter { get; private init; }
    public bool NoVideo { get; private init; }
    public bool NoAudio { get; private init; }
    public bool SetsVideoTag { get; private init; }
    public bool SetsMovFlags { get; private init; }
    public IReadOnlyList<string> Warnings { get; private init; } = [];

    public bool VideoCopy => VideoCodec == "copy";
    public bool AudioCopy => AudioCodec == "copy";

    public static PresetArguments Parse(string text)
    {
        var tokens = Tokenize(text);
        var output = new List<string>();
        var warnings = new List<string>();
        string? vcodec = null, acodec = null, vf = null, af = null;
        bool noVideo = false, noAudio = false, tagV = false, movflags = false;

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            // ffmpeg would take a stray value as another output file, written next to the real one.
            if (token.Length < 2 || token[0] != '-' || char.IsDigit(token[1]))
                throw new FormatException($"Unexpected value \"{token}\": every value must follow an option, and a preset can't add outputs.");

            var name = token[1..];
            var (head, spec) = SplitSpecifier(name);
            var takesValue = !Flags.Contains(name);
            var value = takesValue && i + 1 < tokens.Count ? tokens[i + 1] : null;
            if (takesValue && value is null)
            {
                warnings.Add($"Option {token} is missing its value.");
                continue;
            }

            if (Managed.Contains(head) || Quiet.Contains(head))
            {
                if (Managed.Contains(head))
                    warnings.Add($"{token} is set by the editor and was ignored.");
                if (takesValue)
                    i++;
                continue;
            }

            switch (head)
            {
                case "c" or "codec" when IsVideo(spec):
                case "vcodec":
                    vcodec = value;
                    break;
                case "c" or "codec" when IsAudio(spec):
                case "acodec":
                    acodec = value;
                    break;
                case "c" or "codec" when spec is null:
                    vcodec = value;
                    acodec = value;
                    break;
                case "vf" or "filter" when head == "vf" || IsVideo(spec):
                    vf = vf is null ? value : $"{vf},{value}";
                    i++;
                    continue;
                case "af" or "filter" when head == "af" || IsAudio(spec):
                    af = af is null ? value : $"{af},{value}";
                    i++;
                    continue;
                case "vn":
                    noVideo = true;
                    break;
                case "an":
                    noAudio = true;
                    break;
                case "tag" when IsVideo(spec):
                case "vtag":
                    tagV = true;
                    break;
                case "movflags":
                    movflags = true;
                    break;
            }

            output.Add(token);
            if (takesValue)
            {
                output.Add(value!);
                i++;
            }
        }

        return new PresetArguments
        {
            Tokens = tokens,
            OutputTokens = output,
            VideoCodec = vcodec,
            AudioCodec = acodec,
            VideoFilter = vf,
            AudioFilter = af,
            NoVideo = noVideo,
            NoAudio = noAudio,
            SetsVideoTag = tagV,
            SetsMovFlags = movflags,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// Output tokens with every audio codec choice removed and <paramref name="fallback"/> appended.
    /// A generic "-c X" is narrowed to "-c:v X" so the video choice survives.
    /// </summary>
    public IReadOnlyList<string> WithAudioCodecReplaced(IReadOnlyList<string> fallback)
    {
        var result = new List<string>();
        for (var i = 0; i < OutputTokens.Count; i++)
        {
            var token = OutputTokens[i];
            if (token.Length > 1 && token[0] == '-' && !char.IsDigit(token[1]))
            {
                var (head, spec) = SplitSpecifier(token[1..]);
                var isCodec = head is "c" or "codec" or "acodec";
                if (isCodec && (head == "acodec" || IsAudio(spec)))
                {
                    i++;
                    continue;
                }
                if (head is "c" or "codec" && spec is null && i + 1 < OutputTokens.Count)
                {
                    result.Add("-c:v");
                    result.Add(OutputTokens[++i]);
                    continue;
                }
            }
            result.Add(token);
        }
        result.AddRange(fallback);
        return result;
    }

    private static (string Head, string? Spec) SplitSpecifier(string name)
    {
        var colon = name.IndexOf(':');
        return colon < 0 ? (name, null) : (name[..colon], name[(colon + 1)..]);
    }

    private static bool IsVideo(string? spec) => spec is not null && (spec == "v" || spec.StartsWith("v:", StringComparison.Ordinal) || spec == "V");

    private static bool IsAudio(string? spec) => spec is not null && (spec == "a" || spec.StartsWith("a:", StringComparison.Ordinal));

    /// <summary>Shell-like splitting: whitespace separates, '…' is literal, "…" allows \" and \\, \ escapes.</summary>
    public static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inToken = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                if (inToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                }
                continue;
            }

            inToken = true;
            switch (c)
            {
                case '\'':
                {
                    var end = text.IndexOf('\'', i + 1);
                    if (end < 0)
                        throw new FormatException("Unterminated ' quote.");
                    current.Append(text, i + 1, end - i - 1);
                    i = end;
                    break;
                }
                case '"':
                {
                    i++;
                    for (; i < text.Length && text[i] != '"'; i++)
                    {
                        if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] is '"' or '\\')
                            i++;
                        current.Append(text[i]);
                    }
                    if (i >= text.Length)
                        throw new FormatException("Unterminated \" quote.");
                    break;
                }
                case '\\' when i + 1 < text.Length:
                    current.Append(text[++i]);
                    break;
                default:
                    current.Append(c);
                    break;
            }
        }
        if (inToken)
            tokens.Add(current.ToString());
        return tokens;
    }

    /// <summary>Quotes a token for display in a copyable shell command.</summary>
    public static string ShellQuote(string token) =>
        token.Length > 0 && token.All(c => char.IsLetterOrDigit(c) || "-_./:=+,@%".Contains(c))
            ? token
            : "'" + token.Replace("'", "'\\''") + "'";
}
