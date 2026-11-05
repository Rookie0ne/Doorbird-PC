#!/usr/bin/env bash
# Fetches the bundled FFmpeg shared libraries (LGPL build) for one or all runtime
# identifiers into native/ffmpeg/<rid>/. The app loads them from next to the
# executable via FFmpeg.AutoGen, so users never install ffmpeg themselves.
#
#   scripts/fetch-ffmpeg.sh linux-x64        # one RID
#   scripts/fetch-ffmpeg.sh all              # every RID with a pinned archive
#
# Pinned to one dated BtbN autobuild so the download is reproducible and the
# sha256 below stays valid. FFmpeg.AutoGen in DoorBird.App.csproj is pinned to the
# same FFmpeg major (8.1); upgrade both together.
#
# macOS has no LGPL shared build to download; see .github/workflows/ffmpeg-macos.yml.
set -euo pipefail

TAG="autobuild-2026-11-04-12-51"
BUILD="ffmpeg-n8.1.3-34-ga35157abb9"
BASE="https://github.com/BtbN/FFmpeg-Builds/releases/download/$TAG"

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT_BASE="$ROOT/native/ffmpeg"
CACHE="${FFMPEG_FETCH_CACHE:-$OUT_BASE/.cache}"
mkdir -p "$CACHE"

# rid  archive-name  sha256
PINS="
linux-x64    $BUILD-linux64-lgpl-shared-8.1.tar.xz     f3684065f1e160c6659e23de714c13a3a9998504d128848f3e2390d9f1957076
linux-arm64  $BUILD-linuxarm64-lgpl-shared-8.1.tar.xz  66fb06d52696aee65af4a6585ae24bf4c787f95ba9d8a6cb79d5b6b19e57d6d1
win-x64      $BUILD-win64-lgpl-shared-8.1.zip          1b32a03bcc71e7db008484060dd1f5734369b14e4f5fea88368e96be35b7288d
"

# Only the libraries the app calls. avfilter/avdevice are left out on purpose.
LIBS="avutil:60 swresample:6 swscale:9 avcodec:62 avformat:62"

fetch_one() {
    local rid="$1" archive="$2" sha="$3"
    local out="$OUT_BASE/$rid"
    local file="$CACHE/$archive"

    if [ ! -f "$file" ]; then
        echo "[$rid] downloading $archive"
        curl -fL --retry 3 -o "$file.part" "$BASE/$archive"
        mv "$file.part" "$file"
    fi
    echo "$sha  $file" | sha256sum -c - >/dev/null || { echo "[$rid] checksum mismatch for $archive" >&2; exit 1; }

    local tmp
    tmp="$(mktemp -d)"
    trap 'rm -rf "$tmp"' RETURN
    mkdir -p "$out"
    rm -f "$out"/*.so.* "$out"/*.dll "$out"/FFMPEG-LICENSE.txt

    case "$archive" in
        *.tar.xz)
            tar -xJf "$file" -C "$tmp"
            local lib
            for lib in $LIBS; do
                local name="${lib%%:*}" major="${lib##*:}"
                # The archive holds the real file as lib<name>.so.<major>.<minor>.<micro>
                # plus symlinks; copy the real file under the soname the loader asks for.
                local real
                real="$(find "$tmp" -type f -name "lib$name.so.$major.*" | head -1)"
                [ -n "$real" ] || { echo "[$rid] lib$name.so.$major missing from archive" >&2; exit 1; }
                cp "$real" "$out/lib$name.so.$major"
            done
            ;;
        *.zip)
            unzip -q "$file" -d "$tmp"
            local lib
            for lib in $LIBS; do
                local name="${lib%%:*}" major="${lib##*:}"
                local dll
                dll="$(find "$tmp" -type f -name "$name-$major.dll" | head -1)"
                [ -n "$dll" ] || { echo "[$rid] $name-$major.dll missing from archive" >&2; exit 1; }
                cp "$dll" "$out/"
            done
            ;;
    esac
    cp "$(find "$tmp" -maxdepth 2 -name LICENSE.txt | head -1)" "$out/FFMPEG-LICENSE.txt"
    echo "[$rid] ready in $out:"
    ls -1 "$out"
}

want="${1:-}"
[ -n "$want" ] || { echo "usage: $0 <rid|all>" >&2; exit 2; }

found=0
while read -r rid archive sha; do
    [ -n "$rid" ] || continue
    if [ "$want" = "all" ] || [ "$want" = "$rid" ]; then
        fetch_one "$rid" "$archive" "$sha"
        found=1
    fi
done <<< "$PINS"

if [ "$found" = 0 ]; then
    echo "no pinned archive for '$want' (available: linux-x64 linux-arm64 win-x64; macOS via CI workflow)" >&2
    exit 1
fi
