//! Time as 100 ns ticks (the C# `TimeSpan` the project files and caches use), plus formatting helpers shared by
//! command builders and the UI.

use super::rational::Rational;
use serde::{Deserialize, Deserializer, Serialize, Serializer};
use std::fmt;
use std::ops::{Add, AddAssign, Div, Mul, Neg, Sub, SubAssign};

pub const TICKS_PER_SECOND: i64 = 10_000_000;
pub const TICKS_PER_MS: i64 = 10_000;

/// A signed duration or position in ticks of 100 ns.
#[derive(Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Default)]
pub struct Time(pub i64);

impl Time {
    pub const ZERO: Time = Time(0);
    pub const MAX: Time = Time(i64::MAX);
    pub const MIN: Time = Time(i64::MIN);

    pub const fn ticks(self) -> i64 {
        self.0
    }
    pub const fn from_ticks(t: i64) -> Time {
        Time(t)
    }
    pub const fn from_ms(ms: i64) -> Time {
        Time(ms * TICKS_PER_MS)
    }
    pub const fn from_secs(s: i64) -> Time {
        Time(s * TICKS_PER_SECOND)
    }
    pub fn from_ms_f(ms: f64) -> Time {
        Time((ms * TICKS_PER_MS as f64).round_ties_even() as i64)
    }
    /// Rounded to whole ticks.
    pub fn from_seconds(seconds: f64) -> Time {
        Time((seconds * TICKS_PER_SECOND as f64).round_ties_even() as i64)
    }
    pub fn from_minutes(m: i64) -> Time {
        Time(m * 60 * TICKS_PER_SECOND)
    }
    pub fn total_seconds(self) -> f64 {
        self.0 as f64 / TICKS_PER_SECOND as f64
    }
    pub fn total_ms(self) -> f64 {
        self.0 as f64 / TICKS_PER_MS as f64
    }
    pub fn abs(self) -> Time {
        Time(self.0.abs())
    }
    pub fn min(self, o: Time) -> Time {
        Time(self.0.min(o.0))
    }
    pub fn max(self, o: Time) -> Time {
        Time(self.0.max(o.0))
    }
    pub fn clamp(self, lo: Time, hi: Time) -> Time {
        Time(self.0.max(lo.0).min(hi.0))
    }
    pub fn mul_f(self, f: f64) -> Time {
        Time((self.0 as f64 * f).round_ties_even() as i64)
    }
}

impl fmt::Debug for Time {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}s", seconds(*self))
    }
}

impl Add for Time {
    type Output = Time;
    fn add(self, o: Time) -> Time {
        Time(self.0 + o.0)
    }
}
impl Sub for Time {
    type Output = Time;
    fn sub(self, o: Time) -> Time {
        Time(self.0 - o.0)
    }
}
impl AddAssign for Time {
    fn add_assign(&mut self, o: Time) {
        self.0 += o.0
    }
}
impl SubAssign for Time {
    fn sub_assign(&mut self, o: Time) {
        self.0 -= o.0
    }
}
impl Neg for Time {
    type Output = Time;
    fn neg(self) -> Time {
        Time(-self.0)
    }
}
impl Mul<i64> for Time {
    type Output = Time;
    fn mul(self, k: i64) -> Time {
        Time(self.0 * k)
    }
}
/// The ratio of two times.
impl Div for Time {
    type Output = f64;
    fn div(self, o: Time) -> f64 {
        self.0 as f64 / o.0 as f64
    }
}
impl std::iter::Sum for Time {
    fn sum<I: Iterator<Item = Time>>(iter: I) -> Time {
        Time(iter.map(|t| t.0).sum())
    }
}

/// Serialized as a .NET TimeSpan string ("[-][d.]hh:mm:ss[.fffffff]"), as System.Text.Json writes it.
impl Serialize for Time {
    fn serialize<S: Serializer>(&self, s: S) -> Result<S::Ok, S::Error> {
        s.serialize_str(&timespan_string(*self))
    }
}

impl<'de> Deserialize<'de> for Time {
    fn deserialize<D: Deserializer<'de>>(d: D) -> Result<Time, D::Error> {
        let text = String::deserialize(d)?;
        parse_timespan(&text).ok_or_else(|| serde::de::Error::custom(format!("invalid time span {text:?}")))
    }
}

/// "c" format of .NET TimeSpan: [-][d.]hh:mm:ss[.fffffff].
pub fn timespan_string(t: Time) -> String {
    let neg = t.0 < 0;
    let ticks = t.0.unsigned_abs();
    let frac = ticks % TICKS_PER_SECOND as u64;
    let total_s = ticks / TICKS_PER_SECOND as u64;
    let (d, h, m, s) = (total_s / 86_400, total_s / 3600 % 24, total_s / 60 % 60, total_s % 60);
    let mut out = String::new();
    if neg {
        out.push('-');
    }
    if d > 0 {
        out.push_str(&format!("{d}."));
    }
    out.push_str(&format!("{h:02}:{m:02}:{s:02}"));
    if frac > 0 {
        out.push_str(&format!(".{frac:07}"));
    }
    out
}

pub fn parse_timespan(text: &str) -> Option<Time> {
    let text = text.trim();
    let (neg, rest) = match text.strip_prefix('-') {
        Some(r) => (true, r),
        None => (false, text),
    };
    let parts: Vec<&str> = rest.split(':').collect();
    if parts.len() != 3 {
        return None;
    }
    let (days, hours) = match parts[0].split_once('.') {
        Some((d, h)) => (d.parse::<i64>().ok()?, h.parse::<i64>().ok()?),
        None => (0, parts[0].parse::<i64>().ok()?),
    };
    let minutes: i64 = parts[1].parse().ok()?;
    let (secs, frac) = match parts[2].split_once('.') {
        Some((s, f)) => {
            if f.is_empty() || f.len() > 7 || !f.bytes().all(|b| b.is_ascii_digit()) {
                return None;
            }
            (s.parse::<i64>().ok()?, format!("{f:0<7}").parse::<i64>().ok()?)
        }
        None => (parts[2].parse::<i64>().ok()?, 0),
    };
    let ticks = (((days * 24 + hours) * 60 + minutes) * 60 + secs) * TICKS_PER_SECOND + frac;
    Some(Time(if neg { -ticks } else { ticks }))
}

/// Seconds for ffmpeg/mpv arguments, floored to whole microseconds so a cut placed exactly on a frame boundary
/// never pulls in the neighbouring frame.
pub fn seconds(time: Time) -> String {
    let mut micros = time.0 / 10;
    if time.0 < 0 && time.0 % 10 != 0 {
        micros -= 1;
    }
    let sign = if micros < 0 { "-" } else { "" };
    let micros = micros.abs();
    format!("{sign}{}.{:06}", micros / 1_000_000, micros % 1_000_000)
}

/// "1:02:03.456" / "02:03.456" style display timecode.
pub fn display(time: Time, force_hours: bool) -> String {
    let sign = if time < Time::ZERO { "-" } else { "" };
    let ms = time.0.abs() / TICKS_PER_MS;
    let (h, m, s, f) = (ms / 3_600_000, ms / 60_000 % 60, ms / 1000 % 60, ms % 1000);
    if h > 0 || force_hours { format!("{sign}{h}:{m:02}:{s:02}.{f:03}") } else { format!("{sign}{m:02}:{s:02}.{f:03}") }
}

/// A number with up to `decimals` decimals, trailing zeros dropped (.NET's "0.##" format).
pub fn trim_number(value: f64, decimals: usize) -> String {
    let s = format!("{value:.decimals$}");
    if s.contains('.') { s.trim_end_matches('0').trim_end_matches('.').to_string() } else { s }
}

/// Signed milliseconds for offsets, e.g. "+120 ms"; seconds from 10 s up, e.g. "−83.5 s".
pub fn offset(offset: Time) -> String {
    let ms = offset.total_ms();
    if ms.abs() >= 10_000.0 {
        return format!("{}{} s", if ms > 0.0 { "+" } else { "−" }, trim_number(ms.abs() / 1000.0, 3));
    }
    if ms > 0.0 {
        format!("+{} ms", trim_number(ms, 1))
    } else if ms < 0.0 {
        format!("−{} ms", trim_number(-ms, 1))
    } else {
        "0 ms".into()
    }
}

/// Parses "1:02:03.5", "02:03.5", "123.5" or "123,5".
pub fn try_parse(text: &str) -> Option<Time> {
    let text = text.trim().replace(',', ".");
    if text.is_empty() {
        return None;
    }
    let (negative, text) = if let Some(r) = text.strip_prefix('-') {
        (true, r.to_string())
    } else if let Some(r) = text.strip_prefix('−') {
        (true, r.to_string())
    } else {
        (false, text)
    };
    let parts: Vec<&str> = text.split(':').collect();
    if parts.len() > 3 {
        return None;
    }
    let mut total = 0.0;
    for part in parts {
        let value = parse_float(part)?;
        if value < 0.0 {
            return None;
        }
        total = total * 60.0 + value;
    }
    Some(Time::from_seconds(if negative { -total } else { total }))
}

/// An invariant-culture float (digits, one point, optional exponent), as .NET's Float style accepts.
pub fn parse_float(s: &str) -> Option<f64> {
    let s = s.trim();
    if s.is_empty() || s.eq_ignore_ascii_case("nan") || s.to_ascii_lowercase().contains("inf") {
        return None;
    }
    s.parse::<f64>().ok()
}

fn ceil_div(a: i128, b: i128) -> i128 {
    let q = a / b;
    if (a % b != 0) && ((a < 0) == (b < 0)) { q + 1 } else { q }
}

/// Start time of frame `frame`, rounded up to whole ticks.
pub fn frame_start(frame: i64, rate: Rational) -> Time {
    let ticks = ceil_div(frame as i128 * rate.den as i128 * TICKS_PER_SECOND as i128, rate.num as i128);
    Time(ticks as i64)
}

/// Rounds a time to the nearest frame of the given rate; frames start at `phase`.
pub fn snap_to_frame(time: Time, rate: Rational, phase: Time) -> Time {
    if !rate.is_valid() {
        return time;
    }
    let frame = ((time - phase).0 as f64 * rate.num as f64 / (rate.den as f64 * TICKS_PER_SECOND as f64)).round_ties_even();
    let snapped = frame_start(frame as i64, rate) + phase;
    snapped.max(Time::ZERO)
}

pub fn frame_duration(rate: Rational) -> Time {
    if rate.is_valid() { Time(TICKS_PER_SECOND * rate.den / rate.num) } else { Time::from_ms(40) }
}
