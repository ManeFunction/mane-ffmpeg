//! Reads CHANGELOG.md, written in the Keep a Changelog format (`## [1.2.0] - 2026-10-26` per release).

use crate::version::Version;
use regex::Regex;

/// The release sections newer than `after` up to and including `up_to`, newest first as in the file, or None when
/// there are none. "Unreleased" and the link references at the end are left out.
pub fn between(markdown: &str, after: Version, up_to: Version) -> Option<String> {
    let heading = Regex::new(r"^##\s+\[?v?([0-9][^\]\s]*)\]?").unwrap();
    let link = Regex::new(r"^\[[^\]]+\]:\s").unwrap();
    let mut lines = Vec::new();
    let mut taking = false;
    let normalized = markdown.replace("\r\n", "\n").replace('\r', "\n");
    for line in normalized.split('\n') {
        if line.starts_with("## ") {
            let version = heading.captures(line).and_then(|c| super::parse_version(&c[1]));
            taking = version.is_some_and(|v| v > after && v <= up_to);
        }
        if taking && !link.is_match(line) {
            lines.push(line.trim_end());
        }
    }
    let text = lines.join("\n");
    let text = text.trim_matches('\n');
    (!text.is_empty()).then(|| text.to_string())
}
