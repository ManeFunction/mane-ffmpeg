//! Shows CHANGELOG.md sections: release and group headings, bullet lists (wrapped and nested), **bold**, *italic* and
//! `code`. Links show their text only. That's all the Markdown the changelog uses.

use crate::theme;
use egui::text::LayoutJob;
use egui::{Color32, FontId, TextFormat};
use regex::Regex;

pub fn show(ui: &mut egui::Ui, markdown: &str) {
    let text = theme::palette(ui.ctx()).text;
    let mut bullet: Option<(usize, String)> = None;
    let mut first = true;
    let flush = |ui: &mut egui::Ui, bullet: &mut Option<(usize, String)>| {
        if let Some((level, body)) = bullet.take() {
            ui.horizontal_top(|ui| {
                ui.add_space(4.0 + level as f32 * 16.0);
                ui.label(egui::RichText::new(if level == 0 { "•" } else { "◦" }).size(13.0));
                ui.add(egui::Label::new(inline(&body, 13.0, text)).wrap().selectable(true));
            });
        }
    };
    for raw in markdown.replace("\r\n", "\n").split('\n') {
        let line = raw.trim_end();
        if line.is_empty() {
            flush(ui, &mut bullet);
            continue;
        }
        let indent = line.len() - line.trim_start().len();
        let t = line.trim();
        if let Some(h) = t.strip_prefix("## ") {
            flush(ui, &mut bullet);
            if !first {
                ui.add_space(12.0);
            }
            ui.label(egui::RichText::new(release_title(h)).font(theme::semibold(15.0)));
        } else if let Some(h) = t.strip_prefix("### ") {
            flush(ui, &mut bullet);
            ui.add_space(4.0);
            ui.label(egui::RichText::new(h).font(theme::semibold(13.0)).color(text.gamma_multiply(0.75)));
        } else if let Some(b) = t.strip_prefix("- ").or_else(|| t.strip_prefix("* ")) {
            flush(ui, &mut bullet);
            bullet = Some((indent / 2, b.to_string()));
        } else if let (Some((_, body)), true) = (&mut bullet, indent > 0) {
            // A wrapped bullet continues on an indented line.
            body.push(' ');
            body.push_str(t);
        } else {
            flush(ui, &mut bullet);
            ui.add(egui::Label::new(inline(t, 13.0, text)).wrap().selectable(true));
        }
        first = false;
    }
    flush(ui, &mut bullet);
}

/// "[0.9.1] - 2026-11-02" → "0.9.1 · 2026-11-02".
fn release_title(heading: &str) -> String {
    let h = heading.replace(['[', ']'], "");
    match h.split_once(" - ") {
        Some((v, d)) => format!("{v} · {d}"),
        None => h,
    }
}

fn inline(markdown: &str, size: f32, color: Color32) -> LayoutJob {
    let re = Regex::new(
        r"\*\*(?P<bold>.+?)\*\*|`(?P<code>[^`]+)`|(?:^|[^\w*])\*(?P<italic>[^*\s][^*]*?)\*(?:[^\w*]|$)|\[(?P<link>[^\]]+)\]\([^)]*\)",
    )
    .unwrap();
    let mut job = LayoutJob::default();
    let plain = TextFormat { font_id: theme::regular(size), color, ..Default::default() };
    let mut at = 0;
    for c in re.captures_iter(markdown) {
        let m = c.get(0).unwrap();
        let (text, format, start, end) = if let Some(b) = c.name("bold") {
            (b.as_str(), TextFormat { font_id: theme::semibold(size), color, ..Default::default() }, m.start(), m.end())
        } else if let Some(code) = c.name("code") {
            (code.as_str(), TextFormat { font_id: FontId::monospace(size - 1.0), color, ..Default::default() }, m.start(), m.end())
        } else if let Some(i) = c.name("italic") {
            // The match may include a neighbouring character; keep it as plain text.
            (
                i.as_str(),
                TextFormat { font_id: theme::regular(size), color, italics: true, ..Default::default() },
                i.start() - 1,
                i.end() + 1,
            )
        } else {
            (c.name("link").unwrap().as_str(), plain.clone(), m.start(), m.end())
        };
        if start > at {
            job.append(&markdown[at..start], 0.0, plain.clone());
        }
        job.append(text, 0.0, format);
        at = end;
    }
    if at < markdown.len() {
        job.append(&markdown[at..], 0.0, plain);
    }
    job
}
