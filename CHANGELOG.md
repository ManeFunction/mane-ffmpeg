# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]
### Added
- Crop the picture right on the preview: click *Crop* (or press C), then drag the rectangle, its edges or corners, or
  drag outside it to draw a new one. Hold Shift to keep its shape, or pick *Original*, 16:9, 9:16, 4:3, 4:5 or 1:1.
  The rectangle shows the output size in pixels. *Done* (Enter or Esc) shows the cropped picture, rendered larger so it
  stays sharp. *Edit → Crop Picture* and *Reset Crop* do the same from the menu, and each drag can be undone.
- Exports cut the crop from the joined picture (after clips of other sizes are fitted to the first) with ffmpeg's
  `crop`, before the preset's own `-vf`. Presets that copy video refuse a cropped project. Projects with a crop are
  saved as format 3, which older versions refuse to open instead of silently dropping the crop.

## [2.0.0] - 2026-11-05
### Added
- **Files** panel (was *Videos*) with *Video* and *Audio* tabs. Audio files get their own pastel colors. Add them with
  *+ Add audio…* or *File → Add Audio…* (⇧⌘I), or drop them on the window. Dropped files without video go to the
  *Audio* tab, and videos dropped on the *Audio* tab are added for their sound. Double-click an audio file to hear it.
- Replace a clip's sound: drag an audio file from the *Audio* tab, or straight from Finder, onto a clip. It starts at
  the clip's start and replaces only that clip; split a video first to replace the sound of just a part. Drag the
  audio row to shift it, as with a clip's own audio. Splitting continues the sound on both halves.
- *Loop audio* in *Selection* repeats a replaced sound so the clip has no silence, even when it is shifted.
  *Use original audio* (also in the *Edit* menu) brings the clip's own sound back with its offset.
- Replaced audio is exported sample-exactly, looped or not, and is re-encoded when the preset copies audio. Projects
  that use audio files are saved as format 2, which older versions refuse to open instead of silently dropping the
  replaced sound.
- Hold Shift while dragging the playhead to snap it to clip edges and audio edges: where a clip's sound starts or runs
  out inside it (shifted audio, or audio shorter than the clip) and where looped audio starts again. Pressing or
  releasing Shift mid-drag snaps or lets go at once.
- **copy to mp4 (w/o encode)** built-in preset: stream copy into an MP4. A preset of your own named *plane copy but mp4*
  becomes this one.

### Changed
- Built-in presets are renamed: *for storage (balanced)*, *for share (compatibility)* (was *share*, now second in the
  list), *harsh compression* and *copy (w/o encode)* (was *plane copy*). Existing presets files are updated, except
  names you edited yourself.

### Fixed
- Exports that join clips with different audio formats keep the best of them: the highest sample rate and the widest
  channel layout. Before, everything was converted to the first clip's format, so a mono or low-rate first clip (or an
  audio file dropped on it) downgraded the sound of the whole export.
- *Check for Updates…* could stay greyed out for the rest of the session when something failed at launch between
  finding an update and showing it.
- Restarting for an update no longer stops a running export in one window when a later window answers *Cancel*: exports
  stop only once every window has agreed to close.
- A possible crash when closing a window while the preview was still waiting on a slow seek.

## [1.0.0] - 2026-11-02
### Added
- **share** built-in preset for everyday video to post or send: H.264 + AAC in an MP4 that plays on any phone or
  browser. Existing presets files gain it after *harsh*.
- Updates: at launch the app looks for a newer release on GitHub and shows what changed since your version. It
  downloads and installs the update and restarts, asking to save open projects first and reopening them afterwards,
  or installs it when you quit. "Do not remind for this version" skips a release until the next one, and Settings can
  turn the check off. *Check for Updates…* in the app menu looks right away (even with the automatic check off, and
  even for a release you skipped) and is greyed out as *Checking for Update…* until the check or the update window is done.

### Fixed
- The UI no longer freezes while the preview plays: buttons like play/pause could take seconds to respond on older
  Macs. Drawing a frame no longer blocks the UI, and 1080p60 video plays at close to its full frame rate instead of
  about 22 fps.
- Dragging the playhead along the timeline now updates the preview while the pointer moves. Before, video with long
  gaps between keyframes (H.265 from *for storage*, camera footage) showed no new frame until the pointer stopped,
  because each new position cancelled the one mpv was still decoding.

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
