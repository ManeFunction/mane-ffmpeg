# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]
### Added
- **share** built-in preset for everyday video to post or send: H.264 + AAC in an MP4 that plays on any phone or
  browser. Existing presets files gain it after *harsh*.

### Fixed
- The UI no longer freezes while the preview plays: buttons like play/pause could take seconds to respond on older
  Macs. Drawing a frame no longer blocks the UI, and 1080p60 video plays at close to its full frame rate instead of
  about 22 fps.

## [0.9.0] - 2026-10-26
### Added
- First release: a small macOS/Linux video editor on top of the system ffmpeg, with a libmpv preview.
- Timeline with a video row and a linked audio row with waveforms: split, delete, reorder and trim clips on whole
  frames, with undo/redo, zoom and scroll.
- Audio offsets that slip a clip's audio without moving its edges, per clip or for every clip of a video.
- Preview of the whole timeline as it will be exported, and of a single source with In/Out marks.
- Editable ffmpeg presets with three built-ins (*for storage*, *harsh*, *plane copy*), and an export bar with
  progress, ETA, pause/resume and cancel. A notification appears when an export ends, and the computer stays awake
  while it runs.
- `*.mfvp` projects as readable JSON that survive moving the project folder, with relinking of missing files.
- Compact mode, several windows, and an offer to install a missing ffmpeg or libmpv with Homebrew on macOS.
- Downloads on the GitHub releases page: a macOS app (Apple Silicon and Intel) and a Linux tarball (x64 and arm64).
