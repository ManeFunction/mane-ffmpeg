#!/usr/bin/env bash
# Builds a self-contained Windows folder into "build/windows-<rid>/Mane FFmpeg" and a .zip next to it, with libmpv bundled.
# Usage: scripts/package-windows.sh [win-x64|win-arm64]   (default: win-x64)
#        scripts/package-windows.sh --libmpv-only <rid> <dir>   (just fetches and checks libmpv-2.dll into <dir>; CI uses it)
# Needs: dotnet, curl, 7z (preinstalled on GitHub's Windows runners; brew install sevenzip / apt install 7zip elsewhere).
# On the target machine: unzip anywhere and run "Mane FFmpeg\ManeFfmpeg.exe". ffmpeg comes from winget (the app offers it).
set -euo pipefail

# The bundled libmpv: zhongfly's LGPL build (mpv and FFmpeg under the LGPL, no GPL parts), pinned by name and SHA-256.
# Upstream keeps about a month of daily builds, so the files are fetched from LIBMPV_BASE_URL; update all four lines
# together. Sources: https://github.com/zhongfly/mpv-winbuild (build scripts), https://github.com/mpv-player/mpv.
LIBMPV_BASE_URL="${LIBMPV_BASE_URL:-https://github.com/zhongfly/mpv-winbuild/releases/download/2026-11-15-bb4823e5f0}"
LIBMPV_X64="mpv-dev-lgpl-x86_64-20261115-git-bb4823e5f0.7z"
LIBMPV_X64_SHA256="e97e8d95c8c6cc4c9f98b87809d51f794dc0260174a3ab8187ae4881075238e1"
LIBMPV_ARM64="mpv-dev-lgpl-aarch64-20261115-git-bb4823e5f0.7z"
LIBMPV_ARM64_SHA256="a2760187fb99ca9d760a42329336a9f894e4dec783f731def04154ca8aaf8744"

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
# 7zz is current 7-Zip; p7zip's old 7z can't unpack the arm64 archive (its ARM64 filter came later).
SEVENZIP="$(command -v 7zz || command -v 7z || command -v 7za || true)"

sha256() {
  if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi
}

# fetch_libmpv <rid> <dir>: downloads the pinned archive, checks it and puts libmpv-2.dll into <dir>.
fetch_libmpv() {
  local rid="$1" dest="$2" archive expected
  case "$rid" in
    win-x64) archive="$LIBMPV_X64"; expected="$LIBMPV_X64_SHA256" ;;
    win-arm64) archive="$LIBMPV_ARM64"; expected="$LIBMPV_ARM64_SHA256" ;;
    *) echo "Unknown runtime identifier: $rid" >&2; exit 1 ;;
  esac
  [ -n "$SEVENZIP" ] || { echo "7z is needed to unpack libmpv." >&2; exit 1; }
  local work
  work="$(mktemp -d)"
  curl -fsSL --retry 3 -o "$work/$archive" "$LIBMPV_BASE_URL/$archive"
  local actual
  actual="$(sha256 "$work/$archive")"
  if [ "$actual" != "$expected" ]; then
    echo "libmpv checksum mismatch for $archive: expected $expected, got $actual" >&2
    rm -rf "$work"
    exit 1
  fi
  mkdir -p "$dest"
  "$SEVENZIP" e -y -bso0 -bsp0 "-o$work/out" "$work/$archive" libmpv-2.dll
  cp "$work/out/libmpv-2.dll" "$dest/"
  rm -rf "$work"
  echo "libmpv: $archive -> $dest/libmpv-2.dll"
}

if [ "${1:-}" = "--libmpv-only" ]; then
  fetch_libmpv "${2:?rid}" "${3:?dir}"
  exit 0
fi

RID="${1:-win-x64}"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props")"
OUT="$ROOT/build/windows-$RID"
DIR="$OUT/Mane FFmpeg"
DOTNET="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"

rm -rf "$OUT"
mkdir -p "$DIR"

# One ManeFfmpeg.exe with the runtime and Avalonia's native libraries inside; libmpv stays a separate DLL next to it.
"$DOTNET" publish "$ROOT/src/ManeFfmpeg.UI/ManeFfmpeg.UI.csproj" -c Release -r "$RID" --self-contained true \
  -p:UseAppHost=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none \
  -o "$DIR"
# SkiaSharp and HarfBuzzSharp ship their native debug symbols as content (about 100 MB); users don't need them.
rm -f "$DIR"/*.pdb

fetch_libmpv "$RID" "$DIR"

# The app updates itself only in a folder carrying this marker (WindowsPlatform.PackageMarker).
: > "$DIR/.mane-ffmpeg-package"

cat > "$DIR/THIRD-PARTY-NOTICES.txt" <<'NOTICES'
Mane FFmpeg for Windows includes libmpv-2.dll, a build of mpv (https://mpv.io) with FFmpeg (https://ffmpeg.org)
and other libraries, made by https://github.com/zhongfly/mpv-winbuild in its LGPL configuration.

mpv and FFmpeg are free software under the GNU Lesser General Public License, version 2.1 or later
(https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html). Their source code is available at
https://github.com/mpv-player/mpv and https://git.ffmpeg.org/ffmpeg.git, and the build scripts with the exact
versions used at https://github.com/zhongfly/mpv-winbuild.

You can replace libmpv-2.dll with another libmpv build, or point Settings > libmpv path at one.

Mane FFmpeg runs the ffmpeg you install yourself; it doesn't include ffmpeg.exe.
NOTICES

(cd "$OUT" && "$SEVENZIP" a -tzip -bso0 -bsp0 "mane-ffmpeg-$VERSION-$RID.zip" "Mane FFmpeg")
echo "Built: $OUT/mane-ffmpeg-$VERSION-$RID.zip"
