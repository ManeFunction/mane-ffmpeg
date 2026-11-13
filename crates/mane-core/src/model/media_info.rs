use super::datetime::UtcTime;
use super::rational::Rational;
use super::time::Time;

/// A file's size and modification time: analyses cached for a file stay valid while they match.
#[derive(Clone, Copy, PartialEq, Eq, Hash, Default, Debug)]
pub struct FileStamp {
    pub size: i64,
    pub last_write_utc: UtcTime,
}

impl FileStamp {
    pub fn of(path: &str) -> FileStamp {
        match std::fs::metadata(path) {
            Ok(m) if m.is_file() => {
                FileStamp { size: m.len() as i64, last_write_utc: m.modified().map(UtcTime::from_system).unwrap_or_default() }
            }
            _ => FileStamp::default(),
        }
    }
}

impl std::fmt::Display for FileStamp {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}/{}", self.size, self.last_write_utc.0)
    }
}

/// What ffprobe told us about a source file. Times are 0-based from the file start.
#[derive(Clone, PartialEq, Debug, Default)]
pub struct MediaInfo {
    /// The file as it was when probed.
    pub stamp: FileStamp,
    pub duration: Time,
    /// Container start_time; file timestamps = 0-based time + start_time.
    pub start_time: Time,
    pub format_name: String,
    pub video: Option<VideoStreamInfo>,
    pub audio_streams: Vec<AudioStreamInfo>,
}

impl MediaInfo {
    pub fn audio(&self, audio_index: i32) -> Option<&AudioStreamInfo> {
        if audio_index >= 0 { self.audio_streams.get(audio_index as usize) } else { None }
    }

    /// Where `stream` ends; the container duration when the stream has none of its own.
    pub fn audio_end(&self, stream: &AudioStreamInfo) -> Time {
        stream.duration.unwrap_or(self.duration)
    }

    /// Containers whose timestamp seeking can land after the wanted keyframe (MPEG-TS/PS, AVI, FLV, raw
    /// streams). Exports seek a few seconds early there and trim precisely in the filter graph.
    pub fn has_unreliable_seeking(&self) -> bool {
        self.format_name.split(',').any(|f| matches!(f, "mpegts" | "mpeg" | "mpegvideo" | "avi" | "flv" | "asf" | "h264" | "hevc" | "m4v"))
    }
}

#[derive(Clone, PartialEq, Debug)]
pub struct VideoStreamInfo {
    /// Absolute stream index in the file (for "-map 0:N").
    pub index: i32,
    pub codec: String,
    pub width: i32,
    pub height: i32,
    pub frame_rate: Rational,
    pub pixel_format: String,
    pub sample_aspect_ratio: Rational,
    /// When the first video frame starts, relative to the container start (0-based time).
    pub start_offset: Time,
    /// Display rotation in degrees (from the display matrix), e.g. 90 for portrait phone video.
    pub rotation: i32,
}

impl Default for VideoStreamInfo {
    fn default() -> Self {
        VideoStreamInfo {
            index: 0,
            codec: String::new(),
            width: 0,
            height: 0,
            frame_rate: Rational::ZERO,
            pixel_format: String::new(),
            sample_aspect_ratio: Rational::ONE,
            start_offset: Time::ZERO,
            rotation: 0,
        }
    }
}

impl VideoStreamInfo {
    fn rotated(&self) -> bool {
        self.rotation.abs() % 180 == 90
    }

    /// The decoded frame's size once rotated upright, as filters see it (no square-pixel correction).
    pub fn upright_size(&self) -> (i32, i32) {
        if self.rotated() { (self.height, self.width) } else { (self.width, self.height) }
    }

    /// Width over height as shown: after square-pixel correction and rotation.
    pub fn display_aspect(&self) -> f64 {
        self.display_width() as f64 / self.display_height() as f64
    }

    fn sar_width(&self) -> i32 {
        let sar = self.sample_aspect_ratio;
        if sar.is_valid() && sar.num != sar.den { (self.width as f64 * sar.value()).round_ties_even() as i32 } else { self.width }
    }

    /// Width as shown after square-pixel correction and rotation, rounded to even.
    pub fn display_width(&self) -> i32 {
        even(if self.rotated() { self.height } else { self.sar_width() })
    }

    pub fn display_height(&self) -> i32 {
        even(if self.rotated() { self.sar_width() } else { self.height })
    }
}

fn even(value: i32) -> i32 {
    (value / 2 * 2).max(2)
}

#[derive(Clone, PartialEq, Debug, Default)]
pub struct AudioStreamInfo {
    /// Absolute stream index in the file.
    pub index: i32,
    pub codec: String,
    pub sample_rate: i32,
    pub channels: i32,
    pub channel_layout: String,
    pub language: Option<String>,
    pub title: Option<String>,
    pub duration: Option<Time>,
}

impl AudioStreamInfo {
    /// A layout name ffmpeg's aformat accepts, even when ffprobe reported none.
    pub fn effective_layout(&self) -> String {
        if !self.channel_layout.is_empty() && !self.channel_layout.contains('(') {
            return self.channel_layout.clone();
        }
        match self.channels {
            1 => "mono".into(),
            2 => "stereo".into(),
            6 => "5.1".into(),
            8 => "7.1".into(),
            n => format!("{}c", n.max(1)),
        }
    }

    pub fn describe(&self) -> String {
        let mut parts = vec![
            self.codec.clone(),
            format!("{}ch", self.channels),
            format!("{} kHz", super::time::trim_number(self.sample_rate as f64 / 1000.0, 1)),
        ];
        if let Some(l) = self.language.as_deref().filter(|l| !l.is_empty() && *l != "und") {
            parts.insert(0, l.to_string());
        }
        if let Some(t) = self.title.as_deref().filter(|t| !t.is_empty()) {
            parts.push(t.to_string());
        }
        parts.join(" · ")
    }
}
