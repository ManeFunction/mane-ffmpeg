#!/usr/bin/env bash
# Builds a self-contained Linux bundle into build/linux-<rid>/mane-ffmpeg and a .tar.gz next to it.
# Usage: scripts/package-linux.sh [linux-x64|linux-arm64]   (default: linux-x64)
# On the target machine: tar xf mane-ffmpeg-*.tar.gz && ./mane-ffmpeg/install.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RID="${1:-linux-x64}"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props")"
OUT="$ROOT/build/linux-$RID"
DIR="$OUT/mane-ffmpeg"
DOTNET="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"

rm -rf "$OUT"
mkdir -p "$DIR"

"$DOTNET" publish "$ROOT/src/ManeFfmpeg.App/ManeFfmpeg.App.csproj" -c Release -r "$RID" --self-contained true \
  -p:UseAppHost=true -o "$DIR/app"

cp "$ROOT/src/ManeFfmpeg.App/Assets/app.png" "$DIR/mane-ffmpeg.png"

cat > "$DIR/mane-ffmpeg.desktop" <<'DESKTOP'
[Desktop Entry]
Type=Application
Name=Mane FFmpeg
Comment=Cut, reorder and export videos with your system ffmpeg
Exec=@APPDIR@/app/ManeFfmpeg %F
Icon=mane-ffmpeg
Terminal=false
Categories=AudioVideo;Video;AudioVideoEditing;
MimeType=application/x-mane-ffmpeg-project;video/mp4;video/quicktime;video/x-matroska;video/webm;video/mp2t;
DESKTOP

cat > "$DIR/mane-ffmpeg-mime.xml" <<'MIME'
<?xml version="1.0" encoding="UTF-8"?>
<mime-info xmlns="http://www.freedesktop.org/standards/shared-mime-info">
  <mime-type type="application/x-mane-ffmpeg-project">
    <comment>Mane FFmpeg project</comment>
    <sub-class-of type="application/json"/>
    <glob pattern="*.mfvp"/>
  </mime-type>
</mime-info>
MIME

cat > "$DIR/install.sh" <<'INSTALL'
#!/usr/bin/env bash
# Installs for the current user: ~/.local/opt/mane-ffmpeg plus menu entry, icon and .mfvp file type.
set -euo pipefail
SRC="$(cd "$(dirname "$0")" && pwd)"
DEST="$HOME/.local/opt/mane-ffmpeg"
mkdir -p "$DEST" "$HOME/.local/share/applications" "$HOME/.local/share/icons/hicolor/512x512/apps" "$HOME/.local/share/mime/packages" "$HOME/.local/bin"
cp -R "$SRC/app" "$DEST/"
sed "s|@APPDIR@|$DEST|g" "$SRC/mane-ffmpeg.desktop" > "$HOME/.local/share/applications/mane-ffmpeg.desktop"
cp "$SRC/mane-ffmpeg.png" "$HOME/.local/share/icons/hicolor/512x512/apps/mane-ffmpeg.png"
cp "$SRC/mane-ffmpeg-mime.xml" "$HOME/.local/share/mime/packages/"
ln -sf "$DEST/app/ManeFfmpeg" "$HOME/.local/bin/mane-ffmpeg"
command -v update-mime-database >/dev/null && update-mime-database "$HOME/.local/share/mime" || true
command -v update-desktop-database >/dev/null && update-desktop-database "$HOME/.local/share/applications" || true
echo "Installed. Needs ffmpeg and libmpv (e.g. sudo apt install ffmpeg libmpv2)."
INSTALL
chmod +x "$DIR/install.sh"

tar -C "$OUT" -czf "$OUT/mane-ffmpeg-$VERSION-$RID.tar.gz" mane-ffmpeg
echo "Built: $OUT/mane-ffmpeg-$VERSION-$RID.tar.gz"
