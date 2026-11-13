//! Turns a timeline and a preset into ffmpeg commands. Pure, apart from resolving symbolic links.

use super::preset_analysis::PresetAnalysis;
use super::preset_arguments::{PresetArguments, shell_quote};
use super::tools::QUIET;
use crate::media::keyframes::KeyframeIndex;
use crate::model::time::{self, Time, seconds, trim_number};
use crate::model::{
    AudioLayout, AudioLoop, AudioStreamInfo, Clip, CropRect, HeardAudio, MediaInfo, OutputFrame, PixelCrop, Preset, ProjectState,
    SourceMedia, VideoStreamInfo,
};
use std::collections::HashMap;
use std::path::{Path, PathBuf};

#[derive(Debug, Clone, PartialEq, thiserror::Error)]
#[error("{0}")]
pub struct ExportPlanError(pub String);

fn fail<T>(msg: impl Into<String>) -> Result<T, ExportPlanError> {
    Err(ExportPlanError(msg.into()))
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum ExportMode {
    /// One untouched clip: the preset runs on the whole file, exactly like a hand-written command.
    Passthrough,
    /// Video is encoded; every clip is trimmed and joined in a filter graph.
    Encode,
    /// Video is stream-copied through the concat demuxer, cut at keyframes.
    StreamCopy,
    /// One trimmed or offset clip whose audio is copied: audio is cut losslessly first, then muxed.
    SingleClipAudioCopy,
}

#[derive(Clone, Debug)]
pub struct ExportJob {
    pub state: ProjectState,
    /// Probe results by source id.
    pub media: HashMap<String, MediaInfo>,
    pub preset: Preset,
    pub output_path: String,
    /// Directory for list files and intermediates; the runner creates and deletes it.
    pub work_directory: String,
    /// Keyframe indexes by source id; required for stream copy.
    pub keyframes: HashMap<String, KeyframeIndex>,
}

/// ffmpeg shifts a concat input by minus its (often negative) start time, which is only known once the list's
/// segments exist. Filtered audio has to be shifted the same way to stay aligned with the copied video, so the
/// runner probes `list_path` first and replaces [`SHIFT_PLACEHOLDER`] in the step's arguments with that shift (in
/// seconds).
#[derive(Clone, Debug, PartialEq)]
pub struct ConcatShift {
    pub list_path: String,
}

pub const SHIFT_PLACEHOLDER: &str = "$SHIFT";

/// One ffmpeg invocation. `duration` is the media time it produces, for progress.
#[derive(Clone, Debug, PartialEq)]
pub struct ExportStep {
    pub title: String,
    pub arguments: Vec<String>,
    pub duration: Time,
    pub writes_output: bool,
    pub shift: Option<ConcatShift>,
}

impl ExportStep {
    fn new(title: impl Into<String>, arguments: Vec<String>, duration: Time, writes_output: bool) -> ExportStep {
        ExportStep { title: title.into(), arguments, duration, writes_output, shift: None }
    }

    /// The arguments with the concat shift filled in.
    pub fn arguments_for(&self, shift: &str) -> Vec<String> {
        self.arguments.iter().map(|a| a.replace(SHIFT_PLACEHOLDER, shift)).collect()
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct PlannedFile {
    pub path: String,
    pub content: String,
}

#[derive(Clone, Debug, PartialEq)]
pub struct ExportPlan {
    pub mode: ExportMode,
    pub steps: Vec<ExportStep>,
    pub files: Vec<PlannedFile>,
    pub duration: Time,
    pub notes: Vec<String>,
    /// The file the last step writes.
    pub output_path: String,
    /// Where the planned files and intermediates go; the runner creates and deletes it.
    pub work_directory: String,
}

impl ExportPlan {
    fn new(mode: ExportMode, steps: Vec<ExportStep>, files: Vec<PlannedFile>, duration: Time, notes: Vec<String>) -> Self {
        ExportPlan { mode, steps, files, duration, notes, output_path: String::new(), work_directory: String::new() }
    }

    /// The commands as a copyable shell snippet.
    pub fn command_text(&self, ffmpeg: &str) -> String {
        let mut sb = String::new();
        for f in &self.files {
            sb.push_str(&format!("# {}:\n# {}\n\n", f.path, f.content.trim_end().replace('\n', "\n# ")));
        }
        for step in &self.steps {
            if let Some(shift) = &step.shift {
                sb.push_str(&format!("# {SHIFT_PLACEHOLDER}: minus the start time of {}, probed before this runs\n", shift.list_path));
            }
            sb.push_str(&shell_quote(ffmpeg));
            sb.push(' ');
            sb.push_str(&step.arguments.iter().map(|a| shell_quote(a)).collect::<Vec<_>>().join(" "));
            sb.push_str("\n\n");
        }
        sb.trim_end().to_string()
    }
}

const TOLERANCE: Time = Time::from_ms(1);
const AUDIO_PRE_SEEK: Time = Time::from_secs(3);
/// How far before a cut to seek in containers with unreliable seeking.
const PRE_ROLL: Time = Time::from_secs(10);

fn prefix() -> Vec<String> {
    let mut p: Vec<String> = QUIET.iter().map(|s| s.to_string()).collect();
    p.extend(["-nostats", "-stats_period", "0.5", "-progress", "pipe:1", "-y"].map(String::from));
    p
}

fn strs(items: &[&str]) -> Vec<String> {
    items.iter().map(|s| s.to_string()).collect()
}

struct Item {
    clip: Clip,
    source: SourceMedia,
    media: MediaInfo,
    video: VideoStreamInfo,
    /// What the clip plays (an audio file dropped on it, or its source), or None for silence.
    heard: Option<HeardAudio>,
}

impl Item {
    fn audio(&self) -> Option<&AudioStreamInfo> {
        self.heard.as_ref().map(|h| &h.stream)
    }

    /// The clip plays its video's own audio (none dropped on it). Replaced audio is always re-encoded.
    fn plays_own_audio(&self) -> bool {
        self.clip.replaced_audio.is_none()
    }

    /// The clip plays its own audio, not slipped: the audio can be copied along with the video.
    fn own_audio_in_sync(&self) -> bool {
        self.plays_own_audio() && self.clip.audio_offset == Time::ZERO
    }
}

struct Context<'a> {
    job: &'a ExportJob,
    args: PresetArguments,
    fallback: Vec<String>,
    items: Vec<Item>,
    /// The joined picture, which the crop is a part of.
    frame: OutputFrame,
    crop: Option<CropRect>,
}

impl Context<'_> {
    fn out(&self) -> &str {
        &self.job.output_path
    }

    fn any_audio(&self) -> bool {
        self.items.iter().any(|i| i.audio().is_some())
    }

    fn crop_pixels(&self) -> Option<PixelCrop> {
        self.crop.map(|c| self.frame.pixels(&c))
    }

    /// The filters for the finished picture: the crop, then the preset's own -vf.
    fn video_filter(&self) -> Option<String> {
        match (self.crop_pixels(), &self.args.video_filter) {
            (Some(p), Some(f)) => Some(format!("{},{f}", p.filter())),
            (Some(p), None) => Some(p.filter()),
            (None, f) => f.clone(),
        }
    }
}

pub fn plan(job: &ExportJob) -> Result<ExportPlan, ExportPlanError> {
    let clips = job.state.timeline.clips();
    if clips.is_empty() {
        return fail("The timeline is empty.");
    }
    let analysis = PresetAnalysis::of(&job.preset);
    if !analysis.is_valid() {
        return fail(format!("Preset \"{}\": {}", job.preset.name, analysis.errors[0]));
    }
    let args = analysis.arguments.clone().unwrap();
    let fallback = analysis.fallback.clone();
    let items: Vec<Item> = clips.iter().map(|c| resolve(job, c, &args)).collect::<Result<_, _>>()?;
    for item in &items {
        let mut files = vec![&item.source];
        if let Some(a) = job.state.audio_source_of(&item.clip)
            && a != &item.source
        {
            files.push(a);
        }
        for file in files {
            if same_file(&file.path, &job.output_path) {
                return fail(format!(
                    "The output file is one of the {}. Choose another name.",
                    if file == &item.source { "source videos" } else { "audio files" }
                ));
            }
        }
    }
    let crop = job.state.crop();
    if crop.is_some() && args.video_copy() {
        return fail(format!(
            "Preset \"{}\" copies the video, and a copied picture can't be cropped. Choose an encoding preset or reset the crop.",
            job.preset.name
        ));
    }
    let videos: Vec<&VideoStreamInfo> = items.iter().map(|i| &i.video).collect();
    let frame = OutputFrame::of(&videos);
    let ctx = Context { job, args, fallback, items, frame, crop };
    let mut plan = if ctx.items.len() == 1 && is_whole_file(&ctx.items[0]) {
        passthrough(&ctx)
    } else if ctx.args.video_copy() {
        stream_copy(&ctx)?
    } else if ctx.items.len() == 1 && ctx.args.audio_copy() && ctx.items[0].audio().is_some() {
        single_clip_audio_copy(&ctx).unwrap_or_else(|| encode(&ctx))
    } else {
        encode(&ctx)
    };
    let mut notes = ctx.args.warnings.clone();
    notes.append(&mut plan.notes);
    if let Some(p) = ctx.crop_pixels() {
        notes.push(format!(
            "Cropped to {}×{}, from {},{} of the {}×{} frame.",
            p.width, p.height, p.x, p.y, ctx.frame.width, ctx.frame.height
        ));
    }
    plan.notes = notes;
    plan.output_path = job.output_path.clone();
    plan.work_directory = job.work_directory.clone();
    Ok(plan)
}

fn resolve(job: &ExportJob, clip: &Clip, args: &PresetArguments) -> Result<Item, ExportPlanError> {
    let Some(source) = job.state.source(&clip.source_id) else {
        return fail("A clip refers to a source that is no longer in the project.");
    };
    let Some(media) = job.media.get(&source.id) else {
        return fail(format!("\"{}\" is missing or hasn't been analyzed yet.", source.display_name()));
    };
    let Some(video) = media.video.clone() else {
        return fail(format!("\"{}\" has no video stream.", source.display_name()));
    };
    let Some(replaced) = &clip.replaced_audio else {
        let heard = if args.no_audio {
            None
        } else {
            media.audio(source.audio_stream_index).map(|own| HeardAudio { file: source.clone(), info: media.clone(), stream: own.clone() })
        };
        return Ok(Item { clip: clip.clone(), source: source.clone(), media: media.clone(), video, heard });
    };
    let Some(file) = job.state.audio_file(&replaced.file_id) else {
        return fail("A clip plays an audio file that is no longer in the project.");
    };
    let Some(file_media) = job.media.get(&file.id) else {
        return fail(format!("\"{}\" is missing or hasn't been analyzed yet.", file.display_name()));
    };
    let stream = file_media.audio(file.audio_stream_index);
    if stream.is_none() && !args.no_audio {
        return fail(format!("\"{}\" has no audio stream.", file.display_name()));
    }
    let heard = if args.no_audio {
        None
    } else {
        Some(HeardAudio { file: file.clone(), info: file_media.clone(), stream: stream.unwrap().clone() })
    };
    Ok(Item { clip: clip.clone(), source: source.clone(), media: media.clone(), video, heard })
}

fn is_whole_file(item: &Item) -> bool {
    item.clip.r#in <= TOLERANCE && item.clip.out >= item.media.duration - TOLERANCE && item.own_audio_in_sync()
}

// ---- Passthrough -----------------------------------------------------------------------------------

fn passthrough(ctx: &Context) -> ExportPlan {
    let item = &ctx.items[0];
    let mut a = prefix();
    a.extend(["-i".into(), item.source.path.clone(), "-map".into(), format!("0:{}", item.video.index)]);
    if let Some(audio) = item.audio() {
        a.extend(["-map".into(), format!("0:{}", audio.index)]);
    }
    if let Some(vf) = ctx.video_filter() {
        a.extend(["-vf".into(), vf]);
    }
    if let (Some(af), Some(_)) = (&ctx.args.audio_filter, item.audio()) {
        a.extend(["-af".into(), af.clone()]);
    }
    a.extend(ctx.args.output_tokens.iter().cloned());
    a.extend(container_flags(ctx, is_hevc(&ctx.args, item)));
    a.push(ctx.out().into());
    let note =
        if ctx.crop.is_none() { "Whole file, no edits: the preset runs unchanged." } else { "Whole file, cropped: the preset runs on it." };
    ExportPlan::new(
        ExportMode::Passthrough,
        vec![ExportStep::new("Exporting", a, item.media.duration, true)],
        vec![],
        item.media.duration,
        vec![note.into()],
    )
}

// ---- Encode ----------------------------------------------------------------------------------------

fn encode(ctx: &Context) -> ExportPlan {
    let mut notes = Vec::new();
    let mut inputs = Vec::new();
    let mut graph = String::new();
    let norm = video_normalization(ctx);
    let audio_format = audio_format(&ctx.items);
    let n = ctx.items.len();
    for (i, item) in ctx.items.iter().enumerate() {
        let trim = input_item(&mut inputs, item, item.clip.r#in, item.clip.duration());
        graph.push_str(&format!(
            "[{i}:{}]trim={trim}duration={},setpts=PTS-STARTPTS{norm}[v{i}];",
            item.video.index,
            seconds(item.clip.duration())
        ));
    }
    let mut audio_input = n;
    if ctx.any_audio() {
        for (i, item) in ctx.items.iter().enumerate() {
            let chain =
                audio_chain(item, item.clip.r#in, item.clip.duration(), audio_format.as_ref().unwrap(), &mut audio_input, &mut inputs);
            graph.push_str(&format!("{chain}[a{i}];"));
        }
    }
    append_concat(&mut graph, n, true, ctx.any_audio(), ctx.video_filter().as_deref(), ctx.args.audio_filter.as_deref(), "aout");
    let mut a = prefix();
    a.extend(inputs);
    a.extend(["-filter_complex".into(), graph, "-map".into(), "[vout]".into()]);
    if ctx.any_audio() {
        a.extend(strs(&["-map", "[aout]"]));
    }
    let reason = audio_reencode_reason(ctx, "the edit has cuts");
    a.extend(audio_tokens(ctx, ctx.any_audio(), &mut notes, reason));
    a.extend(container_flags(ctx, is_hevc(&ctx.args, &ctx.items[0])));
    a.push(ctx.out().into());
    if !norm.is_empty() {
        notes.push(format!(
            "Clips differ in size, frame rate or pixel format: all are converted to {}×{} like the first clip.",
            ctx.frame.width, ctx.frame.height
        ));
    }
    let duration: Time = ctx.items.iter().map(|i| i.clip.duration()).sum();
    ExportPlan::new(ExportMode::Encode, vec![ExportStep::new("Exporting", a, duration, true)], vec![], duration, notes)
}

// ---- Stream copy ------------------------------------------------------------------------------------

fn stream_copy(ctx: &Context) -> Result<ExportPlan, ExportPlanError> {
    let mut notes = Vec::new();
    let first = &ctx.items[0];
    for item in &ctx.items[1..] {
        let v = &item.video;
        let f = &first.video;
        if v.codec != f.codec || v.width != f.width || v.height != f.height || v.pixel_format != f.pixel_format || v.index != f.index {
            return fail(format!(
                "Stream copy can only join videos with the same format, but \"{}\" ({} {}×{}) differs from \"{}\" ({} {}×{}). Use an encoding preset.",
                item.source.display_name(),
                v.codec,
                v.width,
                v.height,
                first.source.display_name(),
                f.codec,
                f.width,
                f.height
            ));
        }
    }
    // Effective in points: the keyframe at or before each in point.
    let mut starts = Vec::new();
    let mut max_shift = Time::ZERO;
    let mut shifted = 0;
    for item in &ctx.items {
        let Some(k) = ctx.job.keyframes.get(&item.source.id).filter(|k| !k.times.is_empty()) else {
            return fail(format!("Keyframes of \"{}\" aren't known yet.", item.source.display_name()));
        };
        let start = keyframe_at_or_before(&k.times, item.clip.r#in);
        if item.clip.r#in - start > TOLERANCE {
            shifted += 1;
            max_shift = max_shift.max(item.clip.r#in - start);
        }
        starts.push(start);
    }
    if shifted > 0 {
        notes.push(format!(
            "Stream copy cuts at keyframes: {shifted} clip(s) start earlier, by up to {} s. Turn on keyframe snapping to see exact cuts.",
            trim_number(max_shift.total_seconds(), 3)
        ));
    }
    let all_have_audio = ctx.items.iter().all(|i| i.audio().is_some());
    let same_audio = all_have_audio
        && ctx.items.iter().all(|i| {
            let (a, f) = (i.audio().unwrap(), first.audio().unwrap());
            a.codec == f.codec && a.sample_rate == f.sample_rate && a.channels == f.channels && a.index == f.index
        });
    let copy_audio = ctx.any_audio() && ctx.args.audio_copy() && same_audio && ctx.items.iter().all(|i| i.own_audio_in_sync());

    // Where the container seeks unreliably (MPEG-TS…), the concat demuxer's inpoint lands early, so each clip is
    // first cut into an exact temporary segment and the segments are joined.
    let segmented = ctx.items.iter().any(|i| i.media.has_unreliable_seeking());
    // How long a segment runs before its keyframe: with copied audio, it starts at the keyframe's decode time, the
    // reorder delay earlier, but no earlier than the file. The durations in the list make up for it.
    let leads: Vec<Time> = ctx
        .items
        .iter()
        .enumerate()
        .map(|(i, item)| {
            let delay = ctx.job.keyframes[&item.source.id].reorder_delay;
            if segmented && copy_audio { if starts[i] < delay { starts[i] } else { delay } } else { Time::ZERO }
        })
        .collect();
    let mut steps = Vec::new();
    let mut list = String::from("ffconcat version 1.0\n");
    let work = Path::new(&ctx.job.work_directory);
    let n = ctx.items.len();
    for (i, item) in ctx.items.iter().enumerate() {
        let length = item.clip.out - starts[i];
        let delay = ctx.job.keyframes[&item.source.id].reorder_delay;
        let to_end = item.clip.out >= item.media.duration - TOLERANCE;
        if segmented {
            let segment = work.join(format!("segment{i:03}.mkv")).to_string_lossy().into_owned();
            let seek = if starts[i] > PRE_ROLL { starts[i] - PRE_ROLL } else { Time::ZERO };
            let mut cut = prefix();
            cut.extend([
                "-ss".into(),
                seconds(seek),
                "-i".into(),
                item.source.path.clone(),
                "-map".into(),
                format!("0:{}", item.video.index),
            ]);
            // Output-side seek and length compare decode timestamps; the keyframe decodes "delay" early.
            let mut cut_start = starts[i] - delay - seek;
            let mut cut_length = length;
            if copy_audio {
                cut.extend(["-map".into(), format!("0:{}", item.audio().unwrap().index)]);
            } else {
                // With video alone, ffmpeg counts output time from the video's first timestamp rather than the
                // container start, and a seek even slightly past the keyframe drops its whole GOP. Seek by that
                // difference and half a frame earlier; the frame before is no keyframe and isn't copied.
                let margin = Time(time::frame_duration(item.video.frame_rate).0 / 2);
                cut_start -= item.video.start_offset + margin;
                cut_length += margin;
            }
            cut.extend(["-c".into(), "copy".into(), "-ss".into(), seconds(cut_start)]);
            if !to_end {
                cut.extend(["-t".into(), seconds(cut_length)]);
            }
            cut.extend(["-f".into(), "matroska".into(), segment.clone()]);
            steps.push(ExportStep::new(format!("Cutting clip {} of {n}", i + 1), cut, length, false));
            list.push_str(&format!("file {}\n", concat_quote(&segment)));
        } else {
            list.push_str(&format!("file {}\n", concat_quote(&item.source.path)));
            list.push_str(&format!("inpoint {}\n", seconds(starts[i] + item.media.start_time)));
            // The concat demuxer stops at a *decode* timestamp; with B-frames the next GOP's keyframe is decoded
            // before the cut, so stop earlier by the reorder delay. A clip ending at the file end runs to the end.
            if !to_end {
                list.push_str(&format!("outpoint {}\n", seconds(item.clip.out - delay + item.media.start_time)));
            }
        }
        // Explicit length, so the next segment starts at the real cut and not at the earlier outpoint.
        let next_lead = if i + 1 < n { leads[i + 1] } else { leads[i] };
        list.push_str(&format!("duration {}\n", seconds(length + leads[i] - next_lead)));
    }
    let list_path = work.join("segments.ffconcat").to_string_lossy().into_owned();
    let video_map = if segmented { "0:0".to_string() } else { format!("0:{}", first.video.index) };
    let audio_map =
        if segmented { "0:1".to_string() } else { format!("0:{}", first.audio().map(|a| a.index.to_string()).unwrap_or_default()) };

    // Audio that isn't copied goes through a filter graph, shifted like the concat input (see ConcatShift).
    let filtered_audio = !copy_audio && ctx.any_audio();
    let mut inputs = Vec::new();
    let mut graph = String::new();
    let mut output_tokens = ctx.args.output_tokens.clone();
    if filtered_audio {
        let format = audio_format(&ctx.items).unwrap();
        let mut audio_input = 1;
        for (i, item) in ctx.items.iter().enumerate() {
            let chain = audio_chain(item, starts[i], item.clip.out - starts[i], &format, &mut audio_input, &mut inputs);
            graph.push_str(&format!("{chain}[a{i}];"));
        }
        append_concat(&mut graph, n, false, true, None, ctx.args.audio_filter.as_deref(), "ashift");
        let reason =
            audio_reencode_reason(ctx, if all_have_audio { "the clips' audio formats differ" } else { "some clips have no audio" });
        output_tokens = audio_tokens(ctx, true, &mut notes, reason);
    }
    let mut a = prefix();
    a.extend(strs(&["-f", "concat", "-safe", "0", "-i"]));
    a.push(list_path.clone());
    if filtered_audio {
        a.extend(inputs);
        a.extend([
            "-filter_complex".into(),
            format!("{graph};[ashift]asetpts=PTS+{SHIFT_PLACEHOLDER}/TB[aout]"),
            "-map".into(),
            video_map,
            "-map".into(),
            "[aout]".into(),
        ]);
    } else {
        a.extend(["-map".into(), video_map]);
        if copy_audio {
            a.extend(["-map".into(), audio_map]);
        }
    }
    a.extend(output_tokens);
    a.extend(container_flags(ctx, is_hevc(&ctx.args, first)));
    a.push(ctx.out().into());
    let duration: Time = ctx.items.iter().enumerate().map(|(i, it)| it.clip.out - starts[i]).sum();
    let mut join = ExportStep::new(if segmented { "Joining" } else { "Copying" }, a, duration, true);
    join.shift = filtered_audio.then(|| ConcatShift { list_path: list_path.clone() });
    steps.push(join);
    Ok(ExportPlan::new(ExportMode::StreamCopy, steps, vec![PlannedFile { path: list_path, content: list }], duration, notes))
}

pub fn keyframe_at_or_before(keyframes: &[Time], time: Time) -> Time {
    let mut best = keyframes[0];
    for &k in keyframes {
        if k > time + TOLERANCE {
            break;
        }
        best = k;
    }
    if best > time { time } else { best }
}

// ---- Single clip, lossless audio -------------------------------------------------------------------

fn single_clip_audio_copy(ctx: &Context) -> Option<ExportPlan> {
    let item = &ctx.items[0];
    let heard = item.heard.as_ref()?;
    let clip = &item.clip;
    if !item.plays_own_audio() {
        return None;
    }
    let AudioLayout::Placed { placement, .. } = heard.layout(clip) else { return None };
    if placement.is_silent() {
        return None;
    }
    let (lead, read_from, read_length) = (placement.lead, placement.read_from, placement.read_length);
    let pre_seek = if read_from > AUDIO_PRE_SEEK { read_from - AUDIO_PRE_SEEK } else { Time::ZERO };
    let tmp = Path::new(&ctx.job.work_directory).join("audio.mka").to_string_lossy().into_owned();
    let mut extract = prefix();
    extract.extend([
        "-ss".into(),
        seconds(pre_seek),
        "-i".into(),
        heard.file.path.clone(),
        "-map".into(),
        format!("0:{}", heard.stream.index),
        "-c".into(),
        "copy".into(),
        "-ss".into(),
        seconds(read_from - pre_seek),
        "-t".into(),
        seconds(read_length),
        "-f".into(),
        "matroska".into(),
        tmp.clone(),
    ]);
    let mut mux = prefix();
    let trim = input_item(&mut mux, item, clip.r#in, clip.duration());
    if lead > Time::ZERO {
        mux.extend(["-itsoffset".into(), seconds(lead)]);
    }
    mux.extend(["-i".into(), tmp, "-map".into(), format!("0:{}", item.video.index), "-map".into(), "1:a:0".into()]);
    let notes = vec!["Audio is copied without re-encoding (cut separately, then muxed).".to_string()];
    let mut video_filters = Vec::new();
    if !trim.is_empty() {
        video_filters.push(format!("trim={trim}duration={},setpts=PTS-STARTPTS", seconds(clip.duration())));
    }
    if let Some(vf) = ctx.video_filter() {
        video_filters.push(vf);
    }
    if !video_filters.is_empty() {
        mux.extend(["-vf".into(), video_filters.join(",")]);
    }
    mux.extend(ctx.args.output_tokens.iter().cloned());
    mux.extend(container_flags(ctx, is_hevc(&ctx.args, item)));
    mux.extend(["-t".into(), seconds(clip.duration()), ctx.out().into()]);
    Some(ExportPlan::new(
        ExportMode::SingleClipAudioCopy,
        vec![ExportStep::new("Cutting audio", extract, read_length, false), ExportStep::new("Exporting", mux, clip.duration(), true)],
        vec![],
        clip.duration(),
        notes,
    ))
}

// ---- Shared pieces ---------------------------------------------------------------------------------

struct AudioFormat {
    rate: i32,
    layout: String,
}

/// Looped audio that repeats at most this many times is read piece by piece; shorter files that repeat more often
/// are repeated in the graph, which holds one whole file of decoded audio in memory.
const MAX_LOOP_PIECES: usize = 16;

/// Audio for one clip: the slipped range of the heard file, padded with silence where it runs past the file (or
/// looped), and trimmed to exactly `length` so clips never drift. Adds an input when audio is read.
fn audio_chain(
    item: &Item,
    video_start: Time,
    length: Time,
    format: &AudioFormat,
    next_input: &mut usize,
    inputs: &mut Vec<String>,
) -> String {
    let fmt = format!("aformat=sample_rates={}:channel_layouts={}", format.rate, format.layout);
    let len = seconds(length);
    let silence = format!("anullsrc=r={}:cl={},atrim=duration={len},asetpts=N/SR/TB", format.rate, format.layout);
    let Some(heard) = &item.heard else { return silence };
    let layout = heard.layout_range(&item.clip, video_start, length);
    if let AudioLayout::Looped(lp) = layout
        && lp.wraps()
    {
        let pieces = lp.pieces();
        return if pieces.len() <= MAX_LOOP_PIECES || samples(lp.period, format.rate) > i32::MAX as i64 {
            loop_by_pieces(heard, &pieces, &fmt, format.rate, next_input, inputs)
        } else {
            loop_by_repeating(heard, &lp, length, &fmt, format.rate, next_input, inputs)
        };
    }
    // Looped audio that doesn't wrap is one plain read inside some repetition of the file.
    let placement = match layout {
        AudioLayout::Looped(lp) => lp.as_placement(),
        AudioLayout::Placed { placement, .. } => placement,
    };
    if placement.is_silent() {
        return silence;
    }
    let trim = input(inputs, &heard.file.path, &heard.info, placement.read_from, placement.read_length);
    let mut chain = format!("[{next_input}:{}]", heard.stream.index);
    if !trim.is_empty() {
        chain.push_str(&format!("atrim={trim}duration={},asetpts=PTS-STARTPTS,", seconds(placement.read_length)));
    }
    chain.push_str(&fmt);
    *next_input += 1;
    if placement.lead > Time::ZERO {
        chain.push_str(&format!(",adelay=delays={}S:all=1", samples(placement.lead, format.rate)));
    }
    chain.push_str(&format!(",apad=whole_dur={len},atrim=duration={len},asetpts=N/SR/TB"));
    chain
}

/// Each piece of the loop read on its own and cut to the samples between its start and end in the stretch, then
/// joined: sample-exact seams that never drift, and memory that doesn't grow with the file's length.
fn loop_by_pieces(
    heard: &HeardAudio,
    pieces: &[(Time, Time)],
    fmt: &str,
    rate: i32,
    next_input: &mut usize,
    inputs: &mut Vec<String>,
) -> String {
    let mut graph = String::new();
    let mut labels = String::new();
    let mut at = Time::ZERO;
    for &(read_from, length) in pieces {
        let n = samples(at + length, rate) - samples(at, rate);
        at += length;
        let trim = input(inputs, &heard.file.path, &heard.info, read_from, length);
        graph.push_str(&format!("[{next_input}:{}]", heard.stream.index));
        if !trim.is_empty() {
            graph.push_str(&format!("atrim={trim}duration={},asetpts=PTS-STARTPTS,", seconds(length)));
        }
        graph.push_str(&format!("{fmt},apad=whole_len={n},atrim=end_sample={n}[lp{next_input}];"));
        labels.push_str(&format!("[lp{next_input}]"));
        *next_input += 1;
    }
    format!("{graph}{labels}concat=n={}:v=0:a=1,asetpts=N/SR/TB", pieces.len())
}

/// One whole period of the file, padded or cut to exactly that many samples, repeated sample-exactly.
fn loop_by_repeating(
    heard: &HeardAudio,
    lp: &AudioLoop,
    length: Time,
    fmt: &str,
    rate: i32,
    next_input: &mut usize,
    inputs: &mut Vec<String>,
) -> String {
    let period = samples(lp.period, rate);
    let phase = samples(lp.phase, rate);
    let loop_trim = input(inputs, &heard.file.path, &heard.info, Time::ZERO, lp.period);
    let mut looped = format!("[{next_input}:{}]", heard.stream.index);
    if !loop_trim.is_empty() {
        looped.push_str(&format!("atrim={loop_trim}duration={},asetpts=PTS-STARTPTS,", seconds(lp.period)));
    }
    looped.push_str(&format!("{fmt},apad=whole_dur={},atrim=end_sample={period},aloop=loop=-1:size={period}", seconds(lp.period)));
    looped.push_str(&format!(",atrim=start_sample={phase}:end_sample={},asetpts=N/SR/TB", phase + samples(length, rate)));
    *next_input += 1;
    looped
}

fn samples(t: Time, rate: i32) -> i64 {
    (t.total_seconds() * rate as f64).round_ties_even() as i64
}

fn input_item(inputs: &mut Vec<String>, item: &Item, start: Time, length: Time) -> String {
    input(inputs, &item.source.path, &item.media, start, length)
}

/// Adds an input reading [start, start+length) of a file. Where seeking is unreliable it seeks `PRE_ROLL` early and
/// returns the "start=…:" part of a trim that cuts exactly. Audio-only files are read from their start and trimmed
/// in the graph: seeking in MP3 or ADTS lands on estimated timestamps, and decoding audio up to the cut is cheap.
fn input(inputs: &mut Vec<String>, path: &str, media: &MediaInfo, start: Time, length: Time) -> String {
    if media.video.is_none() {
        inputs.extend(["-t".into(), seconds(start + length), "-i".into(), path.into()]);
        return if start > Time::ZERO { format!("start={}:", seconds(start)) } else { String::new() };
    }
    if !media.has_unreliable_seeking() {
        inputs.extend(["-ss".into(), seconds(start), "-t".into(), seconds(length), "-i".into(), path.into()]);
        return String::new();
    }
    let seek = if start > PRE_ROLL { start - PRE_ROLL } else { Time::ZERO };
    inputs.extend(["-ss".into(), seconds(seek), "-t".into(), seconds(start - seek + length), "-i".into(), path.into()]);
    format!("start={}:", seconds(start - seek))
}

/// Joins the clips' [vN] and [aN], then runs `video_filter` and `audio_filter` on the result.
fn append_concat(
    graph: &mut String,
    n: usize,
    video: bool,
    audio: bool,
    video_filter: Option<&str>,
    audio_filter: Option<&str>,
    audio_out: &str,
) {
    for i in 0..n {
        if video {
            graph.push_str(&format!("[v{i}]"));
        }
        if audio {
            graph.push_str(&format!("[a{i}]"));
        }
    }
    let v_label = if video_filter.is_none() { "vout" } else { "vcat" };
    let a_label = if audio_filter.is_none() { audio_out } else { "acat" };
    graph.push_str(&format!("concat=n={n}:v={}:a={}", video as i32, audio as i32));
    if video {
        graph.push_str(&format!("[{v_label}]"));
    }
    if audio {
        graph.push_str(&format!("[{a_label}]"));
    }
    if let (true, Some(f)) = (video, video_filter) {
        graph.push_str(&format!(";[vcat]{f}[vout]"));
    }
    if let (true, Some(f)) = (audio, audio_filter) {
        graph.push_str(&format!(";[acat]{f}[{audio_out}]"));
    }
}

/// Why audio can't be copied: replaced or slipped audio first, else `otherwise`.
fn audio_reencode_reason<'a>(ctx: &Context, otherwise: &'a str) -> &'a str {
    if ctx.items.iter().any(|i| !i.plays_own_audio()) {
        "clips have replaced audio"
    } else if ctx.items.iter().any(|i| !i.own_audio_in_sync()) {
        "clips have audio offsets"
    } else {
        otherwise
    }
}

/// Preset output options, with "-c:a copy" swapped for the fallback when audio is filtered.
fn audio_tokens(ctx: &Context, audio_filtered: bool, notes: &mut Vec<String>, reason: &str) -> Vec<String> {
    if !audio_filtered || !ctx.args.audio_copy() {
        return ctx.args.output_tokens.clone();
    }
    notes.push(format!(
        "Audio is re-encoded with \"{}\" because {reason} (stream copy isn't possible).",
        ctx.job.preset.audio_fallback_arguments
    ));
    ctx.args.with_audio_codec_replaced(&ctx.fallback)
}

/// Filters that make every clip match the first one, or "" when they already match.
fn video_normalization(ctx: &Context) -> String {
    let r = &ctx.items[0].video;
    let rate_differs = ctx.items.iter().any(|i| !i.video.frame_rate.equivalent(r.frame_rate));
    let format_differs = ctx.items.iter().any(|i| i.video.pixel_format != r.pixel_format) && !r.pixel_format.is_empty();
    let mut sb = String::new();
    if rate_differs {
        sb.push_str(&format!(",fps={}", r.frame_rate));
    }
    if ctx.frame.normalized {
        sb.push(',');
        sb.push_str(&ctx.frame.fit_filter());
    }
    if format_differs {
        sb.push_str(&format!(",format={}", r.pixel_format));
    }
    sb
}

/// The format every clip's audio is converted to: the highest sample rate and the widest channel layout among the
/// clips, so a low-rate or mono audio file on one clip never downgrades the others.
fn audio_format(items: &[Item]) -> Option<AudioFormat> {
    let streams: Vec<&AudioStreamInfo> = items.iter().filter_map(|i| i.audio()).collect();
    if streams.is_empty() {
        return None;
    }
    // The first of the widest, as MaxBy picks.
    let mut widest = streams[0];
    for s in &streams {
        if s.channels > widest.channels {
            widest = s;
        }
    }
    Some(AudioFormat { rate: streams.iter().map(|a| a.sample_rate).max().unwrap(), layout: widest.effective_layout() })
}

fn container_flags(ctx: &Context, hevc: bool) -> Vec<String> {
    let ext = Path::new(ctx.out()).extension().map(|e| e.to_string_lossy().to_lowercase()).unwrap_or_default();
    let mut out = Vec::new();
    if !matches!(ext.as_str(), "mp4" | "m4v" | "mov") {
        return out;
    }
    if !ctx.args.sets_mov_flags {
        out.extend(strs(&["-movflags", "+faststart"]));
    }
    if hevc && !ctx.args.sets_video_tag {
        out.extend(strs(&["-tag:v", "hvc1"]));
    }
    out
}

fn is_hevc(args: &PresetArguments, first: &Item) -> bool {
    match &args.video_codec {
        Some(c) if c == "copy" => first.video.codec == "hevc",
        Some(c) => c.contains("265") || c.contains("hevc"),
        None => false,
    }
}

/// Whether two paths name the same file: compared after resolving symbolic links, and ignoring case on macOS, whose
/// usual file systems treat "clip.mp4" and "clip.MP4" as one file.
fn same_file(a: &str, b: &str) -> bool {
    let (a, b) = (real_path(a), real_path(b));
    if cfg!(target_os = "macos") { a.to_string_lossy().to_lowercase() == b.to_string_lossy().to_lowercase() } else { a == b }
}

fn real_path(path: &str) -> PathBuf {
    let full = std::path::absolute(path).unwrap_or_else(|_| PathBuf::from(path));
    if let Ok(c) = std::fs::canonicalize(&full) {
        return c;
    }
    match (full.parent().and_then(|p| std::fs::canonicalize(p).ok()), full.file_name()) {
        (Some(dir), Some(name)) => dir.join(name),
        _ => full,
    }
}

fn concat_quote(path: &str) -> String {
    format!("'{}'", path.replace('\'', "'\\''"))
}
