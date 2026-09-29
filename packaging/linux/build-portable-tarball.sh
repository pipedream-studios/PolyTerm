#!/usr/bin/env bash
#
# Builds a self-contained, no-installer-needed PolyTerm distribution for Linux (App + CLI) and
# tar.gz's it - the Linux counterpart to packaging/windows/build-portable-zip.ps1, and the first
# packaging deliverable for issue #72 ("No Linux/macOS packaging/publish scripts"). .deb/.rpm/AppImage
# are heavier lifts, deliberately scoped separately per that issue's own suggested phasing - this is
# just the cheapest first step, parity with what Windows already has.
#
# Unlike the Windows script, App and CLI don't need separate subfolders to avoid a filename collision
# - PolyTerm.App's AssemblyName is "PolyTerm", PolyTerm.Cli's is "polyterm", and Linux filesystems are
# case-sensitive (unlike Windows' default NTFS, where PolyTerm.exe and polyterm.exe are literally the
# same file). Kept separate anyway (App at the tarball root, CLI under cli/) purely for a consistent,
# predictable layout that matches the Windows zip's own structure - same reason, not the same bug.
#
# The native RDP shim (native/PolyTerm-rdp-shim) is NOT built by this script, matching
# build-portable-zip.ps1's own division of responsibility: PolyTerm.Protocols.RdpFreerdp.csproj's own
# MSBuild content-copy only picks up the shim if it's already built at native/PolyTerm-rdp-shim/build
# by the time `dotnet publish` runs (see that .csproj's own comment) - build it first (cmake, or the
# portable-ubuntu CI job's own steps) if you want RDP to actually work in the packaged output, same as
# release.yml's Windows jobs do for their own platform.
#
# Usage:
#   ./build-portable-tarball.sh [--rid linux-x64|linux-arm64] [--smoke-test]

set -euo pipefail

RID="linux-x64"
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
dotnet publish "$REPO_ROOT/src/PolyTerm.Mcp/PolyTerm.Mcp.csproj"   -c Release -r "$RID" --self-contained true   -o "$MCP_DIR"

chmod +x "$STAGE_DIR/PolyTerm" "$CLI_DIR/polyterm" "$MCP_DIR/polyterm-mcp"

if [[ "$SMOKE_TEST" -eq 1 ]]; then
  # Only meaningful when RID matches this machine's own architecture - same "can't run arm64 on x64
  # hardware" constraint the Windows script's own SmokeTest documents, just checked via `uname -m`
  # instead of .NET's RuntimeInformation since this runs before dotnet has anything to introspect with.
  HOST_ARCH="$(uname -m)"
  RID_ARCH=""
  case "$RID" in
    linux-x64) RID_ARCH="x86_64" ;;
    linux-arm64) RID_ARCH="aarch64" ;;
  esac

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

  if [[ "$RID_ARCH" != "$HOST_ARCH" ]]; then
    echo "== Skipping launch-based smoke test for PolyTerm.App: RID '$RID' ($RID_ARCH) doesn't match this machine's own architecture ($HOST_ARCH) =="
    if [[ ! -f "$STAGE_DIR/PolyTerm" ]]; then
      echo "PolyTerm not found at $STAGE_DIR/PolyTerm - publish is broken." >&2
      exit 1
    fi
    echo "Confirmed PolyTerm exists; cannot confirm it actually runs without matching hardware."
  elif command -v xvfb-run >/dev/null 2>&1; then
    # PolyTerm.App is a real GUI app (Avalonia) - needs an actual display to initialize, which a bare
    # CI runner doesn't have. xvfb-run provides a throwaway virtual framebuffer so this can be a real
    # launch test instead of just "the file exists" - matches this project's own "prove it actually
    # runs, don't just assume" testing discipline (see docs/testing.md if present).
    echo "== Smoke-testing the packaged PolyTerm under xvfb-run =="
    xvfb-run --auto-servernum "$STAGE_DIR/PolyTerm" &
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
    echo "== Skipping launch-based smoke test for PolyTerm.App: xvfb-run not available (install the xvfb package for a real launch test) =="
    if [[ ! -f "$STAGE_DIR/PolyTerm" ]]; then
      echo "PolyTerm not found at $STAGE_DIR/PolyTerm - publish is broken." >&2
      exit 1
    fi
    echo "Confirmed PolyTerm exists; cannot confirm it actually runs without a display."
  fi
fi

TARBALL_PATH="$OUT_DIR/PolyTerm-$VERSION-$RID.tar.gz"
rm -f "$TARBALL_PATH"
echo "== Creating $TARBALL_PATH =="
tar -czf "$TARBALL_PATH" -C "$STAGE_DIR" .

SIZE_MB=$(du -m "$TARBALL_PATH" | cut -f1)
echo "== Done: $TARBALL_PATH (${SIZE_MB} MB) =="
