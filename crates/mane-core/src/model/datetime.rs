//! UTC instants as .NET DateTime ticks (100 ns since 0001-01-01), the form project files and caches store.

use serde::{Deserialize, Deserializer, Serialize, Serializer};
use std::time::{SystemTime, UNIX_EPOCH};

/// Ticks from 0001-01-01 to 1970-01-01.
const UNIX_EPOCH_TICKS: i64 = 621_355_968_000_000_000;

#[derive(Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Default, Debug)]
pub struct UtcTime(pub i64);

impl UtcTime {
    pub fn ticks(self) -> i64 {
        self.0
    }

    pub fn from_system(t: SystemTime) -> UtcTime {
        match t.duration_since(UNIX_EPOCH) {
            Ok(d) => UtcTime(UNIX_EPOCH_TICKS + d.as_secs() as i64 * 10_000_000 + d.subsec_nanos() as i64 / 100),
            Err(e) => {
                let d = e.duration();
                UtcTime(UNIX_EPOCH_TICKS - d.as_secs() as i64 * 10_000_000 - d.subsec_nanos() as i64 / 100)
            }
        }
    }

    pub fn now() -> UtcTime {
        UtcTime::from_system(SystemTime::now())
    }

    /// ISO 8601 as System.Text.Json writes a UTC DateTime: fraction without trailing zeros, "Z".
    pub fn iso(self) -> String {
        let ticks = self.0;
        let days = ticks.div_euclid(864_000_000_000);
        let rem = ticks.rem_euclid(864_000_000_000);
        // Days from 0001-01-01 to 1970-01-01: 719162.
        let (y, m, d) = civil_from_days(days - 719_162);
        let secs = rem / 10_000_000;
        let frac = rem % 10_000_000;
        let mut s = format!("{y:04}-{m:02}-{d:02}T{:02}:{:02}:{:02}", secs / 3600, secs / 60 % 60, secs % 60);
        if frac > 0 {
            let f = format!("{frac:07}");
            s.push('.');
            s.push_str(f.trim_end_matches('0'));
        }
        s.push('Z');
        s
    }

    /// Parses "yyyy-MM-ddTHH:mm:ss[.f…][Z|±hh:mm]" (no offset counts as UTC).
    pub fn parse_iso(text: &str) -> Option<UtcTime> {
        let t = text.trim();
        if t.len() < 19 {
            return None;
        }
        let num = |a: usize, b: usize| t.get(a..b)?.parse::<i64>().ok();
        let (y, mo, d) = (num(0, 4)?, num(5, 7)?, num(8, 10)?);
        let (h, mi, s) = (num(11, 13)?, num(14, 16)?, num(17, 19)?);
        let mut rest = &t[19..];
        let mut frac = 0i64;
        if let Some(r) = rest.strip_prefix('.') {
            let digits: String = r.chars().take_while(|c| c.is_ascii_digit()).collect();
            rest = &r[digits.len()..];
            let seven: String = format!("{:0<7}", &digits[..digits.len().min(7)]);
            frac = seven.parse().ok()?;
        }
        let mut offset = 0i64;
        if rest == "Z" || rest.is_empty() {
        } else if rest.len() == 6 && (rest.starts_with('+') || rest.starts_with('-')) {
            let oh: i64 = rest[1..3].parse().ok()?;
            let om: i64 = rest[4..6].parse().ok()?;
            offset = (oh * 60 + om) * 60 * 10_000_000 * if rest.starts_with('-') { -1 } else { 1 };
        } else {
            return None;
        }
        let days = days_from_civil(y, mo, d) + 719_162;
        Some(UtcTime(days * 864_000_000_000 + ((h * 60 + mi) * 60 + s) * 10_000_000 + frac - offset))
    }
}

fn days_from_civil(y: i64, m: i64, d: i64) -> i64 {
    let y = if m <= 2 { y - 1 } else { y };
    let era = y.div_euclid(400);
    let yoe = y - era * 400;
    let mp = (m + 9) % 12;
    let doy = (153 * mp + 2) / 5 + d - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    era * 146_097 + doe - 719_468
}

fn civil_from_days(z: i64) -> (i64, i64, i64) {
    let z = z + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    (if m <= 2 { y + 1 } else { y }, m, d)
}

impl Serialize for UtcTime {
    fn serialize<S: Serializer>(&self, s: S) -> Result<S::Ok, S::Error> {
        s.serialize_str(&self.iso())
    }
}

impl<'de> Deserialize<'de> for UtcTime {
    fn deserialize<D: Deserializer<'de>>(d: D) -> Result<UtcTime, D::Error> {
        let text = String::deserialize(d)?;
        UtcTime::parse_iso(&text).ok_or_else(|| serde::de::Error::custom(format!("invalid date {text:?}")))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn round_trips_iso() {
        for s in ["2026-10-26T12:34:56.1234567Z", "1970-01-01T00:00:00Z", "2000-02-29T23:59:59.5Z"] {
            assert_eq!(UtcTime::parse_iso(s).unwrap().iso(), s);
        }
        assert_eq!(UtcTime::parse_iso("1970-01-01T00:00:00Z").unwrap().0, UNIX_EPOCH_TICKS);
        assert_eq!(UtcTime::from_system(UNIX_EPOCH).0, UNIX_EPOCH_TICKS);
    }
}
