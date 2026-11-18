# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]
### Added
- Settings has a "Check Now" button next to "Check for updates at startup". It runs the same check as the menu's
  "Check for Updates…", and the update window opens over Settings.

### Fixed
- In Settings, the Register/Unregister button keeps its width when its label changes, so the buttons beside it no
  longer jump.
- In the "Install Missing Components" window, the installer's output wraps long lines instead of needing a
  horizontal scroll to read them.
- Windows: installing ffmpeg no longer fails with "exit code -1978335189" when winget already has it. winget
  reports that as "no newer version"; the app now treats it as installed. The app also finds a winget ffmpeg that was
  added to PATH after the app started, or that was unpacked without its shortcut in WinGet\Links (which happens without
  admin rights or Developer Mode), so it no longer has to be restarted as administrator.

## [2.2.0] - 2026-11-18
### Added
- The audio row (A1) shows a small animated "Loading…" while a clip's file is analyzed and its waveform is built,
  instead of staying empty until the waveform appears.
- Windows builds (`win-x64`, `win-arm64`) with every feature of the macOS and Linux ones: preview with a bundled
  libmpv, export with pause and resume, keeping the computer awake, notifications, the `.mfvp` file type and updates
  that install themselves. When ffmpeg is missing, the app offers to install it with winget.

### Changed
- Everything that differs between macOS, Linux and Windows now sits behind one platform layer
  (`src/ManeFfmpeg.Core/Platforms`), with one implementation per system.
- Projects store the relative paths of their media with `/` on every system, so a project made on Windows still
  finds its media on macOS and Linux.
- Tooltips show shortcuts the way the system writes them: ⇧⌘C on macOS, Ctrl+Shift+C on Linux and Windows.

### Fixed
- During playback near the end of the project, the timeline no longer scrolls the end to the left edge and leaves
  the rest of the view empty. It scrolls only while clips lie past the right edge, and stops with the project's end
  at the right edge.
- The audio row's waveform lines up with the sound you hear and ends where the audio ends. For files whose audio
  timestamps have gaps or bursts of packets (some recorders write them), it drifted later than the sound and ran
  past the clip's end. Waveforms cached by earlier versions are rebuilt.
- With ffmpeg 9, exporting a single clip whose AAC audio is copied without re-encoding no longer plays the audio
  about a frame (~30 ms) early.
- Exporting a single clip whose audio is copied without re-encoding no longer plays the audio up to one audio packet
  early (up to 85 ms for ALAC, up to 21 ms for AAC).

## [2.1.1] - 2026-11-12
### Changed
- While cropping, the preview's close button (✕) cancels the crop: it goes back to the crop and shape you entered
  crop mode with, and the crop changes made meanwhile leave the undo history, while the steps before stay. Outside crop
  mode it still hides the preview. Hiding the preview while cropping (the *Hide preview* button or ⇧⌘C) cancels the
  crop the same way, instead of keeping it.

### Fixed
- The crop rectangle can be dragged from anywhere inside it to move it, and its edges grabbed from inside; only
  the border line and the handles reacted to the mouse.
- The crop size shown on the rectangle, in the preview title and in the status bar is the size exported. For
  videos with non-square pixels (anamorphic HDV/DV) and odd sizes it showed the size on screen instead.
- Projects exported with your own *plane copy but mp4* preset open with *copy to mp4 (w/o encode)*, which it became
  in 2.0.0, instead of asking to add it again. Any project whose preset got another id finds a preset with exactly
  the same options.
- *Edit → Reset Crop* is disabled while nothing is cropped, instead of reporting "Crop removed".

## [2.1.0] - 2026-11-12
### Added
- Crop the picture right on the preview: click *Crop* (or press C), then drag the rectangle, its edges or corners, or
  drag outside it to draw a new one. Hold Shift to keep its shape, or pick *Original*, 16:9, 9:16, 4:3, 4:5 or 1:1.
  The rectangle shows the output size in pixels. *Done* (Enter or Esc) shows the cropped picture, rendered larger so it
  stays sharp. *Edit → Crop Picture* and *Reset Crop* do the same from the menu, and each drag can be undone.
- Exports cut the crop from the joined picture (after clips of other sizes are fitted to the first) with ffmpeg's
  `crop`, before the preset's own `-vf`. Presets that copy video refuse a cropped project. Projects with a crop are
  saved as format 3, which older versions refuse to open instead of silently dropping the crop.

### Fixed
- Labels in dropdown lists are centred in their rows; they sat 1.5 to 2.5 px high.

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
