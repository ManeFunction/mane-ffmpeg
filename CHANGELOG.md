# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]
### Added
- On Windows and Linux, a *Help* menu at the end of the menu bar with *Check for Updates…* and *About Mane FFmpeg*,
  which only the macOS app menu had.
### Changed
- Once the clipping check has marked the timeline, its button turns into *Clear clipping*, which removes the marks.

## [2.3.0] - 2026-11-20
### Added
- **Extract audio:** the export button has a dropdown arrow, that switches it to "Extract audio"
  mode. In that mode the preset menu lists audio presets, and the button saves only the timeline's audio.
- Audio presets, on a new Audio tab in the presets window. Built-in: "extract audio (as is)", "m4a (AAC 256k)" and
  "mp3 (V0)". "extract audio (as is)" copies a single clip's own audio without re-encoding, into the file type its
  codec belongs in (.m4a for AAC and ALAC, .mp3, .flac, .opus, .wav…; .mka for anything else). Several clips,
  replaced audio, or offsets that need silence are encoded losslessly to FLAC. A file name you chose is checked against
  what will be written: audio that the file type can't hold is encoded with the preset's fallback, or the export
  says which file type to use.
- **A2: a second audio track** under A1, for background music and voice-overs. Drag an audio file from the Audio tab (or
  from Finder or Explorer) onto the A2 row to mix it in at that point. A2 clips can be moved, trimmed, split and
  deleted; hold Shift while moving a clip or dragging an edge to snap it where the playhead snaps (clip and audio
  edges).
- Volume for each clip: Selection has a 0–200 % slider for the selected V1 or A2 clips, a 100 % reset, and a button
  that applies the volume more widely. For a clip playing its own sound it reaches the other clips of that video
  that still play their own sound (not those whose sound was replaced). For an audio file it reaches every place
  that file plays, on A1 and A2. A changed volume shows as a
  badge on the clip and scales its waveform. The preview applies it live while the slider moves. Audio whose volume
  is changed is re-encoded, using the preset's audio fallback where the preset copies audio.
- **Level meters** left of the timeline: one peak bar per channel of what the preview plays (clip volumes and A2
  included, the preview's own volume not), on a dBFS scale with a peak-hold tick. A red marker on top of a bar lights
  up when that channel clips (reaches full scale) and stays lit until you click the meters. The strip is as wide as
  the channels need: mono, stereo, 5.1…
- **Track volume:** the A1 and A2 labels on the timeline are buttons. Clicking one selects that whole track, and
  Selection then has a 0–200 % volume for it. The track volume is applied on top of each clip's own volume, so an 80 %
  clip on a 50 % track plays at 40 %. It is part of the project (undoable, saved) and is applied in the preview,
  the level meters, the waveforms and the export.
- **Quick mute:** click the speaker icon next to the preview's volume slider to mute or unmute the preview. Unmuting
  returns to the volume it had before; if the slider was dragged down to 0 %, it goes back to 100 %. While muted, the
  icon shows a crossed-out speaker.
- **Check clipping:** a button above the timeline checks the whole timeline's audio for clipping, mixed exactly as it
  would be exported, with every clip, A1 and A2 volume applied. Only the audio is decoded, so it runs at hundreds of
  times real time (a 20-minute timeline takes about 2 seconds). Clipped places are marked red on the ruler and over the
  audio rows, and the status line gives how many there are and the loudest peak. After an edit the marks fade until
  the next check. Click the button again while it runs to cancel.

### Changed
- Projects with clip or track volumes or A2 clips are saved as format 4, which older versions refuse to open rather than
  silently dropping them.
- The presets window has Video and Audio tabs. Existing presets are video presets; the built-in audio presets are
  added after them. If an older version saves the presets file in between, the built-in audio presets come back as
  audio presets.
- Hearing A2 in the preview needs a libmpv built on FFmpeg 4.4 or newer. With an older one the preview plays A1 alone
  and says so; the export still mixes A2.
### Fixed
- On a busy machine the preview could put the playhead on the next keyframe (up to a GOP later) after loading a
  timeline with changed clip volumes or A2, or after a change to the mix. It now checks where the preview landed and
  seeks back again.

## [2.2.2] - 2026-11-18
> [!IMPORTANT]
> ⚠️ **If you are on Windows, please update to this version manually!**
> Previous releases have broken built-in updater.

### Fixed
- Windows: a downloaded update now installs wherever the app was unpacked (the Desktop, Downloads, any folder).
  Started from Explorer, the app passed its own folder to the update as the current folder, and Windows won't rename
  a folder that is in use that way. So the old version stayed, and the same update was offered again after the
  restart. The update now starts outside the app's folder. When something else still holds the folder (a Command
  Prompt opened there, OneDrive), it replaces the files inside the folder instead, and puts the old ones back if a
  file stays locked. update.log now says which file couldn't be moved, and why.

## [2.2.1] - 2026-11-18
### Added
- Settings has a "Check Now" button next to "Check for updates at startup". It runs the same check as the menu's
  "Check for Updates…", and the update window opens over Settings.
- Windows: when the app runs as administrator, the Files panel warns that files can't be dropped from Explorer
  (Windows blocks that), and suggests "Add videos…" or starting the app normally.

### Changed
- Clicking a clip on the timeline (V1 or A1) only selects it and leaves the playhead where it is; clicking an empty
  part of the track rows clears the selection without seeking. The playhead now moves only from the ruler above V1.

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
