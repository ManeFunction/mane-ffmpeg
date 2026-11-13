use std::fmt;

/// A dotted version such as "8.1.2" (missing parts are 0).
#[derive(Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Debug, Default)]
pub struct Version {
    pub major: u32,
    pub minor: u32,
    pub patch: u32,
}

impl Version {
    pub const fn new(major: u32, minor: u32, patch: u32) -> Version {
        Version { major, minor, patch }
    }

    /// "1.2", "1.2.3" or "1.2.3.4" (extra parts are ignored); None for anything else.
    pub fn parse(text: &str) -> Option<Version> {
        let parts: Vec<&str> = text.trim().split('.').collect();
        if parts.len() < 2 || parts.len() > 4 {
            return None;
        }
        let n = |i: usize| -> Option<u32> { parts.get(i).map(|p| p.parse::<u32>().ok()).unwrap_or(Some(0)) };
        Some(Version::new(n(0)?, n(1)?, n(2)?))
    }

    /// The running app's version.
    pub fn current() -> Version {
        Version::parse(env!("CARGO_PKG_VERSION")).unwrap()
    }
}

impl fmt::Display for Version {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}.{}.{}", self.major, self.minor, self.patch)
    }
}
