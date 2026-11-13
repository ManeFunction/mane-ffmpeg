//! Shows an mpv player using mpv's software renderer. A render thread draws each new frame into a buffer of its
//! own sized to the view's pixels (mpv scales and letterboxes), then swaps it with the latest finished frame. The UI
//! thread uploads that frame into a BGRA wgpu texture, so no per-pixel work runs on the UI thread and mpv draws
//! outside the lock the UI takes, so a slow frame never holds up clicks.

use eframe::egui_wgpu::{self, wgpu};
use egui::{Color32, Rect, TextureId, Vec2};
use mane_core::mpv::player::MpvPlayer;
use mane_core::mpv::sw_renderer::SoftwareRenderer;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Condvar, Mutex};
use std::thread::JoinHandle;

pub const MAX_PIXELS: f32 = 1920.0 * 1080.0;

#[derive(Default)]
struct Frame {
    pixels: Vec<u8>,
    width: u32,
    height: u32,
    /// Holds a whole rendered frame not uploaded yet.
    fresh: bool,
}

struct Shared {
    latest: Mutex<Frame>,
    wake: Mutex<bool>,
    wake_cv: Condvar,
    stop: AtomicBool,
    /// Wanted size, packed as (w << 32 | h).
    target: AtomicU64,
    resized: AtomicBool,
    rendered: AtomicU64,
}

impl Shared {
    fn signal(&self) {
        *self.wake.lock().unwrap() = true;
        self.wake_cv.notify_one();
    }
}

/// A frame source bound to one player (create it after the player, drop it before).
pub struct VideoRenderer {
    shared: Arc<Shared>,
    thread: Option<JoinHandle<()>>,
}

impl VideoRenderer {
    pub fn new(player: &MpvPlayer, wake: Arc<dyn Fn() + Send + Sync>) -> Result<VideoRenderer, String> {
        let shared = Arc::new(Shared {
            latest: Mutex::new(Frame::default()),
            wake: Mutex::new(false),
            wake_cv: Condvar::new(),
            stop: AtomicBool::new(false),
            target: AtomicU64::new(0),
            resized: AtomicBool::new(false),
            rendered: AtomicU64::new(0),
        });
        let signal = shared.clone();
        let renderer = SoftwareRenderer::new(player, Box::new(move || signal.signal())).map_err(|e| e.0)?;
        let wake_render = wake;
        let loop_shared = shared.clone();
        let thread = std::thread::Builder::new()
            .name("mpv render".into())
            .spawn(move || render_loop(renderer, loop_shared, wake_render))
            .map_err(|e| e.to_string())?;
        Ok(VideoRenderer { shared, thread: Some(thread) })
    }

    /// Asks for frames of this many pixels (even sizes, capped at [`MAX_PIXELS`]).
    pub fn set_target(&self, mut width: f32, mut height: f32) {
        if width < 2.0 || height < 2.0 {
            return;
        }
        let area = width * height;
        if area > MAX_PIXELS {
            let factor = (MAX_PIXELS / area).sqrt();
            width *= factor;
            height *= factor;
        }
        let (w, h) = ((width as u64) / 2 * 2, (height as u64) / 2 * 2);
        let packed = (w << 32) | h;
        if self.shared.target.swap(packed, Ordering::AcqRel) != packed {
            self.shared.resized.store(true, Ordering::Release);
            self.shared.signal();
        }
    }

    pub fn rendered_frames(&self) -> u64 {
        self.shared.rendered.load(Ordering::Relaxed)
    }

    /// The center pixel of the latest frame, for debug automation.
    pub fn center_pixel(&self) -> String {
        let f = self.shared.latest.lock().unwrap();
        if f.width == 0 {
            return "none".into();
        }
        let i = ((f.height / 2 * f.width + f.width / 2) * 4) as usize;
        let p = &f.pixels[i..i + 4];
        format!("{}x{} bgra=({},{},{},{})", f.width, f.height, p[0], p[1], p[2], p[3])
    }
}

impl Drop for VideoRenderer {
    fn drop(&mut self) {
        self.shared.stop.store(true, Ordering::Release);
        self.shared.signal();
        if let Some(t) = self.thread.take() {
            let _ = t.join();
        }
    }
}

fn render_loop(renderer: SoftwareRenderer, shared: Arc<Shared>, wake: Arc<dyn Fn() + Send + Sync>) {
    let mut drawing = Frame::default();
    loop {
        {
            let mut woke = shared.wake.lock().unwrap();
            while !*woke && !shared.stop.load(Ordering::Acquire) {
                woke = shared.wake_cv.wait(woke).unwrap();
            }
            *woke = false;
        }
        if shared.stop.load(Ordering::Acquire) {
            break;
        }
        let resized = shared.resized.swap(false, Ordering::AcqRel);
        let new_frame = renderer.update();
        if !new_frame && !resized {
            continue;
        }
        let packed = shared.target.load(Ordering::Acquire);
        let (width, height) = ((packed >> 32) as u32, (packed & 0xffff_ffff) as u32);
        if width == 0 || height == 0 {
            continue;
        }
        let len = (width * height * 4) as usize;
        if drawing.pixels.len() != len {
            drawing.pixels = vec![0; len];
        }
        drawing.width = width;
        drawing.height = height;
        if !renderer.render(width as i32, height as i32, (width * 4) as usize, &mut drawing.pixels) {
            continue;
        }
        shared.rendered.fetch_add(1, Ordering::Relaxed);
        {
            let mut latest = shared.latest.lock().unwrap();
            std::mem::swap(&mut *latest, &mut drawing);
            latest.fresh = true;
        }
        wake();
    }
    drop(renderer);
}

/// The GPU side: a texture that the latest frame is copied into, registered with egui.
#[derive(Default)]
pub struct VideoTexture {
    texture: Option<(wgpu::Texture, TextureId, u32, u32)>,
}

impl VideoTexture {
    /// Uploads a fresh frame, if any. Returns the texture and its pixel size.
    pub fn update(&mut self, state: &egui_wgpu::RenderState, source: &VideoRenderer) -> Option<(TextureId, Vec2)> {
        let mut frame = source.shared.latest.lock().unwrap();
        if frame.fresh && frame.width > 0 {
            frame.fresh = false;
            let (w, h) = (frame.width, frame.height);
            if !matches!(self.texture, Some((_, _, tw, th)) if tw == w && th == h) {
                let texture = state.device.create_texture(&wgpu::TextureDescriptor {
                    label: Some("mpv frame"),
                    size: wgpu::Extent3d { width: w, height: h, depth_or_array_layers: 1 },
                    mip_level_count: 1,
                    sample_count: 1,
                    dimension: wgpu::TextureDimension::D2,
                    // sRGB, as egui's own textures: the shader decodes it and the colors stay as mpv drew them.
                    format: wgpu::TextureFormat::Bgra8UnormSrgb,
                    usage: wgpu::TextureUsages::TEXTURE_BINDING | wgpu::TextureUsages::COPY_DST,
                    view_formats: &[],
                });
                let view = texture.create_view(&wgpu::TextureViewDescriptor::default());
                let mut renderer = state.renderer.write();
                let id = match self.texture.take() {
                    Some((_, id, _, _)) => {
                        renderer.update_egui_texture_from_wgpu_texture(&state.device, &view, wgpu::FilterMode::Linear, id);
                        id
                    }
                    None => renderer.register_native_texture(&state.device, &view, wgpu::FilterMode::Linear),
                };
                self.texture = Some((texture, id, w, h));
            }
            let (texture, ..) = self.texture.as_ref().unwrap();
            state.queue.write_texture(
                wgpu::TexelCopyTextureInfo { texture, mip_level: 0, origin: wgpu::Origin3d::ZERO, aspect: wgpu::TextureAspect::All },
                &frame.pixels,
                wgpu::TexelCopyBufferLayout { offset: 0, bytes_per_row: Some(w * 4), rows_per_image: Some(h) },
                wgpu::Extent3d { width: w, height: h, depth_or_array_layers: 1 },
            );
        }
        self.texture.as_ref().map(|(_, id, w, h)| (*id, Vec2::new(*w as f32, *h as f32)))
    }

    /// Frees the texture (the view went away).
    pub fn free(&mut self, state: &egui_wgpu::RenderState) {
        if let Some((_, id, _, _)) = self.texture.take() {
            state.renderer.write().free_texture(&id);
        }
    }
}

/// Paints `source` (a sub-rectangle of the texture, in its pixels) into `dest`.
pub fn paint(painter: &egui::Painter, id: TextureId, size: Vec2, source: Rect, dest: Rect) {
    let uv = Rect::from_min_max(
        egui::pos2(source.min.x / size.x, source.min.y / size.y),
        egui::pos2(source.max.x / size.x, source.max.y / size.y),
    );
    painter.image(id, dest, uv, Color32::WHITE);
}
