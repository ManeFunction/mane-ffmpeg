//! Updates from GitHub releases: finding a newer one, its changelog, downloading and swapping it in.

pub mod changelog;
pub mod checker;
pub mod installer;
pub mod release_package;

use crate::version::Version;

/// A tag or changelog version: a leading "v" and any pre-release or build suffix ("-rc.1", "+abc") are ignored.
/// None when it isn't a version.
pub fn parse_version(text: &str) -> Option<Version> {
    let value = text.trim().trim_start_matches(['v', 'V']);
    let value = match value.find(['-', '+']) {
        Some(i) => &value[..i],
        None => value,
    };
    Version::parse(value)
}
