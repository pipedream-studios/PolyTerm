#!/usr/bin/env bash
#
# Builds both native shims for macOS as universal (x86_64 + arm64) dylibs, at the paths the csproj
# Exists() checks look for:
#   native/PolyTerm-pty-shim/build/libpolyterm_pty_shim.dylib
#   native/PolyTerm-rdp-shim/build/libpolyterm_rdp_shim.dylib
# Shared by ci.yml's portable-macos job and release.yml's macos-portable-tarball job, and runnable
# locally the same way.
#
# No Homebrew: it no longer supports Intel Macs, which is what the self-hosted CI runner is. FreeRDP
# comes from vcpkg instead - the same native/PolyTerm-rdp-shim/vcpkg.json manifest (and pinned
# builtin-baseline) the Windows build uses. vcpkg's osx triplets link statically, so the shim needs
# nothing installed at runtime, and arm64-osx cross-compiles fine from an Intel host - so both
# architectures get working RDP, lipo'd into one dylib. CMake also comes from vcpkg (`vcpkg fetch
# cmake`), and pkg-config is built from source (see below). The only prerequisites are git, curl and
# the Xcode Command Line Tools.
#
# vcpkg is cloned to $VCPKG_ROOT (default ~/.cache/polyterm/vcpkg) and its binary cache lives in
# vcpkg's default ~/.cache/vcpkg/archives, so on a persistent self-hosted runner only the first run
# pays for building FreeRDP + OpenSSL from source (twice - once per architecture).
#
# Usage:
#   ./build-native-shims.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
PTY_DIR="$REPO_ROOT/native/PolyTerm-pty-shim"
RDP_DIR="$REPO_ROOT/native/PolyTerm-rdp-shim"
VCPKG_ROOT="${VCPKG_ROOT:-$HOME/.cache/polyterm/vcpkg}"

# Check out the manifest's own builtin-baseline rather than whatever vcpkg master is today: port
# versions already come from the baseline, but vcpkg's cmake helper scripts come from the working
# tree, so pinning both keeps this build reproducible.
BASELINE="$(sed -n 's/.*"builtin-baseline": *"\([0-9a-f]*\)".*/\1/p' "$RDP_DIR/vcpkg.json")"
if [[ -z "$BASELINE" ]]; then
  echo "Couldn't read builtin-baseline from $RDP_DIR/vcpkg.json." >&2
  exit 1
fi

echo "== Preparing vcpkg at $VCPKG_ROOT (baseline $BASELINE) =="
if [[ ! -d "$VCPKG_ROOT/.git" ]]; then
  mkdir -p "$(dirname "$VCPKG_ROOT")"
  git clone https://github.com/microsoft/vcpkg.git "$VCPKG_ROOT"
fi
if ! git -C "$VCPKG_ROOT" cat-file -e "$BASELINE^{commit}" 2>/dev/null; then
  git -C "$VCPKG_ROOT" fetch origin
fi
git -C "$VCPKG_ROOT" checkout --quiet --detach "$BASELINE"

# Re-bootstrap only when the checkout moved - each baseline pins its own vcpkg tool version.
STAMP="$VCPKG_ROOT/.polyterm-bootstrapped"
if [[ ! -x "$VCPKG_ROOT/vcpkg" || "$(cat "$STAMP" 2>/dev/null)" != "$BASELINE" ]]; then
  "$VCPKG_ROOT/bootstrap-vcpkg.sh" -disableMetrics
  echo "$BASELINE" > "$STAMP"
fi

CMAKE="$("$VCPKG_ROOT/vcpkg" fetch cmake | tail -n 1)"
echo "Using cmake: $CMAKE ($("$CMAKE" --version | head -n 1))"

# vcpkg's ports (zlib first) post-process their .pc files with pkg-config, which vcpkg only
# downloads for itself on Windows - on macOS it expects it already on PATH and suggests `brew
# install pkg-config`. It does honour $PKG_CONFIG first, so build pkgconf (a drop-in pkg-config)
# from a pinned source release instead: plain ./configure && make, nothing beyond the Command Line
# Tools, links only libSystem. Built once per version into ~/.cache/polyterm/tools.
PKGCONF_VERSION="2.5.1"
PKGCONF_SHA256="cd05c9589b9f86ecf044c10a2269822bc9eb001eced2582cfffd658b0a50c243"
PKGCONF_PREFIX="$HOME/.cache/polyterm/tools/pkgconf-$PKGCONF_VERSION"
if [[ ! -x "$PKGCONF_PREFIX/bin/pkgconf" ]]; then
  echo "== Building pkgconf $PKGCONF_VERSION into $PKGCONF_PREFIX =="
  PKGCONF_WORK="$(mktemp -d)"
  curl -fsSL "https://distfiles.ariadne.space/pkgconf/pkgconf-$PKGCONF_VERSION.tar.xz" -o "$PKGCONF_WORK/pkgconf.tar.xz"
  echo "$PKGCONF_SHA256  $PKGCONF_WORK/pkgconf.tar.xz" | shasum -a 256 -c -
  tar -xf "$PKGCONF_WORK/pkgconf.tar.xz" -C "$PKGCONF_WORK"
  (
    cd "$PKGCONF_WORK/pkgconf-$PKGCONF_VERSION"
    ./configure --prefix="$PKGCONF_PREFIX" --disable-shared --enable-static \
      --with-system-libdir=/usr/lib --with-system-includedir=/usr/include
    make -j"$(sysctl -n hw.ncpu)"
    make install
  )
  rm -rf "$PKGCONF_WORK"
fi
export PKG_CONFIG="$PKGCONF_PREFIX/bin/pkgconf"
echo "Using pkg-config: $PKG_CONFIG ($("$PKG_CONFIG" --version))"

# Stale shims from an earlier commit would otherwise get silently picked up by the csproj Exists()
# checks - self-hosted runner workspaces persist between runs.
rm -rf "$PTY_DIR/build" "$RDP_DIR/build" "$RDP_DIR"/build-*

echo "== Building PTY shim (universal) =="
"$CMAKE" -S "$PTY_DIR" -B "$PTY_DIR/build" -DCMAKE_BUILD_TYPE=Release "-DCMAKE_OSX_ARCHITECTURES=x86_64;arm64"
"$CMAKE" --build "$PTY_DIR/build"

# One vcpkg build per architecture (vcpkg has no universal triplet), then lipo them together.
for arch in x86_64 arm64; do
  case "$arch" in
    x86_64) triplet="x64-osx" ;;
    arm64) triplet="arm64-osx" ;;
  esac
  echo "== Building RDP shim ($arch, vcpkg triplet $triplet) =="
  "$CMAKE" -S "$RDP_DIR" -B "$RDP_DIR/build-$arch" -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_TOOLCHAIN_FILE="$VCPKG_ROOT/scripts/buildsystems/vcpkg.cmake" \
    -DVCPKG_TARGET_TRIPLET="$triplet" \
    -DCMAKE_OSX_ARCHITECTURES="$arch"
  "$CMAKE" --build "$RDP_DIR/build-$arch"
done

mkdir -p "$RDP_DIR/build"
lipo -create "$RDP_DIR/build-x86_64/libpolyterm_rdp_shim.dylib" "$RDP_DIR/build-arm64/libpolyterm_rdp_shim.dylib" \
  -output "$RDP_DIR/build/libpolyterm_rdp_shim.dylib"

# The tarballs ship these dylibs as-is, so anything they link beyond the OS itself (e.g. a
# dynamically-linked FreeRDP from some local install leaking in) would load on this machine and
# then fail on every user's.
echo "== Verifying shims =="
for lib in "$PTY_DIR/build/libpolyterm_pty_shim.dylib" "$RDP_DIR/build/libpolyterm_rdp_shim.dylib"; do
  echo "$(basename "$lib"): $(lipo -archs "$lib")"
  foreign="$(otool -L "$lib" | grep -v ':$' | awk '{print $1}' | grep -v -E '^(/usr/lib/|/System/Library/|@rpath/libpolyterm_)' || true)"
  if [[ -n "$foreign" ]]; then
    echo "$(basename "$lib") links non-system libraries, which won't exist on users' machines:" >&2
    echo "$foreign" >&2
    exit 1
  fi
done

echo "== Done =="
