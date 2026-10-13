#!/usr/bin/env bash
#
# Builds a self-contained, no-installer-needed PolyTerm distribution for macOS (App + CLI + MCP
# server) and tar.gz's it - the macOS counterpart to packaging/linux/build-portable-tarball.sh, with
# the same layout (App at the tarball root, CLI under cli/, MCP server under mcp/). Not a .app bundle
# or .dmg, and not signed/notarized: Gatekeeper quarantines anything downloaded through a browser, so
# users need `xattr -dr com.apple.quarantine <extracted dir>` before first launch.
#
# .NET has no universal (x86_64 + arm64) self-contained publish, so osx-x64 and osx-arm64 are two
# separate tarballs - run this once per RID.
#
# Native shims are NOT built by this script, matching the Linux/Windows scripts' division of
# responsibility - the csproj Exists() checks only pick them up if they're already built under
# native/*/build by the time `dotnet publish` runs. Run ./build-native-shims.sh first: it builds the
# PTY shim (terminal resize) and RDP shim (FreeRDP statically linked via vcpkg) as universal dylibs,
# so the same pair serves both RIDs and users don't need FreeRDP installed. A missing shim just makes
# that feature unavailable, it doesn't break the app.
#
# Usage:
#   ./build-portable-tarball.sh [--rid osx-x64|osx-arm64] [--smoke-test]

set -euo pipefail

RID="osx-arm64"
SMOKE_TEST=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --rid) RID="$2"; shift 2 ;;
    --smoke-test) SMOKE_TEST=1; shift ;;
    *) echo "Unknown argument: $1" >&2; exit 1 ;;
  esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
OUT_DIR="$SCRIPT_DIR/publish/$RID"
STAGE_DIR="$OUT_DIR/PolyTerm"
CLI_DIR="$STAGE_DIR/cli"
MCP_DIR="$STAGE_DIR/mcp"

echo "== Reading version from Directory.Build.props =="
VERSION=$(dotnet msbuild "$REPO_ROOT/src/PolyTerm.App/PolyTerm.App.csproj" -getProperty:Version -nologo)
echo "Version: $VERSION"

rm -rf "$OUT_DIR"
mkdir -p "$STAGE_DIR" "$CLI_DIR" "$MCP_DIR"

echo "== Publishing PolyTerm.App ($RID, self-contained) =="
dotnet publish "$REPO_ROOT/src/PolyTerm.App/PolyTerm.App.csproj" \
  -c Release -r "$RID" --self-contained true \
  -o "$STAGE_DIR"

echo "== Publishing PolyTerm.Cli ($RID, self-contained) into cli/ =="
dotnet publish "$REPO_ROOT/src/PolyTerm.Cli/PolyTerm.Cli.csproj" \
  -c Release -r "$RID" --self-contained true \
  -o "$CLI_DIR"

echo "== Publishing PolyTerm.Mcp ($RID, self-contained) into mcp/ =="
dotnet publish "$REPO_ROOT/src/PolyTerm.Mcp/PolyTerm.Mcp.csproj" \
  -c Release -r "$RID" --self-contained true \
  -o "$MCP_DIR"

chmod +x "$STAGE_DIR/PolyTerm" "$CLI_DIR/polyterm" "$MCP_DIR/polyterm-mcp"

# Report which native shims made it in, so a release log shows at a glance whether this tarball
# has working terminal resize / RDP rather than finding out from a user.
for shim in libpolyterm_pty_shim.dylib libpolyterm_rdp_shim.dylib; do
  if [[ -f "$STAGE_DIR/$shim" ]]; then
    echo "Included $shim: $(lipo -archs "$STAGE_DIR/$shim")"
  else
    echo "WARNING: $shim not included - build it under native/ first if this tarball should have it."
  fi
done

if [[ "$SMOKE_TEST" -eq 1 ]]; then
  # Only meaningful when RID matches this machine's own architecture - Rosetta runs x86_64 on Apple
  # Silicon, but nothing runs arm64 on an Intel Mac. Checked against `uname -m`, which reports the
  # real hardware unless this shell itself is running under Rosetta.
  HOST_ARCH="$(uname -m)"
  RID_ARCH=""
  case "$RID" in
    osx-x64) RID_ARCH="x86_64" ;;
    osx-arm64) RID_ARCH="arm64" ;;
  esac

  if [[ "$RID_ARCH" != "$HOST_ARCH" ]]; then
    echo "== Skipping launch-based smoke tests: RID '$RID' ($RID_ARCH) doesn't match this machine's own architecture ($HOST_ARCH) =="
    for f in "$STAGE_DIR/PolyTerm" "$CLI_DIR/polyterm" "$MCP_DIR/polyterm-mcp"; do
      if [[ ! -f "$f" ]]; then
        echo "$f not found - publish is broken." >&2
        exit 1
      fi
      echo "Confirmed $(file -b "$f")"
    done
  else
    echo "== Smoke-testing the packaged cli/polyterm =="
    CLI_VERSION="$("$CLI_DIR/polyterm" --version)"
    echo "polyterm --version: $CLI_VERSION"
    if [[ -z "$CLI_VERSION" ]]; then
      echo "polyterm --version produced no output." >&2
      exit 1
    fi
    if [[ "$CLI_VERSION" != *"$VERSION"* ]]; then
      echo "polyterm --version ('$CLI_VERSION') doesn't contain the expected version ('$VERSION')." >&2
      exit 1
    fi

    # No xvfb equivalent on macOS - Avalonia needs a real WindowServer session, which a runner only
    # has if it was started from a logged-in user's session (not as a LaunchDaemon). `launchctl
    # managername` reports "Aqua" in exactly that case.
    if [[ "$(launchctl managername 2>/dev/null)" == "Aqua" ]]; then
      echo "== Smoke-testing the packaged PolyTerm (GUI session available) =="
      "$STAGE_DIR/PolyTerm" &
      APP_PID=$!
      sleep 4
      if ! kill -0 "$APP_PID" 2>/dev/null; then
        echo "PolyTerm exited early - publish is broken, not packaging this." >&2
        exit 1
      fi
      echo "PolyTerm is running (PID $APP_PID) - looks healthy. Stopping it."
      kill "$APP_PID"
      wait "$APP_PID" 2>/dev/null || true
    else
      echo "== Skipping launch-based smoke test for PolyTerm.App: no GUI (Aqua) session available =="
      if [[ ! -f "$STAGE_DIR/PolyTerm" ]]; then
        echo "PolyTerm not found at $STAGE_DIR/PolyTerm - publish is broken." >&2
        exit 1
      fi
      echo "Confirmed PolyTerm exists; cannot confirm it actually runs without a GUI session."
    fi
  fi
fi

TARBALL_PATH="$OUT_DIR/PolyTerm-$VERSION-$RID.tar.gz"
rm -f "$TARBALL_PATH"
echo "== Creating $TARBALL_PATH =="
tar -czf "$TARBALL_PATH" -C "$STAGE_DIR" .

SIZE_MB=$(du -m "$TARBALL_PATH" | cut -f1)
echo "== Done: $TARBALL_PATH (${SIZE_MB} MB) =="
