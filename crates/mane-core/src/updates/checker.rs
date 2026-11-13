//! Looks for a newer release on GitHub.

use super::{changelog, installer, parse_version};
use crate::persistence::app_log;
use crate::version::Version;
use serde::Deserialize;
use std::time::Duration;

pub const REPOSITORY: &str = "ManeFunction/mane-ffmpeg";

/// A downloadable file of a release; `sha256` is GitHub's digest of it, in hex, when it has one.
#[derive(Clone, Debug, PartialEq)]
pub struct ReleaseAsset {
    pub name: String,
    pub url: String,
    pub size: u64,
    pub sha256: Option<String>,
}

/// A newer release than this build, with what changed since this build.
#[derive(Clone, Debug, PartialEq)]
pub struct UpdateOffer {
    pub version: Version,
    pub tag: String,
    pub page: String,
    /// The download for this platform, or None when the release has none.
    pub asset: Option<ReleaseAsset>,
    /// CHANGELOG.md's sections after this build's version up to the release (Markdown).
    pub changelog: Option<String>,
}

#[derive(Deserialize)]
struct GitHubRelease {
    tag_name: String,
    html_url: String,
    body: Option<String>,
    #[serde(default)]
    assets: Vec<GitHubAsset>,
}

#[derive(Deserialize)]
struct GitHubAsset {
    name: String,
    browser_download_url: String,
    #[serde(default)]
    size: u64,
    digest: Option<String>,
}

/// Talks to api.github.com and the download hosts; GitHub rejects requests without a User-Agent.
pub fn http_agent() -> ureq::Agent {
    ureq::Agent::config_builder()
        .timeout_global(Some(Duration::from_secs(30)))
        .user_agent(format!("ManeFfmpeg/{}", Version::current()))
        .build()
        .into()
}

/// The latest release (GitHub's "latest" skips drafts and pre-releases) when it's newer than `current`, else None.
/// Network and API errors are returned.
pub fn check(agent: &ureq::Agent, current: Version) -> Result<Option<UpdateOffer>, String> {
    let mut response = agent
        .get(format!("https://api.github.com/repos/{REPOSITORY}/releases/latest"))
        .header("Accept", "application/vnd.github+json")
        .call()
        .map_err(|e| e.to_string())?;
    let release: GitHubRelease = response.body_mut().read_json().map_err(|e| e.to_string())?;
    let Some(version) = parse_version(&release.tag_name).filter(|v| *v > current) else {
        return Ok(None);
    };
    let asset_name = installer::asset_name(version);
    let asset = release.assets.iter().find(|a| Some(&a.name) == asset_name.as_ref()).map(|a| ReleaseAsset {
        name: a.name.clone(),
        url: a.browser_download_url.clone(),
        size: a.size,
        sha256: sha256_of(a.digest.as_deref()),
    });
    let notes = changelog_at(agent, &release.tag_name, current, version).or_else(|| notes_without_downloads(release.body.as_deref()));
    Ok(Some(UpdateOffer { version, tag: release.tag_name, page: release.html_url, asset, changelog: notes }))
}

/// CHANGELOG.md as of the release's tag, cut to the versions between; None if it can't be read.
fn changelog_at(agent: &ureq::Agent, tag: &str, current: Version, latest: Version) -> Option<String> {
    let url = format!("https://raw.githubusercontent.com/{REPOSITORY}/{}/CHANGELOG.md", escape(tag));
    match agent.get(&url).call().and_then(|mut r| r.body_mut().read_to_string()) {
        Ok(md) => changelog::between(&md, current, latest),
        Err(e) => {
            app_log::write(&format!("Update: couldn't read CHANGELOG.md at {tag}: {e}"));
            None
        }
    }
}

fn escape(s: &str) -> String {
    s.bytes()
        .map(|b| if b.is_ascii_alphanumeric() || b"-._~".contains(&b) { (b as char).to_string() } else { format!("%{b:02X}") })
        .collect()
}

/// The release notes without the install instructions the release workflow appends.
fn notes_without_downloads(body: Option<&str>) -> Option<String> {
    let body = body.filter(|b| !b.trim().is_empty())?;
    let text = body.replace("\r\n", "\n");
    let cut = text.find("\n## Downloads").map(|i| &text[..i]).unwrap_or(&text);
    Some(cut.trim().to_string())
}

/// "sha256:abc…" → "abc…".
fn sha256_of(digest: Option<&str>) -> Option<String> {
    let d = digest?;
    (d.len() > 7 && d[..7].eq_ignore_ascii_case("sha256:")).then(|| d[7..].to_string())
}
