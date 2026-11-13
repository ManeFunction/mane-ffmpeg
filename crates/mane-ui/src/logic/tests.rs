//! The C# UI tests of gestures and geometry, ported.
#![cfg(test)]

use super::audio_drag::*;
use super::crop_gesture::*;
use super::preview_geometry as g;
use super::rect::{Pt, R};
use super::timeline_gestures::*;
use super::timeline_layout as tl;
use mane_core::editing::timeline_editor as te;
use mane_core::editing::{CutGrid, UndoStack};
use mane_core::model::*;
use std::collections::{HashMap, HashSet};

fn s(v: f64) -> Time {
    Time::from_seconds(v)
}

fn c(id: &str, a: f64, b: f64) -> Clip {
    Clip { id: id.into(), source_id: "s".into(), r#in: s(a), out: s(b), ..Default::default() }
}

struct Host {
    history: UndoStack<Timeline>,
    selection: HashSet<String>,
    seeks: Vec<(Time, bool)>,
    scrub_ended: bool,
    audio_drag: Time,
    grid: Option<CutGrid>,
    audio_ends: HashMap<String, Time>,
}

impl Host {
    fn new(t: Timeline) -> Host {
        Host {
            history: UndoStack::new(t),
            selection: HashSet::new(),
            seeks: vec![],
            scrub_ended: false,
            audio_drag: Time::ZERO,
            grid: None,
            audio_ends: HashMap::new(),
        }
    }
    fn tl(&self) -> &Timeline {
        self.history.current()
    }
}

impl GestureHost for Host {
    fn timeline(&self) -> Timeline {
        (**self.history.current()).clone()
    }
    fn selection(&self) -> HashSet<String> {
        self.selection.clone()
    }
    fn zoom(&self) -> f64 {
        100.0
    }
    fn select(&mut self, id: &str, toggle: bool, _range: bool) {
        if !toggle {
            self.selection.clear();
        }
        self.selection.insert(id.into());
    }
    fn clear_selection(&mut self) {
        self.selection.clear();
    }
    fn seek(&mut self, t: Time, scrubbing: bool) {
        self.seeks.push((t, scrubbing));
    }
    fn end_scrub(&mut self) {
        self.scrub_ended = true;
    }
    fn begin_gesture(&mut self) {
        self.history.begin_gesture();
    }
    fn preview_timeline(&mut self, t: Timeline) {
        self.history.replace_current(t);
    }
    fn end_gesture(&mut self) {
        self.history.end_gesture();
    }
    fn move_selection(&mut self, i: usize) {
        let next = te::move_clips(self.history.current(), &self.selection, i);
        self.history.commit(next);
    }
    fn grid_of(&self, _: &str) -> Option<CutGrid> {
        self.grid.clone()
    }
    fn preview_audio_drag(&mut self, d: Time) {
        self.audio_drag = d;
    }
    fn audio_end(&self, clip: &Clip) -> Option<Time> {
        self.audio_ends.get(&clip.id).copied()
    }
}

fn three(grid: Option<CutGrid>) -> Host {
    let mut h = Host::new(Timeline::new(vec![c("a", 0.0, 2.0), c("b", 10.0, 12.0), c("c", 20.0, 22.0)]));
    h.grid = grid;
    h
}

fn hit(zone: TimelineZone, t: f64, id: Option<&str>) -> TimelineHit {
    TimelineHit { zone, time: s(t), clip_id: id.map(String::from) }
}

#[test]
fn a_click_that_barely_moves_selects_and_seeks() {
    let mut h = three(None);
    let mut g = TimelineGestures::default();
    g.press(&mut h, hit(TimelineZone::Clip, 2.5, Some("b")), false, false);
    g.move_to(&mut h, s(2.53), false);
    assert!(!g.is_reordering());
    g.release(&mut h, s(2.53));
    assert_eq!(h.selection, HashSet::from(["b".to_string()]));
    assert_eq!(*h.seeks.last().unwrap(), (s(2.53), false));
    assert!(!h.history.can_undo());
}

#[test]
fn a_slip_drag_is_one_undo_step() {
    let mut h = three(None);
    let mut g = TimelineGestures::default();
    g.press(&mut h, hit(TimelineZone::Audio, 1.0, Some("a")), false, false);
    for t in [1.05, 1.10, 1.12] {
        g.move_to(&mut h, s(t), false);
    }
    assert_eq!(h.audio_drag, Time::from_ms(120));
    g.release(&mut h, s(1.12));
    assert_eq!(h.tl().clips()[0].audio_offset, Time::from_ms(120));
    assert_eq!(h.audio_drag, Time::ZERO);
    h.history.undo();
    assert_eq!(h.tl().clips()[0].audio_offset, Time::ZERO);
    assert!(!h.history.can_undo());
}

#[test]
fn slipping_replaced_audio_moves_the_replacement_in_one_undo_step() {
    let t = te::replace_audio(three(None).tl(), "a", "f");
    let mut h = Host::new(t);
    let mut g = TimelineGestures::default();
    g.press(&mut h, hit(TimelineZone::Audio, 1.0, Some("a")), false, false);
    g.move_to(&mut h, s(1.2), false);
    g.move_to(&mut h, s(1.5), false);
    g.release(&mut h, s(1.5));
    assert_eq!(h.tl().clips()[0].replaced_audio.as_ref().unwrap().offset, Time::from_ms(500));
    assert_eq!(h.tl().clips()[0].audio_offset, Time::ZERO);
    h.history.undo();
    assert_eq!(h.tl().clips()[0].replaced_audio.as_ref().unwrap().offset, Time::ZERO);
}

#[test]
fn shift_slips_at_a_tenth_of_the_speed() {
    let mut h = three(None);
    let mut g = TimelineGestures::default();
    g.press(&mut h, hit(TimelineZone::Audio, 1.0, Some("a")), false, false);
    g.move_to(&mut h, s(1.05), true);
    g.move_to(&mut h, s(1.12), true);
    g.release(&mut h, s(1.12));
    assert_eq!(h.tl().clips()[0].audio_offset, Time::from_ms(12));
}

#[test]
fn trimming_snaps_to_frames_in_one_undo_step() {
    let mut h = three(Some(CutGrid { duration: s(30.0), frame_rate: Rational::new(30, 1), ..Default::default() }));
    let mut g = TimelineGestures::default();
    g.press(&mut h, hit(TimelineZone::TrimOut, 2.0, Some("a")), false, false);
    g.move_to(&mut h, s(2.3), false);
    g.move_to(&mut h, s(2.513), false);
    g.release(&mut h, s(2.513));
    assert_eq!(h.tl().clips()[0].out, s(2.5));
    h.history.undo();
    assert_eq!(h.tl().clips()[0].out, s(2.0));
}

#[test]
fn reordering_drops_at_the_nearest_cut() {
    let mut h = three(None);
    let mut g = TimelineGestures::default();
    g.press(&mut h, hit(TimelineZone::Clip, 1.0, Some("a")), false, false);
    g.move_to(&mut h, s(5.1), false);
    assert!(g.is_reordering());
    assert_eq!(g.insert_index, Some(3));
    g.release(&mut h, s(5.1));
    let order: String = h.tl().clips().iter().map(|c| c.id.as_str()).collect();
    assert_eq!(order, "bca");
}

#[test]
fn pressing_an_empty_track_clears_the_selection_and_scrubs_but_the_ruler_keeps_it() {
    let mut h = three(None);
    h.selection.insert("a".into());
    let mut g = TimelineGestures::default();
    g.press(&mut h, hit(TimelineZone::Ruler, 3.0, None), false, false);
    g.release(&mut h, s(3.0));
    assert_eq!(h.selection, HashSet::from(["a".to_string()]));
    g.press(&mut h, hit(TimelineZone::Empty, 3.0, None), false, false);
    g.move_to(&mut h, s(3.5), false);
    g.release(&mut h, s(3.5));
    assert!(h.selection.is_empty());
    assert_eq!(h.seeks, vec![(s(3.0), true), (s(3.0), true), (s(3.5), true)]);
    assert!(h.scrub_ended);
}

#[test]
fn shift_scrubbing_snaps_to_clip_edges_within_eight_pixels() {
    let mut h = three(None);
    let mut g = TimelineGestures::default();
    g.press(&mut h, hit(TimelineZone::Ruler, 1.95, None), false, true);
    g.move_to(&mut h, s(2.5), true);
    g.move_to(&mut h, s(3.9), true);
    g.move_to(&mut h, s(3.96), true);
    g.move_to(&mut h, s(3.96), false);
    g.release(&mut h, s(3.96));
    assert_eq!(h.seeks, vec![(s(2.0), true), (s(2.5), true), (s(3.9), true), (s(4.0), true), (s(3.96), true)]);
    assert!(h.scrub_ended);
}

#[test]
fn pressing_or_releasing_shift_mid_scrub_snaps_or_lets_go_at_once() {
    let mut h = three(None);
    let mut g = TimelineGestures::default();
    g.press(&mut h, hit(TimelineZone::Empty, 5.0, None), false, false);
    g.move_to(&mut h, s(5.95), false);
    g.shift_changed(&mut h, true);
    g.shift_changed(&mut h, false);
    g.release(&mut h, s(5.95));
    g.shift_changed(&mut h, true);
    assert_eq!(h.seeks, vec![(s(5.0), true), (s(5.95), true), (s(6.0), true), (s(5.95), true)]);
}

#[test]
fn shift_scrubbing_snaps_to_where_audio_starts_or_runs_out_inside_a_clip() {
    let mut a = c("a", 0.0, 2.0);
    a.audio_offset = s(0.5);
    let mut h = Host::new(Timeline::new(vec![a, c("b", 10.0, 12.0), c("c", 20.0, 22.0)]));
    h.audio_ends.insert("a".into(), s(30.0));
    h.audio_ends.insert("b".into(), s(11.0));
    let mut g = TimelineGestures::default();
    g.press(&mut h, hit(TimelineZone::Ruler, 0.45, None), false, true);
    g.move_to(&mut h, s(3.05), true);
    g.move_to(&mut h, s(3.05), false);
    g.release(&mut h, s(3.05));
    assert_eq!(h.seeks, vec![(s(0.5), true), (s(3.0), true), (s(3.05), true)]);
}

#[test]
fn shift_scrubbing_snaps_to_where_looped_audio_starts_again() {
    let t = te::replace_audio(three(None).tl(), "c", "f");
    let mut h = Host::new(te::set_audio_loop(&t, &HashSet::from(["c".to_string()]), true));
    h.audio_ends.insert("c".into(), s(0.75));
    let mut g = TimelineGestures::default();
    g.press(&mut h, hit(TimelineZone::Ruler, 4.8, None), false, true);
    g.move_to(&mut h, s(5.45), true);
    g.move_to(&mut h, s(5.3), true);
    g.release(&mut h, s(5.3));
    assert_eq!(h.seeks, vec![(s(4.75), true), (s(5.5), true), (s(5.3), true)]);
}

#[test]
fn hit_testing_finds_edges_bodies_and_audio() {
    let t = Timeline::new(vec![c("a", 0.0, 2.0), c("b", 10.0, 12.0)]);
    let zone = |x: f64, y: f64| tl::hit_test(&t, 100.0, 0.0, x, y).zone;
    let x0 = tl::x(100.0, 0.0, s(2.0));
    let video = tl::VIDEO_TOP + tl::VIDEO_HEIGHT / 2.0;
    let audio = tl::AUDIO_TOP + tl::AUDIO_HEIGHT / 2.0;
    assert_eq!(zone(x0, tl::RULER_HEIGHT / 2.0), TimelineZone::Ruler);
    assert_eq!(zone(x0 - 2.0, video), TimelineZone::TrimOut);
    assert_eq!(zone(x0 + 2.0, video), TimelineZone::TrimIn);
    assert_eq!(zone(x0 + 50.0, video), TimelineZone::Clip);
    assert_eq!(zone(x0 + 50.0, audio), TimelineZone::Audio);
    assert_eq!(zone(tl::x(100.0, 0.0, s(5.0)), video), TimelineZone::Empty);
    assert_eq!(tl::hit_test(&t, 100.0, 0.0, x0 + 50.0, audio).clip_id.as_deref(), Some("b"));
}

fn song() -> Option<DraggedFile> {
    Some(DraggedFile { id: "f".into(), name: "song.m4a".into(), color: egui::Color32::WHITE })
}

fn status(effects: &[AudioDragEffect]) -> Option<Option<String>> {
    effects.iter().rev().find_map(|e| if let AudioDragEffect::Status(s) = e { Some(s.clone()) } else { None })
}

#[test]
fn dropping_on_a_clip_replaces_its_sound() {
    let mut d = AudioDragGesture::default();
    d.press(song());
    let (on, effects) = d.move_to(DRAG_THRESHOLD, Some("c".into()));
    assert!(!on && effects.is_empty());
    let (on, effects) = d.move_to(20.0, None);
    assert!(on);
    assert!(status(&effects).flatten().unwrap().contains("song.m4a"));
    assert!(d.move_to(40.0, Some("c".into())).0);
    assert_eq!(d.target_clip.as_deref(), Some("c"));
    let effects = d.release();
    assert!(!effects.is_empty());
    assert!(effects.contains(&AudioDragEffect::Replace { clip_id: "c".into(), file_id: "f".into() }));
    assert!(effects.contains(&AudioDragEffect::ShowDrop(None)));
    assert_eq!(status(&effects), Some(None));
    assert!(!d.is_dragging);
}

#[test]
fn released_off_a_clip_or_cancelled_nothing_changes() {
    let mut d = AudioDragGesture::default();
    d.press(song());
    d.move_to(20.0, Some("c".into()));
    d.move_to(30.0, None);
    let e = d.release();
    assert!(!e.is_empty() && !e.iter().any(|x| matches!(x, AudioDragEffect::Replace { .. })));
    d.press(song());
    d.move_to(20.0, Some("c".into()));
    let e = d.cancel();
    assert!(!e.is_empty() && !e.iter().any(|x| matches!(x, AudioDragEffect::Replace { .. })));
    assert!(d.release().is_empty());
}

#[test]
fn a_click_or_a_missing_file_never_drags() {
    let mut d = AudioDragGesture::default();
    d.press(song());
    assert!(d.release().is_empty());
    // A missing file isn't passed as a draggable one.
    d.press(None);
    assert!(!d.move_to(50.0, Some("c".into())).0);
    assert!(d.release().is_empty());
}

const FRAME: R = R::new(0.0, 0.0, 800.0, 450.0);
const MIDDLE: R = R::new(200.0, 112.5, 400.0, 225.0);

fn drag(from: Pt, to: Pt, shown: Option<R>, lock: f64, shift: bool) -> Option<CropRect> {
    let mut d = CropDragGesture::default();
    d.press(from, FRAME, shown.unwrap_or(MIDDLE), lock);
    for i in 1..=4 {
        let f = i as f64 / 4.0;
        d.move_to(Pt::new(from.x + (to.x - from.x) * f, from.y + (to.y - from.y) * f), shift);
    }
    d.release(to)
}

fn near(expected: CropRect, actual: Option<CropRect>) {
    let a = actual.expect("a crop");
    assert!(
        (expected.x - a.x).abs() < 1e-6
            && (expected.y - a.y).abs() < 1e-6
            && (expected.width - a.width).abs() < 1e-6
            && (expected.height - a.height).abs() < 1e-6,
        "expected {expected:?} but got {a:?}"
    );
}

fn p(x: f64, y: f64) -> Pt {
    Pt::new(x, y)
}

#[test]
fn dragging_inside_moves_it_and_keeps_it_in_the_frame() {
    near(CropRect::new(0.375, 0.25, 0.5, 0.5), drag(p(400.0, 225.0), p(500.0, 225.0), None, 0.0, false));
    near(CropRect::new(0.5, 0.25, 0.5, 0.5), drag(p(400.0, 225.0), p(900.0, 225.0), None, 0.0, false));
}

#[test]
fn dragging_a_corner_or_an_edge_resizes_it() {
    near(CropRect::new(0.25, 0.25, 0.625, 0.25 + 400.0 / 450.0 - 0.5), drag(p(600.0, 337.5), p(700.0, 400.0), None, 0.0, false));
    near(CropRect::new(0.125, 0.25, 0.625, 0.5), drag(p(205.0, 225.0), p(100.0, 225.0), None, 0.0, false));
}

#[test]
fn a_locked_shape_holds_while_resizing_and_still_fits_the_frame() {
    let close = |a: f64, b: f64| (a - b).abs() < 1e-6;
    let sq = drag(p(600.0, 337.5), p(700.0, 350.0), None, 1.0, false).unwrap();
    assert!(close(sq.width * FRAME.width / (sq.height * FRAME.height), 1.0));
    assert!(close(sq.width, 337.5 / 800.0));
    assert!(close(sq.bottom(), 1.0));
    let tall = drag(p(600.0, 225.0), p(800.0, 225.0), None, 1.0, false).unwrap();
    assert!(close(tall.width * FRAME.width / (tall.height * FRAME.height), 1.0));
    assert!(tall.y >= 0.0 && tall.bottom() <= 1.0 + 1e-9);
    assert!(close(tall.height, 1.0));
    let shifted = drag(p(600.0, 337.5), p(700.0, 337.5), None, 0.0, true).unwrap();
    assert!(close(MIDDLE.width / MIDDLE.height, shifted.width * FRAME.width / (shifted.height * FRAME.height)));
}

#[test]
fn it_never_gets_smaller_than_the_minimum() {
    let tiny = drag(p(600.0, 337.5), p(150.0, 50.0), None, 0.0, false).unwrap();
    let crushed = drag(p(600.0, 337.5), p(200.5, 112.6), None, 0.0, false).unwrap();
    assert!(tiny.width >= CropRect::MIN_SIZE - 1e-9 && tiny.height >= CropRect::MIN_SIZE - 1e-9);
    assert!((crushed.width - CropRect::MIN_SIZE).abs() < 1e-6);
    assert!((crushed.height - CropRect::MIN_SIZE).abs() < 1e-6);
}

#[test]
fn dragging_outside_draws_a_new_one() {
    near(CropRect::new(0.0125, 0.02, 0.1125, 0.2), drag(p(10.0, 9.0), p(100.0, 99.0), None, 0.0, false));
    let wide = drag(p(10.0, 9.0), p(100.0, 99.0), None, 16.0 / 9.0, false).unwrap();
    assert!((wide.width * FRAME.width / (wide.height * FRAME.height) - 16.0 / 9.0).abs() < 1e-6);
}

#[test]
fn a_click_or_a_lost_capture_changes_nothing() {
    let mut d = CropDragGesture::default();
    d.press(p(20.0, 20.0), FRAME, MIDDLE, 0.0);
    assert!(d.is_dragging());
    assert!(d.release(p(20.5, 20.0)).is_none());
    assert!(!d.is_dragging());
    assert!(d.live.is_none());
    d.press(p(400.0, 225.0), FRAME, MIDDLE, 0.0);
    d.move_to(p(500.0, 300.0), false);
    assert!(d.live.is_some());
    d.cancel();
    assert!(d.live.is_none());
    assert!(d.release(p(500.0, 300.0)).is_none());
}

#[test]
fn edges_are_grabbed_from_either_side_and_small_rectangles_can_still_be_moved() {
    assert_eq!(hit_edges(MIDDLE, p(195.0, 110.0)), LEFT | TOP);
    assert_eq!(hit_edges(MIDDLE, p(606.0, 225.0)), RIGHT);
    assert_eq!(hit_edges(MIDDLE, p(400.0, 332.0)), BOTTOM);
    assert_eq!(hit_edges(MIDDLE, p(400.0, 225.0)), 0);
    assert_eq!(hit_edges(MIDDLE, p(100.0, 225.0)), 0);
    assert_eq!(hit_edges(R::new(100.0, 100.0, 20.0, 20.0), p(110.0, 110.0)), 0);
}

const WIDE: f64 = 16.0 / 9.0;

fn near_r(e: R, a: R) {
    assert!(
        (e.x - a.x).abs() < 1e-6 && (e.y - a.y).abs() < 1e-6 && (e.width - a.width).abs() < 1e-6 && (e.height - a.height).abs() < 1e-6,
        "expected {e:?} but got {a:?}"
    );
}

#[test]
fn fits_and_centers() {
    near_r(R::new(0.0, 50.0, 800.0, 450.0), g::fit(WIDE, R::new(0.0, 0.0, 800.0, 550.0)));
    near_r(R::new(298.75, 0.0, 202.5, 360.0), g::fit(9.0 / 16.0, R::new(0.0, 0.0, 800.0, 360.0)));
    near_r(R::new(0.0, 0.0, 800.0, 550.0), g::fit(0.0, R::new(0.0, 0.0, 800.0, 550.0)));
}

#[test]
fn the_frame_of_a_clip_with_another_shape_pads_it_like_an_export() {
    let picture = R::new(100.0, 0.0, 360.0, 360.0);
    near_r(R::new(280.0 - 320.0, 0.0, 640.0, 360.0), g::frame_around(picture, 1.0, WIDE));
    near_r(picture, g::frame_around(picture, 1.0, 1.0));
}

#[test]
fn shows_the_whole_frame_as_mpv_rendered_it() {
    let (src, dst) = g::placement(800.0, 450.0, R::new(0.0, 0.0, 800.0, 450.0), WIDE, WIDE, &CropRect::FULL);
    near_r(R::new(0.0, 0.0, 800.0, 450.0), src);
    near_r(R::new(0.0, 0.0, 800.0, 450.0), dst);
}

#[test]
fn a_crop_is_cut_from_the_picture_and_fills_the_view() {
    let (src, dst) = g::placement(1600.0, 1100.0, R::new(0.0, 0.0, 800.0, 450.0), WIDE, WIDE, &CropRect::new(0.0, 0.0, 0.5, 1.0));
    near_r(R::new(0.0, 100.0, 800.0, 900.0), src);
    near_r(R::new(200.0, 0.0, 400.0, 450.0), dst);
}

#[test]
fn padding_outside_the_rendered_picture_stays_black() {
    let (src, dst) = g::placement(400.0, 400.0, R::new(0.0, 0.0, 640.0, 360.0), 1.0, WIDE, &CropRect::FULL);
    near_r(R::new(0.0, 0.0, 400.0, 400.0), src);
    near_r(R::new(140.0, 0.0, 360.0, 360.0), dst);
}

#[test]
fn renders_bigger_for_a_small_crop_and_never_smaller() {
    let b = R::new(0.0, 0.0, 800.0, 450.0);
    assert_eq!(g::render_scale(b, WIDE, WIDE, None), 1.0);
    assert!((g::render_scale(b, WIDE, WIDE, Some(&CropRect::FULL)) - 1.0).abs() < 1e-6);
    assert!((g::render_scale(b, WIDE, WIDE, Some(&CropRect::new(0.375, 0.375, 0.25, 0.25))) - 4.0).abs() < 1e-6);
}

#[test]
fn converts_between_crops_and_screen_rectangles() {
    let frame = R::new(10.0, 20.0, 800.0, 450.0);
    let crop = CropRect::new(0.25, 0.1, 0.5, 0.6);
    near_r(R::new(210.0, 65.0, 400.0, 270.0), g::inside(frame, &crop));
    let back = g::to_crop(frame, g::inside(frame, &crop));
    let r9 = |v: f64| (v * 1e9).round() / 1e9;
    assert_eq!((r9(back.x), r9(back.y), r9(back.width), r9(back.height)), (crop.x, crop.y, crop.width, crop.height));
    assert!((crop.aspect_in(WIDE) - 16.0 / 9.0 * 0.5 / 0.6).abs() < 1e-9);
}
