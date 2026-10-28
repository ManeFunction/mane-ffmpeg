# Mane FFmpeg

A small macOS/Linux video editor on top of your system **ffmpeg**. Load one or more videos, cut and reorder them on a timeline, nudge a clip's audio to fix desync, preview the result, and export it with your own ffmpeg presets. Every ffmpeg and ffprobe call goes through [CliWrap](https://github.com/Tyrrrz/CliWrap). The preview runs on **libmpv**.

Built with C#, .NET 10 and Avalonia 12.

![](https://raw.githubusercontent.com/wiki/ManeFunction/mane-ffmpeg/mane-ffmpeg-UI.png)

## Features

- **Timeline**
  - A video row (V1) and a linked audio row (A1) with waveforms.
  - Split at the playhead (S), delete, drag clips to reorder, and drag clip edges to trim. Cuts land on whole frames.
  - Undo and redo, zoom, and scroll.
- **Audio offset**
  - Drag a clip on the audio row, use Alt+←/→, or type milliseconds in the inspector.
  - The offset *slips* the clip's audio. Clip edges stay put, and the sound comes from slightly earlier or later in the same file, so cuts never open gaps.
  - Silence is added only where the shifted audio runs past the start or end of the file (hatched on A1).
  - "Use this offset for all clips of this video" shifts a whole recording.
- **Preview**
  - Smooth playback, exact seeking and frame stepping via libmpv.
  - The timeline plays as a single virtual file (an mpv EDL with separate video and audio streams), so cuts and audio offsets sound exactly as they will be exported.
  - Double-click a video in the list to preview it on its own, mark In/Out (I/O) and add the range (Enter).
- **Presets**
  - Raw ffmpeg output options, editable in *Presets → Edit Presets…*. Built in:
    - **for storage** (default): `-c:v libx265 -crf 23 -preset slow -pix_fmt yuv420p10le -x265-params aq-mode=3:psy-rdoq=1.0:no-sao=1 -c:a copy` → `.mp4`. 10-bit HEVC tuned to keep detail, so moving objects don't smear.
    - **harsh**: `-c:v libx265 -crf 28 -preset slow -c:a copy` → `.mp4`. Smaller files, softer in motion.
    - **share**: `-c:v libx264 -crf 20 -preset medium -profile:v high -pix_fmt yuv420p -c:a aac -b:a 192k -movflags +faststart` → `.mp4`. Everyday video for messengers and social media: H.264 + AAC plays everywhere and starts before it finishes downloading.
    - **plane copy**: `-c:v copy -c:a copy` → same container as the source
  - Add, duplicate, delete, or restore the built-ins.
- **Export**
  - The progress bar, ETA, speed and Cancel button all stay inside the window.
  - A system notification appears when an export finishes or fails.
  - The computer doesn't go to sleep on its own while an export runs. The display can still turn off.
  - "ffmpeg command" shows the exact commands that will run.
- **Projects**
  - `*.mfvp` files are readable JSON. They store edits, the export choice and the window layout, but no media.
  - Source paths are saved both absolute and relative to the project, so you can move a folder that holds the project and its videos together. Missing files can be relinked.
- **Compact mode**
  - The *Compact* button or ⇧⌘C hides the video preview and shrinks the window from the top, so the timeline and export controls stay where they are on screen. The video list and selection inspector sit side by side, with play/pause in the timeline toolbar.
  - Only audio is decoded while compact. The mode is saved with the project.
- **Several windows**
  - File → New Window. Each window is its own project with its own player and export, so you can export in one window while editing in another.

## Installation

Download the latest build from the [Releases page](https://github.com/ManeFunction/mane-ffmpeg/releases/latest). The builds are self-contained, so you don't need .NET. You still need ffmpeg and libmpv, see [Requirements](#requirements).

- **macOS**: `mane-ffmpeg-<version>-osx-arm64.zip` for Apple Silicon or `osx-x64` for Intel. Unzip it and move *Mane FFmpeg.app* to Applications. The app is signed ad hoc but not notarized, so macOS blocks the first start. Run `xattr -dr com.apple.quarantine "/Applications/Mane FFmpeg.app"`, or allow it in *System Settings → Privacy & Security*.
- **Linux**: `mane-ffmpeg-<version>-linux-x64.tar.gz` or `linux-arm64`. Run `tar xf mane-ffmpeg-*.tar.gz && ./mane-ffmpeg/install.sh`. It installs for the current user into `~/.local/opt/mane-ffmpeg`, with a menu entry, the `.mfvp` file type and a `mane-ffmpeg` command in `~/.local/bin`.

## Requirements

| | macOS | Linux |
|---|---|---|
| ffmpeg + ffprobe (5.0+) | `brew install ffmpeg` | `sudo apt install ffmpeg` (or your distro's package) |
| libmpv (0.33+) for preview | IINA.app installed (its libmpv is used), or `brew install mpv` | `sudo apt install libmpv2` · `dnf install mpv-libs` · `pacman -S mpv` |
| Notifications | built in (`osascript`) | `notify-send` (libnotify) or `gdbus` |
| Staying awake during exports | built in (IOKit power assertion) | `systemd-inhibit` (systemd) |

The app finds ffmpeg in your PATH, `/opt/homebrew/bin`, `/usr/local/bin`, `/usr/bin` and similar places. Apps started from Finder don't get your shell PATH, so this matters. You can also set the paths in **Settings**.

On macOS, when ffmpeg or libmpv is missing, the app offers to install it with Homebrew at startup, or later from **Settings → Install Missing Components…**. If Homebrew itself is missing, its official installer opens in Terminal first, because it asks for your password. `brew install` then runs inside the dialog with its output shown. On macOS versions Homebrew no longer supports, it builds from source, which can take an hour or more.

Without libmpv, editing and exporting still work. The preview pane then shows an install hint.

## Build and run

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). On Intel Macs, where Homebrew would compile it, the official script is simplest:

```sh
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0   # installs to ~/.dotnet
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH
```

```sh
dotnet run --project src/ManeFfmpeg.App            # run
dotnet test                                        # unit + ffmpeg integration tests
scripts/package-macos.sh                           # → build/macos-<rid>/Mane FFmpeg.app
scripts/package-linux.sh                           # → build/linux-<rid>/mane-ffmpeg-<version>-<rid>.tar.gz
```

The preview player's tests run against a headless libmpv and are skipped when it can't be loaded. With IINA's copy on macOS, start the run with `DYLD_FALLBACK_LIBRARY_PATH=/Applications/IINA.app/Contents/Frameworks:$HOME/lib:/usr/local/lib:/usr/lib dotnet test`.

The packaged apps are self-contained, so no .NET install is needed to run them. The macOS bundle registers the `.mfvp` file type, so double-clicking a project opens it. **Settings** has a button that registers or unregisters `.mfvp` projects with the app. On macOS it sets the Launch Services default; on Linux it installs the mime type and sets the default app for the current user. When projects aren't registered, the app offers to register them at startup, after the setup step. That prompt has a "Do not show anymore" checkbox, which **Settings** can undo.

## How exports work

The planner (`src/ManeFfmpeg.Core/Ffmpeg/ExportPlanner.cs`) picks one of four strategies. The exact commands are shown in the export bar.

| Situation | What runs |
|---|---|
| One untouched clip (whole file, no offset) | Your preset as-is: `ffmpeg -i SRC <preset> OUT` |
| One trimmed or offset clip, preset copies audio | The audio is cut losslessly first, then muxed with the encoded video, so `-c:a copy` is kept |
| Several clips or offsets, video encoded | One `-filter_complex` with trimmed inputs, per-clip audio slip (`adelay`/`apad`/`atrim`) and `concat`. `-c:a copy` falls back to the preset's audio fallback (default `-c:a aac -b:a 256k`) |
| Video copied ("plane copy") | ffconcat stream copy. Cuts start on the keyframe at or before each in point (turn on *Snap to keyframes* to see exact cuts). With audio offsets, only the audio is re-encoded |

Some details:
- Clips from differently sized sources are scaled and padded to the first clip's size and frame rate.
- `-tag:v hvc1` and `-movflags +faststart` are added for HEVC in MP4/MOV, so QuickTime can play the file.
- MPEG-TS and other containers with unreliable seeking are handled by seeking early and trimming exactly.

The integration tests check A/V sync of real exports. They use synthetic video with a flash and a beep every 2 s, then measure where the flashes and beeps land after cutting, reordering and offsetting.

## Keyboard

| Key | Action |
|---|---|
| Space / K | Play / pause |
| ← / → | Previous / next frame |
| Shift+← / → | ∓1 second |
| J / L | ∓5 seconds |
| ↑ / ↓ | Previous / next cut |
| Home / End | Start / end |
| S (or B) | Split at the playhead |
| Delete / Backspace | Delete the selected clips |
| Alt+← / → | Audio 10 ms earlier / later |
| Alt+Shift+← / → | Audio 1 frame earlier / later |
| I / O / Enter | Mark In / Out / add the range (source preview) |
| Esc | Back to the timeline / clear the selection; closes Presets, Settings and message dialogs (as Cancel) |
| ⌘Z / ⇧⌘Z | Undo / redo (Ctrl on Linux) |
| ⌘N, ⌘O, ⌘S, ⇧⌘S, ⌘E, ⌘W | New window, open, save, save as, export, close |
| ⌘+ / ⌘− / ⌘0, or ⌘/Ctrl+wheel | Zoom in, zoom out, fit |
| ⇧⌘C (Ctrl+Shift+C on Linux) | Compact mode: hide the video preview (also the *Compact* button) |

Mouse:
- Click the ruler or a clip to seek; drag on the ruler to scrub.
- Click a clip to select it; Shift extends the selection, ⌘/Ctrl toggles a clip in or out of it.
- Drag a clip to reorder it, drag its edges to trim, and drag on the audio row to slip its audio (hold Shift for fine steps).

## Project layout

```
src/ManeFfmpeg.Core   model, editing, ffprobe/ffmpeg (planner, runner), waveforms, keyframes, libmpv binding, persistence
src/ManeFfmpeg.App    Avalonia UI: windows, view models, timeline control, mpv video view
tests/                xUnit unit tests and real-ffmpeg integration tests
scripts/              macOS .app and Linux tarball packaging
```

Files on disk:
- Settings and presets: `~/Library/Application Support/Mane FFmpeg/` (Linux: `~/.config/mane-ffmpeg/`).
- Caches (waveforms, the silence file used by previews): `~/Library/Caches/Mane FFmpeg/` (Linux: `~/.cache/mane-ffmpeg/`).
- Log: `~/Library/Logs/Mane FFmpeg/app.log` (Linux: `~/.cache/mane-ffmpeg/app.log`).

## Notes and limitations

- **libmpv from IINA.** It resolves its dependencies through `@rpath`, so the app needs `DYLD_FALLBACK_LIBRARY_PATH` set when it starts.
  - The packaged app sets it in `Info.plist`.
  - `dotnet run` relaunches itself once with the variable set.
- **Stream-copy cuts.** They can only start on keyframes, and boundaries may show a frame of artifacts with open-GOP sources. That's inherent to cutting without re-encoding.
- **Sleeping display.** The app won't start while the display is asleep, because Avalonia's macOS backend needs an active display.
- **Development automation.** Debug builds can be driven by a script (`MANE_FFMPEG_SCRIPT=file`) for end-to-end checks. See `src/ManeFfmpeg.App/Services/DebugAutomation.cs`.

## Repository info

This repo follows the [Conventional Commits](https://www.conventionalcommits.org/) specification. Changes are listed in [CHANGELOG.md](CHANGELOG.md).

[![GitHub release (latest by date)](https://img.shields.io/github/v/release/ManeFunction/mane-ffmpeg)](https://github.com/ManeFunction/mane-ffmpeg/releases/latest)
[![GitHub All Releases](https://img.shields.io/github/downloads/ManeFunction/mane-ffmpeg/total)](https://github.com/ManeFunction/mane-ffmpeg/releases)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/ManeFunction?label=Sponsor&logo=GitHubSponsors&style=flat)](https://github.com/sponsors/ManeFunction)
