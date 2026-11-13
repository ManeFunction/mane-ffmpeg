#![allow(dead_code)]
use mane_core::model::*;

pub fn s(seconds: f64) -> Time {
    Time::from_seconds(seconds)
}

pub fn ms(ms: f64) -> Time {
    Time::from_ms_f(ms)
}

pub fn clip(id: &str, source: &str, in_s: f64, out_s: f64) -> Clip {
    Clip { id: id.into(), source_id: source.into(), r#in: s(in_s), out: s(out_s), ..Default::default() }
}

pub fn source(id: &str, path: &str) -> SourceMedia {
    SourceMedia { id: id.into(), path: path.into(), ..Default::default() }
}

pub fn ids(list: &[&str]) -> std::collections::HashSet<String> {
    list.iter().map(|s| s.to_string()).collect()
}
