//! A preset's ffmpeg output options, split into tokens and classified: which codecs it picks, whether it
//! stream-copies, which filters it carries, and which options the editor manages itself.

/// Options without a value; everything else that starts with '-' takes the next token.
const FLAGS: &[&str] = &[
    "an",
    "vn",
    "sn",
    "dn",
    "y",
    "n",
    "shortest",
    "nostdin",
    "stdin",
    "hide_banner",
    "nostats",
    "stats",
    "copyts",
    "start_at_zero",
    "benchmark",
    "benchmark_all",
    "dump",
    "hex",
    "ignore_unknown",
    "copy_unknown",
    "accurate_seek",
    "noaccurate_seek",
    "autorotate",
    "noautorotate",
    "xerror",
    "debug_ts",
    "report",
    "fix_sub_duration",
    "bitexact",
];

/// Options the editor sets itself; dropped from presets with a warning.
const MANAGED: &[&str] = &[
    "i",
    "map",
    "ss",
    "t",
    "to",
    "sseof",
    "filter_complex",
    "lavfi",
    "filter_complex_script",
    "progress",
    "y",
    "n",
    "nostdin",
    "stdin",
    "stats_period",
    "itsoffset",
];

/// Options the editor sets itself; dropped silently.
const QUIET: &[&str] = &["hide_banner", "loglevel", "v", "nostats", "stats"];

#[derive(Clone, Debug, Default, PartialEq)]
pub struct PresetArguments {
    /// The preset tokens minus managed options and minus -vf/-af (which go into the filter graph).
    pub output_tokens: Vec<String>,
    pub video_codec: Option<String>,
    pub audio_codec: Option<String>,
    pub video_filter: Option<String>,
    pub audio_filter: Option<String>,
    pub no_video: bool,
    pub no_audio: bool,
    pub sets_video_tag: bool,
    pub sets_mov_flags: bool,
    pub warnings: Vec<String>,
}

fn split_specifier(name: &str) -> (&str, Option<&str>) {
    match name.find(':') {
        Some(c) => (&name[..c], Some(&name[c + 1..])),
        None => (name, None),
    }
}

fn is_video(spec: Option<&str>) -> bool {
    spec.is_some_and(|s| s == "v" || s.starts_with("v:") || s == "V")
}

fn is_audio(spec: Option<&str>) -> bool {
    spec.is_some_and(|s| s == "a" || s.starts_with("a:"))
}

fn is_option(token: &str) -> bool {
    let mut chars = token.chars();
    token.len() > 1 && chars.next() == Some('-') && !chars.next().unwrap().is_ascii_digit()
}

impl PresetArguments {
    pub fn video_copy(&self) -> bool {
        self.video_codec.as_deref() == Some("copy")
    }

    pub fn audio_copy(&self) -> bool {
        self.audio_codec.as_deref() == Some("copy")
    }

    pub fn parse(text: &str) -> Result<PresetArguments, String> {
        let tokens = tokenize(text)?;
        let mut a = PresetArguments::default();
        let mut i = 0;
        while i < tokens.len() {
            let token = &tokens[i];
            // ffmpeg would take a stray value as another output file, written next to the real one.
            if !is_option(token) {
                return Err(format!("Unexpected value \"{token}\": every value must follow an option, and a preset can't add outputs."));
            }
            let name = &token[1..];
            let (head, spec) = split_specifier(name);
            let takes_value = !FLAGS.contains(&name);
            let value = if takes_value { tokens.get(i + 1).cloned() } else { None };
            if takes_value && value.is_none() {
                a.warnings.push(format!("Option {token} is missing its value."));
                i += 1;
                continue;
            }
            if MANAGED.contains(&head) || QUIET.contains(&head) {
                if MANAGED.contains(&head) {
                    a.warnings.push(format!("{token} is set by the editor and was ignored."));
                }
                i += if takes_value { 2 } else { 1 };
                continue;
            }
            let is_codec = head == "c" || head == "codec";
            if (is_codec && is_video(spec)) || head == "vcodec" {
                a.video_codec = value.clone();
            } else if (is_codec && is_audio(spec)) || head == "acodec" {
                a.audio_codec = value.clone();
            } else if is_codec && spec.is_none() {
                a.video_codec = value.clone();
                a.audio_codec = value.clone();
            } else if head == "vf" || (head == "filter" && is_video(spec)) {
                let v = value.unwrap();
                a.video_filter = Some(match a.video_filter.take() {
                    Some(f) => format!("{f},{v}"),
                    None => v,
                });
                i += 2;
                continue;
            } else if head == "af" || (head == "filter" && is_audio(spec)) {
                let v = value.unwrap();
                a.audio_filter = Some(match a.audio_filter.take() {
                    Some(f) => format!("{f},{v}"),
                    None => v,
                });
                i += 2;
                continue;
            } else if head == "vn" {
                a.no_video = true;
            } else if head == "an" {
                a.no_audio = true;
            } else if (head == "tag" && is_video(spec)) || head == "vtag" {
                a.sets_video_tag = true;
            } else if head == "movflags" {
                a.sets_mov_flags = true;
            }
            a.output_tokens.push(token.clone());
            if takes_value {
                a.output_tokens.push(value.unwrap());
                i += 1;
            }
            i += 1;
        }
        Ok(a)
    }

    /// Output tokens with every audio codec choice removed and `fallback` appended. A generic "-c X" is narrowed to
    /// "-c:v X" so the video choice survives.
    pub fn with_audio_codec_replaced(&self, fallback: &[String]) -> Vec<String> {
        let t = &self.output_tokens;
        let mut result = Vec::new();
        let mut i = 0;
        while i < t.len() {
            let token = &t[i];
            if is_option(token) {
                let (head, spec) = split_specifier(&token[1..]);
                let is_codec = matches!(head, "c" | "codec" | "acodec");
                if is_codec && (head == "acodec" || is_audio(spec)) {
                    i += 2;
                    continue;
                }
                if matches!(head, "c" | "codec") && spec.is_none() && i + 1 < t.len() {
                    result.push("-c:v".to_string());
                    result.push(t[i + 1].clone());
                    i += 2;
                    continue;
                }
            }
            result.push(token.clone());
            i += 1;
        }
        result.extend(fallback.iter().cloned());
        result
    }
}

/// Shell-like splitting: whitespace separates, '…' is literal, "…" allows \" and \\, \ escapes.
pub fn tokenize(text: &str) -> Result<Vec<String>, String> {
    let c: Vec<char> = text.chars().collect();
    let mut tokens = Vec::new();
    let mut current = String::new();
    let mut in_token = false;
    let mut i = 0;
    while i < c.len() {
        let ch = c[i];
        if ch.is_whitespace() {
            if in_token {
                tokens.push(std::mem::take(&mut current));
                in_token = false;
            }
            i += 1;
            continue;
        }
        in_token = true;
        match ch {
            '\'' => {
                let end = c[i + 1..].iter().position(|x| *x == '\'').ok_or("Unterminated ' quote.")? + i + 1;
                current.extend(&c[i + 1..end]);
                i = end;
            }
            '"' => {
                i += 1;
                while i < c.len() && c[i] != '"' {
                    if c[i] == '\\' && i + 1 < c.len() && matches!(c[i + 1], '"' | '\\') {
                        i += 1;
                    }
                    current.push(c[i]);
                    i += 1;
                }
                if i >= c.len() {
                    return Err("Unterminated \" quote.".into());
                }
            }
            '\\' if i + 1 < c.len() => {
                i += 1;
                current.push(c[i]);
            }
            _ => current.push(ch),
        }
        i += 1;
    }
    if in_token {
        tokens.push(current);
    }
    Ok(tokens)
}

/// Quotes a token for display in a copyable shell command.
pub fn shell_quote(token: &str) -> String {
    if !token.is_empty() && token.chars().all(|c| c.is_alphanumeric() || "-_./:=+,@%".contains(c)) {
        token.to_string()
    } else {
        format!("'{}'", token.replace('\'', "'\\''"))
    }
}
