use serde::{Deserialize, Serialize};
use std::fmt;

/// An exact fraction such as ffprobe's "30000/1001" frame rate.
#[derive(Clone, Copy, PartialEq, Eq, Hash, Default, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct Rational {
    pub num: i64,
    pub den: i64,
}

impl Rational {
    pub const ZERO: Rational = Rational { num: 0, den: 1 };
    pub const ONE: Rational = Rational { num: 1, den: 1 };

    pub const fn new(num: i64, den: i64) -> Rational {
        Rational { num, den }
    }

    pub fn is_valid(self) -> bool {
        self.num > 0 && self.den > 0
    }

    pub fn value(self) -> f64 {
        if self.den == 0 { 0.0 } else { self.num as f64 / self.den as f64 }
    }

    pub fn parse(text: Option<&str>) -> Rational {
        let Some(text) = text.map(str::trim).filter(|t| !t.is_empty()) else {
            return Rational::ZERO;
        };
        let parts: Vec<&str> = text.split(['/', ':']).collect();
        if parts.len() == 2
            && let (Ok(n), Ok(d)) = (parts[0].trim().parse::<i64>(), parts[1].trim().parse::<i64>())
        {
            return Rational::new(n, d);
        }
        if let Some(v) = super::time::parse_float(text)
            && v > 0.0
        {
            return Rational::new((v * 1000.0).round_ties_even() as i64, 1000);
        }
        Rational::ZERO
    }

    pub fn equivalent(self, other: Rational) -> bool {
        self.is_valid() && other.is_valid() && self.num as i128 * other.den as i128 == other.num as i128 * self.den as i128
    }
}

impl fmt::Display for Rational {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}/{}", self.num, self.den)
    }
}

impl fmt::Debug for Rational {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}/{}", self.num, self.den)
    }
}
