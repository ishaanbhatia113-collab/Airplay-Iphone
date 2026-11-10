#!/usr/bin/env bash
#
# Builds the AirPlay engine (UxPlay) and collects it, together with GStreamer and every DLL it needs,
# into engine/dist. The app picks that folder up automatically (see src/MirrorDeck/MirrorDeck.csproj).
#
# Run it inside an MSYS2 shell that targets native Windows:
#     UCRT64      for 64-bit Intel/AMD PCs            (Start menu > MSYS2 > "MSYS2 UCRT64")
#     CLANGARM64  for Windows on ARM (Snapdragon PCs)
#
#     ./engine/build-engine.sh                 mirroring + audio (about 250 MB)
#     ./engine/build-engine.sh --all-plugins   every GStreamer plugin; needed for YouTube casting (HLS)
#
# The engine is built from unmodified upstream sources at a pinned commit. That keeps GPL compliance
# simple: the "corresponding source" is exactly that commit, and it is recorded in dist/ENGINE-SOURCE.txt.
#
set -euo pipefail

UXPLAY_REPO="${UXPLAY_REPO:-https://github.com/FDH2/UxPlay.git}"
# UxPlay 1.74 (2026-11-06). 1.74 is the first version with a built-in mDNS responder, which is what
# removes the dependency on Apple's Bonjour service.
UXPLAY_COMMIT="${UXPLAY_COMMIT:-05b0275cb1b726592700e47a6e0307544b3d3a52}"

ALL_PLUGINS=0
WITH_BONJOUR=0
INSTALL_PACKAGES=1
CLEAN=0

usage() {
    sed -n '2,15p' "$0" | sed 's/^# \{0,1\}//'
    cat <<'EOF'

Options:
  --all-plugins    bundle every installed GStreamer plugin plus the TLS/HTTP pieces HLS needs
  --with-bonjour   use Apple's Bonjour for discovery instead of UxPlay's own responder. Needs the
                   Bonjour SDK to build (BONJOUR_SDK_HOME) and the "Bonjour Service" on every PC
                   that runs the app. Only useful if the built-in responder misbehaves on a network.
  --skip-install   do not run pacman (packages are already installed)
  --clean          delete engine/_work and engine/dist first
  -h, --help       this text

Environment:
  UXPLAY_COMMIT    build a different UxPlay commit or tag (default: the pinned 1.74 commit)
  UXPLAY_REPO      clone from a different repository (a fork, a local mirror)
EOF
}

for arg in "$@"; do
    case "$arg" in
        --all-plugins)  ALL_PLUGINS=1 ;;
        --with-bonjour) WITH_BONJOUR=1 ;;
        --skip-install) INSTALL_PACKAGES=0 ;;
        --clean)        CLEAN=1 ;;
        -h|--help)      usage; exit 0 ;;
        *) echo "unknown option: $arg" >&2; usage >&2; exit 2 ;;
    esac
done

say()  { printf '\n\033[1m==> %s\033[0m\n' "$*"; }
warn() { printf '\033[33mwarning:\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[31merror:\033[0m %s\n' "$*" >&2; exit 1; }

# ---------------------------------------------------------------------------------------------------
# 0. the right kind of shell
# ---------------------------------------------------------------------------------------------------

if [[ -z "${MSYSTEM:-}" || -z "${MINGW_PREFIX:-}" || -z "${MINGW_PACKAGE_PREFIX:-}" || "${MSYSTEM}" == "MSYS" ]]; then
    die "run this from an MSYS2 UCRT64 (or CLANGARM64) shell, not from '${MSYSTEM:-a plain shell}'.
       Install MSYS2 from https://www.msys2.org, then open 'MSYS2 UCRT64' from the Start menu.
       From PowerShell you can use scripts/build-engine.ps1 instead."
fi

P="$MINGW_PACKAGE_PREFIX"            # e.g. mingw-w64-ucrt-x86_64
PREFIX="$MINGW_PREFIX"               # e.g. /ucrt64
ENGINE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORK="$ENGINE_DIR/_work"
SRC="$WORK/UxPlay"
DIST="$ENGINE_DIR/dist"

if (( CLEAN )); then
    say "Cleaning"
    rm -rf "$WORK" "$DIST"
fi

# ---------------------------------------------------------------------------------------------------
# 1. build tools and libraries
# ---------------------------------------------------------------------------------------------------

case "$MSYSTEM" in
    CLANG*) COMPILER="$P-clang" ;;
    *)      COMPILER="$P-gcc" ;;
esac

PACKAGES=(
    git
    "$COMPILER" "$P-cmake" "$P-ninja" "$P-pkgconf" "$P-ntldd"
    "$P-openssl" "$P-libplist"
    "$P-gstreamer" "$P-gst-plugins-base" "$P-gst-plugins-good" "$P-gst-plugins-bad" "$P-gst-libav"
)
if (( ALL_PLUGINS )); then
    # HLS fetches video segments over HTTPS: libsoup does the HTTP, glib-networking the TLS
    PACKAGES+=("$P-libsoup3" "$P-glib-networking")
fi

if (( INSTALL_PACKAGES )); then
    say "Installing MSYS2 packages (pacman may ask to update itself first; if it does, run the script again)"
    pacman -S --needed --noconfirm "${PACKAGES[@]}"
fi

for tool in git cmake ninja ntldd pkgconf; do
    command -v "$tool" >/dev/null || die "'$tool' is not installed; run without --skip-install"
done

# ---------------------------------------------------------------------------------------------------
# 2. sources at the pinned commit
# ---------------------------------------------------------------------------------------------------

say "Fetching UxPlay $UXPLAY_COMMIT"
mkdir -p "$SRC"
if [[ ! -d "$SRC/.git" ]]; then
    git -C "$SRC" init -q
    git -C "$SRC" remote add origin "$UXPLAY_REPO"
fi
git -C "$SRC" remote set-url origin "$UXPLAY_REPO"
git -C "$SRC" fetch -q --depth 1 origin "$UXPLAY_COMMIT"
git -C "$SRC" checkout -q --force FETCH_HEAD
git -C "$SRC" clean -q -fdx -e build
RESOLVED_COMMIT="$(git -C "$SRC" rev-parse HEAD)"
UXPLAY_VERSION="$(sed -n 's/^#define VERSION "\(.*\)"/\1/p' "$SRC/uxplay.cpp" | head -n1)"
echo "UxPlay ${UXPLAY_VERSION:-?} at $RESOLVED_COMMIT"

# ---------------------------------------------------------------------------------------------------
# 3. compile
# ---------------------------------------------------------------------------------------------------

say "Building"
CMAKE_ARGS=(
    -G Ninja
    -DCMAKE_BUILD_TYPE=Release
    # Without this UxPlay compiles with -march=native, and the result crashes with an
    # "illegal instruction" on any PC whose processor is older than the build machine's.
    -DNO_MARCH_NATIVE=ON
)
if (( WITH_BONJOUR )); then
    SDK="${BONJOUR_SDK_HOME:-C:\\Program Files\\Bonjour SDK}"
    [[ -f "$(cygpath -u "$SDK")/Include/dns_sd.h" ]] || die "Bonjour SDK not found at '$SDK' (set BONJOUR_SDK_HOME)"
    CMAKE_ARGS+=(-DUSE_DNS_SD=1)
fi
cmake -S "$SRC" -B "$SRC/build" "${CMAKE_ARGS[@]}"
cmake --build "$SRC/build"
[[ -f "$SRC/build/uxplay.exe" ]] || die "the build did not produce uxplay.exe"

# ---------------------------------------------------------------------------------------------------
# 4. bundle
#
# The layout mirrors an MSYS2 prefix on purpose. GStreamer locates its plugins and its plugin scanner
# relative to libgstreamer-1.0-0.dll ("..\lib\gstreamer-1.0", "..\libexec\gstreamer-1.0"), and GLib does
# the same for GIO modules, so a bundle shaped like this works from any folder without configuration.
#
#   dist/bin/                      uxplay.exe, gst-inspect-1.0.exe and all DLLs
#   dist/lib/gstreamer-1.0/        plugins
#   dist/libexec/gstreamer-1.0/    gst-plugin-scanner.exe
#   dist/licenses/                 license texts and the list of bundled packages
# ---------------------------------------------------------------------------------------------------

say "Bundling into $DIST"
rm -rf "$DIST"
mkdir -p "$DIST/bin" "$DIST/lib/gstreamer-1.0" "$DIST/libexec/gstreamer-1.0" "$DIST/licenses"

PLUGIN_SRC="$PREFIX/lib/gstreamer-1.0"

# Without these the engine cannot mirror at all.
REQUIRED_PLUGINS=(
    coreelements        # queue, capsfilter, fakesink
    app                 # appsrc: where decrypted video and audio enter GStreamer
    playback            # decodebin: picks the best available H.264/H.265 decoder
    typefindfunctions
    videoparsersbad     # h264parse, h265parse
    libav               # FFmpeg: software H.264/H.265, and the AAC-ELD / ALAC audio decoders
    videoconvertscale   # videoconvert, videoscale
    audioconvert audioresample volume
    autodetect          # autoaudiosink, autovideosink
    level
    d3d11               # Direct3D 11 video sink and hardware decoders (the default renderer)
)
# Used when present.
OPTIONAL_PLUGINS=(
    d3d12               # Direct3D 12 renderer and decoders
    wasapi wasapi2 directsound   # audio output; at least one must exist (checked below)
    videofilter         # videoflip, for UxPlay's rotate/flip options
    audioparsers
    isomp4              # "mp4" recording option
    jpeg                # cover art for audio-only streams
    rtp udp             # "vrtp"/"artp": forward the streams to OBS and the like
)

copy_plugin() {     # $1 = plugin name, $2 = required|optional
    local file="$PLUGIN_SRC/libgst$1.dll"
    if [[ -f "$file" ]]; then
        cp "$file" "$DIST/lib/gstreamer-1.0/"
    elif [[ "$2" == required ]]; then
        die "required GStreamer plugin '$1' is missing ($file)"
    else
        warn "optional GStreamer plugin '$1' is not installed; skipping"
    fi
}

cp "$SRC/build/uxplay.exe" "$DIST/bin/"
# small, and invaluable for support: "engine\bin\gst-inspect-1.0.exe d3d11videosink"
cp "$PREFIX/bin/gst-inspect-1.0.exe" "$DIST/bin/"
if [[ -f "$PREFIX/libexec/gstreamer-1.0/gst-plugin-scanner.exe" ]]; then
    cp "$PREFIX/libexec/gstreamer-1.0/gst-plugin-scanner.exe" "$DIST/libexec/gstreamer-1.0/"
fi

if (( ALL_PLUGINS )); then
    cp "$PLUGIN_SRC"/*.dll "$DIST/lib/gstreamer-1.0/"
    # dlopen()ed by the soup plugin at run time, so the dependency scan below cannot see it
    for dll in "$PREFIX"/bin/libsoup-3.0-*.dll; do
        if [[ -f "$dll" ]]; then cp "$dll" "$DIST/bin/"; fi
    done
    if compgen -G "$PREFIX/lib/gio/modules/*.dll" >/dev/null; then
        mkdir -p "$DIST/lib/gio/modules"
        cp "$PREFIX"/lib/gio/modules/*.dll "$DIST/lib/gio/modules/"
    fi
else
    for plugin in "${REQUIRED_PLUGINS[@]}"; do copy_plugin "$plugin" required; done
    for plugin in "${OPTIONAL_PLUGINS[@]}"; do copy_plugin "$plugin" optional; done
fi

compgen -G "$DIST/lib/gstreamer-1.0/libgstwasapi*.dll" >/dev/null \
    || [[ -f "$DIST/lib/gstreamer-1.0/libgstdirectsound.dll" ]] \
    || die "no audio output plugin (wasapi, wasapi2, directsound) is installed"

# ---- every DLL that anything above links against --------------------------------------------------

say "Collecting DLL dependencies (this takes a minute)"

prefix_lower="${PREFIX,,}"

# Prints the DLLs below $PREFIX that "$1" needs, directly or indirectly. Windows' own DLLs resolve to
# C:\Windows and are left out; "not found" lines (optional API sets) are ignored.
deps_of() {
    { ntldd -R "$1" 2>/dev/null || true; } | awk '$2 == "=>" && $3 != "not" { print $3 }' | while IFS= read -r win; do
        local unix
        unix="$(cygpath -u "$win" 2>/dev/null)" || continue
        case "${unix,,}" in
            "$prefix_lower"/*) printf '%s\n' "$unix" ;;
        esac
    done
    return 0
}

# The first pass scans everything that is in the bundle so far. ntldd -R follows dependencies of
# dependencies, so that normally finds everything; the loop then re-scans whatever was added and only
# stops when a pass adds nothing, which guarantees that the set is closed.
mapfile -t TODO < <(find "$DIST" -type f \( -name '*.exe' -o -name '*.dll' \) | sort)
while (( ${#TODO[@]} > 0 )); do
    NEXT=()
    while IFS= read -r dll; do
        target="$DIST/bin/$(basename "$dll")"
        if [[ ! -e "$target" ]]; then
            cp "$dll" "$target"
            NEXT+=("$target")
        fi
    done < <(for file in "${TODO[@]}"; do deps_of "$file"; done | sort -u)
    TODO=("${NEXT[@]}")
done

# ---- licenses and a bill of materials --------------------------------------------------------------

say "Writing license files"
cp "$SRC/LICENSE" "$DIST/licenses/UxPlay-GPL-3.0.txt"
cp "$SRC/lib/llhttp/LICENSE-MIT" "$DIST/licenses/llhttp-MIT.txt"
cp "$SRC/lib/playfair/LICENSE.md" "$DIST/licenses/playfair-GPL-3.0.md"

{
    echo "MSYS2 packages whose files are part of this bundle"
    echo "environment: $MSYSTEM    built: $(date -u +%Y-%m-%dT%H:%MZ)"
    echo "Sources and build recipes for every package: https://packages.msys2.org/"
    echo
    printf '%-58s %-22s %s\n' PACKAGE VERSION LICENSE
} > "$DIST/licenses/PACKAGES.txt"

# Map every bundled file back to the MSYS2 package it came from.
{
    find "$DIST/bin" -maxdepth 1 -name '*.dll' -printf "$PREFIX/bin/%f\n"
    find "$DIST/lib/gstreamer-1.0" -name '*.dll' -printf "$PLUGIN_SRC/%f\n"
    if [[ -d "$DIST/lib/gio/modules" ]]; then
        find "$DIST/lib/gio/modules" -name '*.dll' -printf "$PREFIX/lib/gio/modules/%f\n"
    fi
} | while IFS= read -r original; do
    if [[ -f "$original" ]]; then pacman -Qqo "$original" 2>/dev/null || true; fi
done | sort -u > "$WORK/packages.txt"

while IFS= read -r package; do
    [[ -n "$package" ]] || continue
    version="$(pacman -Q "$package" | awk '{ print $2 }')"
    license="$(LC_ALL=C pacman -Qi "$package" | sed -n 's/^Licenses *: *//p')"
    printf '%-58s %-22s %s\n' "$package" "$version" "$license" >> "$DIST/licenses/PACKAGES.txt"
    short="${package#"$P"-}"
    if [[ -d "$PREFIX/share/licenses/$short" ]]; then
        mkdir -p "$DIST/licenses/$short"
        cp -r "$PREFIX/share/licenses/$short/." "$DIST/licenses/$short/"
    fi
done < "$WORK/packages.txt"

cat > "$DIST/ENGINE-SOURCE.txt" <<EOF
AirPlay engine: UxPlay ${UXPLAY_VERSION:-} (GNU General Public License, version 3)
Built from unmodified sources.

  repository  $UXPLAY_REPO
  commit      $RESOLVED_COMMIT
  options     ${CMAKE_ARGS[*]}
  discovery   $( (( WITH_BONJOUR )) && echo "Apple Bonjour (dns_sd); the Bonjour Service must be installed" || echo "built-in mDNS responder (lib/mdnsd); no Bonjour needed")
  plugins     $( (( ALL_PLUGINS )) && echo "all installed GStreamer plugins" || echo "mirroring + audio set")

To rebuild this exact engine:
  UXPLAY_COMMIT=$RESOLVED_COMMIT ./engine/build-engine.sh$( (( ALL_PLUGINS )) && echo " --all-plugins")$( (( WITH_BONJOUR )) && echo " --with-bonjour")

The other files in this folder come from MSYS2 packages; licenses/PACKAGES.txt lists them with their
versions and licenses.
EOF

# ---------------------------------------------------------------------------------------------------
# 5. does the bundle stand on its own?
#
# The checks run the way the app runs the engine: PATH is the bundle's bin folder plus the Windows
# system folder, nothing from MSYS2. If a DLL was missed, they fail here on the build machine instead
# of on a user's PC.
# ---------------------------------------------------------------------------------------------------

say "Verifying the bundle"
SYSTEM32="$(cygpath -u "${SYSTEMROOT:-C:\\Windows}")/System32"
REGISTRY="$WORK/verify-registry.bin"
rm -f "$REGISTRY"

isolated() {
    env PATH="$DIST/bin:$SYSTEM32" \
        GST_PLUGIN_SYSTEM_PATH_1_0="$(cygpath -w "$DIST/lib/gstreamer-1.0")" \
        GST_PLUGIN_PATH_1_0= GST_PLUGIN_PATH= \
        GST_REGISTRY_1_0="$(cygpath -w "$REGISTRY")" \
        "$@"
}

isolated "$DIST/bin/uxplay.exe" -v >/dev/null 2>&1 \
    || die "uxplay.exe does not start outside MSYS2 (a DLL is missing from $DIST/bin). Check with: ntldd -R '$DIST/bin/uxplay.exe'"


missing=0
for element in appsrc queue h264parse decodebin avdec_h264 avdec_aac avdec_alac \
               videoconvert videoscale audioconvert audioresample volume level \
               autoaudiosink d3d11videosink; do
    if ! isolated "$DIST/bin/gst-inspect-1.0.exe" --exists "$element" >/dev/null 2>&1; then
        warn "GStreamer element '$element' does not load from the bundle"
        missing=1
    fi
done
(( missing == 0 )) || die "the bundle is incomplete (see the warnings above)"

if ! isolated "$DIST/bin/gst-inspect-1.0.exe" --exists d3d11h264dec >/dev/null 2>&1; then
    warn "no Direct3D 11 hardware H.264 decoder on this machine; mirroring will use the (slower) software decoder here"
fi

FILES="$(find "$DIST" -type f | wc -l)"
SIZE="$(du -sh "$DIST" | cut -f1)"
say "Done: $FILES files, $SIZE in $DIST"
cat <<EOF
Next:
  dotnet run --project src/MirrorDeck          run the app with this engine
  pwsh scripts/publish.ps1                     self-contained build in publish/
  pwsh packaging/make-msix.ps1                 MSIX package for sideloading or the Microsoft Store
EOF
