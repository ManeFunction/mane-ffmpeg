//! The app's look: the palette from the Avalonia app (very dark grays, pure black only for the video), Inter, and the
//! transport icons drawn from the same path data.

use egui::{Color32, FontData, FontDefinitions, FontFamily, FontId, Painter, Pos2, Rect, Stroke, Vec2, epaint};
use mane_core::persistence::settings::ThemeChoice;
use std::sync::Arc;

pub const WARN: Color32 = Color32::from_rgb(0xF5, 0xA6, 0x23);
pub const ERROR: Color32 = Color32::from_rgb(0xFF, 0x6B, 0x6B);
pub const OK: Color32 = Color32::from_rgb(0x5F, 0xD3, 0x8D);
pub const ACCENT: Color32 = Color32::from_rgb(0x3D, 0x7E, 0xF0);

/// Colors that differ between the dark and the light theme.
#[derive(Clone, Copy)]
pub struct Palette {
    pub window: Color32,
    pub panel: Color32,
    pub panel_border: Color32,
    pub field: Color32,
    pub text: Color32,
    pub dim: Color32,
}

pub const DARK: Palette = Palette {
    window: Color32::from_rgb(0x14, 0x15, 0x19),
    panel: Color32::from_rgb(0x1E, 0x1F, 0x24),
    panel_border: Color32::from_rgb(0x2C, 0x2D, 0x34),
    field: Color32::from_rgb(0x18, 0x19, 0x1D),
    text: Color32::from_rgb(0xE6, 0xE7, 0xEA),
    dim: Color32::from_rgb(0x9A, 0x9C, 0xA4),
};

pub const LIGHT: Palette = Palette {
    window: Color32::from_rgb(0xE7, 0xE8, 0xEC),
    panel: Color32::from_rgb(0xF7, 0xF7, 0xF9),
    panel_border: Color32::from_rgb(0xD5, 0xD7, 0xDD),
    field: Color32::from_rgb(0xFF, 0xFF, 0xFF),
    text: Color32::from_rgb(0x1E, 0x1F, 0x24),
    dim: Color32::from_rgb(0x6A, 0x6C, 0x74),
};

/// The palette now in use.
pub fn palette(ctx: &egui::Context) -> Palette {
    if ctx.theme() == egui::Theme::Dark { DARK } else { LIGHT }
}

pub const SEMIBOLD: &str = "semibold";

pub fn install_fonts(ctx: &egui::Context) {
    let mut fonts = FontDefinitions::default();
    fonts.font_data.insert("inter".into(), Arc::new(FontData::from_static(include_bytes!("../../../assets/fonts/Inter-Regular.ttf"))));
    fonts.font_data.insert(SEMIBOLD.into(), Arc::new(FontData::from_static(include_bytes!("../../../assets/fonts/Inter-SemiBold.ttf"))));
    fonts.families.get_mut(&FontFamily::Proportional).unwrap().insert(0, "inter".into());
    // Semibold falls back to the regular fonts for symbols Inter lacks.
    let mut semibold = vec![SEMIBOLD.to_string()];
    semibold.extend(fonts.families[&FontFamily::Proportional].iter().skip(1).cloned());
    fonts.families.insert(FontFamily::Name(SEMIBOLD.into()), semibold);
    ctx.set_fonts(fonts);
}

pub fn semibold(size: f32) -> FontId {
    FontId::new(size, FontFamily::Name(SEMIBOLD.into()))
}

pub fn regular(size: f32) -> FontId {
    FontId::proportional(size)
}

pub fn mono(size: f32) -> FontId {
    FontId::monospace(size)
}

/// Sets up both looks once; `apply` picks one.
pub fn configure(ctx: &egui::Context) {
    for (theme, p) in [(egui::Theme::Dark, DARK), (egui::Theme::Light, LIGHT)] {
        let dark = theme == egui::Theme::Dark;
        let mut visuals = if dark { egui::Visuals::dark() } else { egui::Visuals::light() };
        visuals.panel_fill = p.window;
        visuals.window_fill = p.panel;
        visuals.extreme_bg_color = p.field;
        visuals.faint_bg_color = p.panel;
        visuals.override_text_color = Some(p.text);
        visuals.window_stroke = Stroke::new(1.0, p.panel_border);
        visuals.selection.bg_fill = ACCENT.gamma_multiply(if dark { 0.55 } else { 0.35 });
        visuals.hyperlink_color = ACCENT;
        let radius = egui::CornerRadius::same(4);
        for w in [
            &mut visuals.widgets.noninteractive,
            &mut visuals.widgets.inactive,
            &mut visuals.widgets.hovered,
            &mut visuals.widgets.active,
            &mut visuals.widgets.open,
        ] {
            w.corner_radius = radius;
        }
        let (btn, btn_hover, btn_active) = if dark {
            (Color32::from_rgb(0x2A, 0x2B, 0x32), Color32::from_rgb(0x34, 0x35, 0x3D), Color32::from_rgb(0x3E, 0x40, 0x48))
        } else {
            (Color32::from_rgb(0xE2, 0xE3, 0xE8), Color32::from_rgb(0xD6, 0xD8, 0xDE), Color32::from_rgb(0xC9, 0xCB, 0xD2))
        };
        visuals.widgets.inactive.weak_bg_fill = btn;
        visuals.widgets.inactive.bg_fill = btn;
        visuals.widgets.hovered.weak_bg_fill = btn_hover;
        visuals.widgets.hovered.bg_fill = btn_hover;
        visuals.widgets.active.weak_bg_fill = btn_active;
        visuals.widgets.active.bg_fill = btn_active;
        visuals.widgets.inactive.bg_stroke = Stroke::new(1.0, p.panel_border);
        visuals.widgets.noninteractive.bg_stroke = Stroke::new(1.0, p.panel_border);
        ctx.set_visuals_of(theme, visuals);
        ctx.style_mut_of(theme, |s| {
            s.spacing.item_spacing = Vec2::new(6.0, 6.0);
            s.spacing.button_padding = Vec2::new(10.0, 5.0);
            s.spacing.interact_size.y = 26.0;
            s.text_styles.insert(egui::TextStyle::Body, regular(13.0));
            s.text_styles.insert(egui::TextStyle::Button, regular(12.5));
            s.text_styles.insert(egui::TextStyle::Small, regular(11.0));
            s.text_styles.insert(egui::TextStyle::Monospace, mono(11.5));
            s.text_styles.insert(egui::TextStyle::Heading, semibold(16.0));
        });
    }
}

/// Applies a theme choice (System follows the system's look).
pub fn apply(ctx: &egui::Context, choice: ThemeChoice) {
    let preference = match choice {
        ThemeChoice::Dark => egui::ThemePreference::Dark,
        ThemeChoice::Light => egui::ThemePreference::Light,
        ThemeChoice::System => egui::ThemePreference::System,
    };
    if ctx.options(|o| o.theme_preference) != preference {
        ctx.set_theme(preference);
    }
}

/// A rounded card like the C# `Border.panel`.
pub fn panel_frame(ctx: &egui::Context) -> egui::Frame {
    let p = palette(ctx);
    egui::Frame::new().fill(p.panel).stroke(Stroke::new(1.0, p.panel_border)).corner_radius(6).inner_margin(8)
}

/// Section titles: small, semibold, dimmed.
pub fn title(ui: &mut egui::Ui, text: &str) -> egui::Response {
    let color = palette(ui.ctx()).text.gamma_multiply(0.7);
    ui.label(egui::RichText::new(text).font(semibold(12.0)).color(color))
}

pub fn dim(ui: &mut egui::Ui, text: impl Into<String>) -> egui::Response {
    let color = palette(ui.ctx()).dim;
    ui.label(egui::RichText::new(text.into()).size(12.0).color(color))
}

pub fn dim_wrapped(ui: &mut egui::Ui, text: impl Into<String>) -> egui::Response {
    let color = palette(ui.ctx()).dim;
    ui.add(egui::Label::new(egui::RichText::new(text.into()).size(12.0).color(color)).wrap())
}

/// The transport and editor icons, as polygons in a 24×24 box (the C# StreamGeometry path data).
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Icon {
    Play,
    Pause,
    StepBack,
    StepForward,
    ToStart,
    ToEnd,
    Speaker,
    ArrowUp,
    ArrowDown,
    Close,
}

fn shapes(icon: Icon) -> Vec<Vec<(f32, f32)>> {
    match icon {
        Icon::Play => vec![vec![(8., 5.), (19., 12.), (8., 19.)]],
        Icon::Pause => vec![vec![(7., 5.), (10.5, 5.), (10.5, 19.), (7., 19.)], vec![(13.5, 5.), (17., 5.), (17., 19.), (13.5, 19.)]],
        Icon::StepBack => vec![vec![(6., 5.), (8., 5.), (8., 19.), (6., 19.)], vec![(18., 5.), (9., 12.), (18., 19.)]],
        Icon::StepForward => vec![vec![(16., 5.), (18., 5.), (18., 19.), (16., 19.)], vec![(6., 5.), (15., 12.), (6., 19.)]],
        Icon::ToStart => vec![
            vec![(5., 5.), (7., 5.), (7., 19.), (5., 19.)],
            vec![(13., 5.), (8., 12.), (13., 19.)],
            vec![(20., 5.), (15., 12.), (20., 19.)],
        ],
        Icon::ToEnd => vec![
            vec![(17., 5.), (19., 5.), (19., 19.), (17., 19.)],
            vec![(11., 5.), (16., 12.), (11., 19.)],
            vec![(4., 5.), (9., 12.), (4., 19.)],
        ],
        Icon::Speaker => vec![vec![(4., 9.), (8., 9.), (13., 5.), (13., 19.), (8., 15.), (4., 15.)]],
        Icon::ArrowUp => {
            vec![vec![(12., 4.), (19., 11.), (17.6, 12.4), (13., 7.8), (13., 20.), (11., 20.), (11., 7.8), (6.4, 12.4), (5., 11.)]]
        }
        Icon::ArrowDown => {
            vec![vec![(12., 20.), (5., 13.), (6.4, 11.6), (11., 16.2), (11., 4.), (13., 4.), (13., 16.2), (17.6, 11.6), (19., 13.)]]
        }
        Icon::Close => vec![],
    }
}

/// Draws `icon` filling `rect` (scaled from its 24×24 box).
pub fn paint_icon(painter: &Painter, rect: Rect, icon: Icon, color: Color32) {
    let scale = rect.width().min(rect.height()) / 24.0;
    let origin = rect.center() - Vec2::splat(12.0 * scale);
    let at = |(x, y): (f32, f32)| Pos2::new(origin.x + x * scale, origin.y + y * scale);
    if icon == Icon::Close {
        let s = Stroke::new(2.5 * scale, color);
        painter.line_segment([at((6.5, 6.5)), at((17.5, 17.5))], s);
        painter.line_segment([at((17.5, 6.5)), at((6.5, 17.5))], s);
        return;
    }
    if icon == Icon::Speaker {
        // The sound wave: an arc beside the cone.
        let center = at((14.5, 12.0));
        let r = 4.2 * scale;
        let points: Vec<Pos2> = (0..=12)
            .map(|i| {
                let a = -0.9 + 1.8 * i as f32 / 12.0;
                center + Vec2::new(a.cos() * r, a.sin() * r)
            })
            .collect();
        painter.add(epaint::Shape::line(points, Stroke::new(1.6 * scale, color)));
    }
    for poly in shapes(icon) {
        let points: Vec<Pos2> = poly.into_iter().map(at).collect();
        painter.add(epaint::Shape::convex_polygon(points, color, Stroke::NONE));
    }
}

/// An icon-only button that doesn't take keyboard focus (so Space always plays/pauses).
pub fn icon_button(ui: &mut egui::Ui, icon: Icon, size: Vec2, icon_size: f32, tip: &str) -> egui::Response {
    let (rect, response) = ui.allocate_exact_size(size, egui::Sense::click());
    let visuals = ui.style().interact(&response);
    if response.hovered() {
        ui.painter().rect_filled(rect, 4, visuals.weak_bg_fill);
    }
    let icon_rect = Rect::from_center_size(rect.center(), Vec2::splat(icon_size));
    paint_icon(ui.painter(), icon_rect, icon, palette(ui.ctx()).text);
    response.on_hover_text(tip)
}

/// The round × in a panel's corner.
pub fn close_button(ui: &mut egui::Ui, tip: &str) -> egui::Response {
    let (rect, response) = ui.allocate_exact_size(Vec2::splat(18.0), egui::Sense::click());
    let visuals = ui.style().interact(&response);
    ui.painter().circle_filled(rect.center(), 9.0, visuals.weak_bg_fill);
    paint_icon(ui.painter(), Rect::from_center_size(rect.center(), Vec2::splat(9.0)), Icon::Close, palette(ui.ctx()).text);
    response.on_hover_text(tip)
}

/// A button filled with the accent color.
pub fn accent_button(text: impl Into<egui::WidgetText>) -> egui::Button<'static> {
    egui::Button::new(text.into().color(Color32::WHITE)).fill(ACCENT)
}

/// A small tool button (12 pt), like the C# `Button.tool`.
pub fn tool_button(text: &str) -> egui::Button<'static> {
    egui::Button::new(egui::RichText::new(text.to_string()).size(12.0))
}

/// The tool buttons in panel headers (11 pt, 18 px tall).
pub fn small_button(text: &str) -> egui::Button<'static> {
    egui::Button::new(egui::RichText::new(text.to_string()).size(11.0)).min_size(Vec2::new(0.0, 18.0))
}
