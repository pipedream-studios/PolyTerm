#!/usr/bin/env bash
#
# Builds PolyTerm.app (App + CLI + MCP server, self-contained) and wraps it in a drag-to-Applications
# .dmg - the macOS counterpart to the Windows MSI, alongside build-portable-tarball.sh's plain
# tarball. One .app/.dmg per RID: .NET has no universal self-contained publish.
#
# Ad-hoc signed only (`codesign --sign -`), not Developer ID signed or notarized - there's no paid
# Apple Developer account behind this yet. Ad-hoc signing still matters: Apple Silicon refuses to
# run unsigned code at all, and a bundle whose signature doesn't match its contents is reported as
# "damaged" with no override. With a valid ad-hoc signature Gatekeeper just says it "can't be
# opened", which the user can override (see the read-me this script puts in the .dmg).
#
# Layout mirrors the tarball: the App in Contents/MacOS, the CLI and MCP server in
# Contents/MacOS/cli and Contents/MacOS/mcp (separate folders because each self-contained publish
# ships its own copy of the runtime). The App finds the MCP server over a named pipe, not by path,
# so their location inside the bundle doesn't matter to it.
#
# Native shims are NOT built here - run ./build-native-shims.sh first, same as for the tarball.
#
# Usage:
#   ./build-app-dmg.sh [--rid osx-x64|osx-arm64] [--smoke-test]

set -euo pipefail

RID="osx-arm64"
SMOKE_TEST=0
BUNDLE_ID="com.polyterm.PolyTerm"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --rid) RID="$2"; shift 2 ;;
    --smoke-test) SMOKE_TEST=1; shift ;;
    *) echo "Unknown argument: $1" >&2; exit 1 ;;
  esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
OUT_DIR="$SCRIPT_DIR/publish/dmg-$RID"
DMG_STAGE="$OUT_DIR/stage"
APP="$DMG_STAGE/PolyTerm.app"
MACOS_DIR="$APP/Contents/MacOS"
RES_DIR="$APP/Contents/Resources"

echo "== Reading version from Directory.Build.props =="
VERSION=$(dotnet msbuild "$REPO_ROOT/src/PolyTerm.App/PolyTerm.App.csproj" -getProperty:Version -nologo)
echo "Version: $VERSION"

rm -rf "$OUT_DIR"
mkdir -p "$MACOS_DIR/cli" "$MACOS_DIR/mcp" "$RES_DIR"

echo "== Publishing PolyTerm.App ($RID, self-contained) into the bundle =="
dotnet publish "$REPO_ROOT/src/PolyTerm.App/PolyTerm.App.csproj" \
  -c Release -r "$RID" --self-contained true \
  -o "$MACOS_DIR"

echo "== Publishing PolyTerm.Cli ($RID, self-contained) into Contents/MacOS/cli =="
dotnet publish "$REPO_ROOT/src/PolyTerm.Cli/PolyTerm.Cli.csproj" \
  -c Release -r "$RID" --self-contained true \
  -o "$MACOS_DIR/cli"

echo "== Publishing PolyTerm.Mcp ($RID, self-contained) into Contents/MacOS/mcp =="
dotnet publish "$REPO_ROOT/src/PolyTerm.Mcp/PolyTerm.Mcp.csproj" \
  -c Release -r "$RID" --self-contained true \
  -o "$MACOS_DIR/mcp"

chmod +x "$MACOS_DIR/PolyTerm" "$MACOS_DIR/cli/polyterm" "$MACOS_DIR/mcp/polyterm-mcp"

for shim in libpolyterm_pty_shim.dylib libpolyterm_rdp_shim.dylib; do
  if [[ -f "$MACOS_DIR/$shim" ]]; then
    echo "Included $shim: $(lipo -archs "$MACOS_DIR/$shim")"
  else
    echo "WARNING: $shim not included - build it under native/ first (./build-native-shims.sh)."
  fi
done

# The app icon only exists as a Windows .ico (largest image 256x256). sips reads .ico and picks the
# largest image; iconutil then wants an .iconset of fixed sizes - anything above 256 would just be
# upscaled, so the set stops at 256 (iconutil accepts a partial set).
echo "== Building AppIcon.icns from src/PolyTerm.App/Assets/app.ico =="
ICONSET="$OUT_DIR/AppIcon.iconset"
mkdir -p "$ICONSET"
sips -s format png "$REPO_ROOT/src/PolyTerm.App/Assets/app.ico" --out "$OUT_DIR/icon-256.png" >/dev/null
for size in 16 32 128 256; do
  sips -z "$size" "$size" "$OUT_DIR/icon-256.png" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
done
sips -z 32 32 "$OUT_DIR/icon-256.png" --out "$ICONSET/icon_16x16@2x.png" >/dev/null
sips -z 64 64 "$OUT_DIR/icon-256.png" --out "$ICONSET/icon_32x32@2x.png" >/dev/null
sips -z 256 256 "$OUT_DIR/icon-256.png" --out "$ICONSET/icon_128x128@2x.png" >/dev/null
iconutil -c icns "$ICONSET" -o "$RES_DIR/AppIcon.icns"

# NSHighResolutionCapable: without it macOS renders the Avalonia window at 1x and upscales it
# (blurry on Retina). NSPrincipalClass: Avalonia's own macOS packaging docs set it.
cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>
  <string>PolyTerm</string>
  <key>CFBundleDisplayName</key>
  <string>PolyTerm</string>
  <key>CFBundleIdentifier</key>
  <string>$BUNDLE_ID</string>
  <key>CFBundleExecutable</key>
  <string>PolyTerm</string>
  <key>CFBundleIconFile</key>
  <string>AppIcon</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>$VERSION</string>
  <key>CFBundleVersion</key>
  <string>$VERSION</string>
  <key>CFBundleInfoDictionaryVersion</key>
  <string>6.0</string>
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>NSPrincipalClass</key>
  <string>NSApplication</string>
</dict>
</plist>
EOF
plutil -lint "$APP/Contents/Info.plist"

# Last step that touches the bundle - anything changed after this breaks the signature, and macOS
# then reports the app as "damaged" instead of the overridable "can't be opened".
echo "== Ad-hoc signing PolyTerm.app =="
codesign --force --deep --sign - "$APP"
codesign --verify --deep --strict "$APP"
echo "Signature valid."

if [[ "$SMOKE_TEST" -eq 1 ]]; then
  HOST_ARCH="$(uname -m)"
  RID_ARCH=""
  case "$RID" in
    osx-x64) RID_ARCH="x86_64" ;;
    osx-arm64) RID_ARCH="arm64" ;;
  esac

  if [[ "$RID_ARCH" != "$HOST_ARCH" ]]; then
    echo "== Skipping launch-based smoke test: RID '$RID' ($RID_ARCH) doesn't match this machine's own architecture ($HOST_ARCH) =="
    echo "Confirmed $(file -b "$MACOS_DIR/PolyTerm")"
  elif [[ "$(launchctl managername 2>/dev/null)" == "Aqua" ]]; then
    # Launched through the bundle's own executable path (not `open`) so its PID can be checked and
    # stopped - still exercises the bundle layout, since the runtime resolves everything relative
    # to Contents/MacOS.
    echo "== Smoke-testing PolyTerm.app (GUI session available) =="
    "$MACOS_DIR/PolyTerm" &
    APP_PID=$!
    sleep 4
    if ! kill -0 "$APP_PID" 2>/dev/null; then
      echo "PolyTerm.app exited early - bundle is broken, not packaging this." >&2
      exit 1
    fi
    echo "PolyTerm.app is running (PID $APP_PID) - looks healthy. Stopping it."
    kill "$APP_PID"
    wait "$APP_PID" 2>/dev/null || true
  else
    echo "== Skipping launch-based smoke test: no GUI (Aqua) session available =="
  fi
fi

ln -s /Applications "$DMG_STAGE/Applications"
cat > "$DMG_STAGE/If macOS says it can't be opened.txt" <<'EOF'
PolyTerm is not signed with an Apple Developer ID yet, so the first time you open it macOS will
say it "can't be opened" (or "Apple could not verify" it). Either of these lets it run:

  1. Drag PolyTerm to Applications and try to open it once. Then go to
     System Settings > Privacy & Security, scroll down, and click "Open Anyway".

  2. Or, in Terminal:
       xattr -dr com.apple.quarantine /Applications/PolyTerm.app

You only need to do this once per installed version.

The command-line tools are inside the app:
  /Applications/PolyTerm.app/Contents/MacOS/cli/polyterm
  /Applications/PolyTerm.app/Contents/MacOS/mcp/polyterm-mcp
EOF

DMG_PATH="$OUT_DIR/PolyTerm-$VERSION-$RID.dmg"
echo "== Creating $DMG_PATH =="
# hdiutil warns that these flags are deprecated in favour of `diskutil image` on the newest macOS,
# but `diskutil image` doesn't exist on older releases (like the CI runner's) - hdiutil works on both.
hdiutil create -volname "PolyTerm $VERSION" -srcfolder "$DMG_STAGE" -fs HFS+ -format UDZO -ov "$DMG_PATH"
hdiutil verify -quiet "$DMG_PATH"

SIZE_MB=$(du -m "$DMG_PATH" | cut -f1)
echo "== Done: $DMG_PATH (${SIZE_MB} MB) =="
