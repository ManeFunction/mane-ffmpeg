//! Turns "-progress pipe:1" key=value lines into snapshots, one per "progress=" line.

use crate::model::Time;

/// A block of ffmpeg "-progress" output.
#[derive(Clone, Copy, PartialEq, Debug)]
pub struct FfmpegProgress {
    pub position: Time,
    pub speed: Option<f64>,
    pub finished: bool,
}

#[derive(Default)]
pub struct ProgressParser {
    position: Time,
    speed: Option<f64>,
}

impl ProgressParser {
    /// Returns a snapshot when the line ends a block.
    pub fn feed(&mut self, line: &str) -> Option<FfmpegProgress> {
        let eq = line.find('=')?;
        if eq == 0 {
            return None;
        }
        let key = line[..eq].trim();
        let value = line[eq + 1..].trim();
        match key {
            // out_time_ms is (historically) also microseconds.
            "out_time_us" | "out_time_ms" => {
                if let Ok(us) = value.parse::<i64>()
                    && us >= 0
                {
                    self.position = Time(us * 10);
                }
            }
            "speed" => {
                self.speed = crate::model::time::parse_float(value.trim_end_matches('x')).filter(|s| *s > 0.0);
            }
            "progress" => return Some(FfmpegProgress { position: self.position, speed: self.speed, finished: value == "end" }),
            _ => {}
        }
        None
    }
}
