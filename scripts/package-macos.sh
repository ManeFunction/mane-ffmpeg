#!/usr/bin/env bash
# Builds "Mane FFmpeg.app" (self-contained, ad-hoc signed) into build/macos-<rid>/.
# Usage: scripts/package-macos.sh [osx-x64|osx-arm64]   (default: this Mac's architecture)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RID="${1:-$([ "$(uname -m)" = "arm64" ] && echo osx-arm64 || echo osx-x64)}"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props")"
OUT="$ROOT/build/macos-$RID"
APP="$OUT/Mane FFmpeg.app"
DOTNET="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"

rm -rf "$OUT"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

"$DOTNET" publish "$ROOT/src/ManeFfmpeg.UI/ManeFfmpeg.UI.csproj" -c Release -r "$RID" --self-contained true \
  -p:UseAppHost=true -o "$APP/Contents/MacOS"

# Icon: app.png → AppIcon.icns
ICONSET="$OUT/AppIcon.iconset"
mkdir -p "$ICONSET"
for size in 16 32 128 256 512; do
  sips -z $size $size "$ROOT/src/ManeFfmpeg.UI/Assets/app.png" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
  sips -z $((size * 2)) $((size * 2)) "$ROOT/src/ManeFfmpeg.UI/Assets/app.png" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"
rm -rf "$ICONSET"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Mane FFmpeg</string>
  <key>CFBundleDisplayName</key><string>Mane FFmpeg</string>
  <key>CFBundleIdentifier</key><string>com.manefunction.maneffmpeg</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleExecutable</key><string>ManeFfmpeg</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>LSApplicationCategoryType</key><string>public.app-category.video</string>
  <!-- IINA's bundled libmpv resolves its dylibs via @rpath; setting this at launch avoids a self-relaunch,
       which would lose "open document" events. -->
  <key>LSEnvironment</key>
  <dict>
    <key>DYLD_FALLBACK_LIBRARY_PATH</key><string>/Applications/IINA.app/Contents/Frameworks:/usr/local/lib:/usr/lib</string>
  </dict>
  <key>CFBundleDocumentTypes</key>
  <array>
    <dict>
      <key>CFBundleTypeName</key><string>Mane FFmpeg Project</string>
      <key>CFBundleTypeRole</key><string>Editor</string>
      <key>LSHandlerRank</key><string>Owner</string>
      <key>CFBundleTypeIconFile</key><string>AppIcon</string>
      <key>LSItemContentTypes</key><array><string>com.manefunction.mfvp</string></array>
    </dict>
    <dict>
      <key>CFBundleTypeName</key><string>Video</string>
      <key>CFBundleTypeRole</key><string>Viewer</string>
      <key>LSHandlerRank</key><string>Alternate</string>
      <key>LSItemContentTypes</key><array><string>public.movie</string></array>
    </dict>
  </array>
  <key>UTExportedTypeDeclarations</key>
  <array>
    <dict>
      <key>UTTypeIdentifier</key><string>com.manefunction.mfvp</string>
      <key>UTTypeDescription</key><string>Mane FFmpeg Project</string>
      <key>UTTypeConformsTo</key><array><string>public.json</string><string>public.data</string></array>
      <key>UTTypeTagSpecification</key>
      <dict><key>public.filename-extension</key><array><string>mfvp</string></array></dict>
    </dict>
  </array>
</dict>
</plist>
PLIST

# Ad-hoc signature (required on Apple Silicon). No hardened runtime, so libmpv from IINA/Homebrew can load.
codesign --force --deep --sign - "$APP"

echo "Built: $APP"
echo "Install: cp -R \"$APP\" /Applications/"
