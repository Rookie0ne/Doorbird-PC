#!/usr/bin/env bash
# Fetches the bundled FFmpeg shared libraries (trimmed LGPL build) for one or all
# runtime identifiers into native/ffmpeg/<rid>/. The app loads them from next to the
# executable via FFmpeg.AutoGen, so users never install ffmpeg themselves.
#
# The libraries are built by .github/workflows/ffmpeg.yml and downloaded from that
# run's artifacts, which needs the GitHub CLI (`gh auth login`).
#
#   scripts/fetch-ffmpeg.sh linux-x64        # one RID from the pinned run
#   scripts/fetch-ffmpeg.sh all              # every RID
#   FFMPEG_RUN_ID=<id> scripts/fetch-ffmpeg.sh all   # try a newer build
#
# RUN_ID pins the workflow run whose artifacts match FFmpeg.AutoGen in
# DoorBird.App.csproj (both on FFmpeg 8.1); upgrade both together. Artifacts expire
# 90 days after the run: if the download 404s, re-run the workflow with the same
# tag (same source, same result) and bump RUN_ID here.
set -euo pipefail

REPO="Rookie0ne/Doorbird-PC"
RUN_ID="${FFMPEG_RUN_ID:-42034835841}"
RIDS="linux-x64 linux-arm64 win-x64 osx-arm64 osx-x64"

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT_BASE="$ROOT/native/ffmpeg"

command -v gh >/dev/null || { echo "gh (GitHub CLI) is required: https://cli.github.com" >&2; exit 1; }

if [ "$RUN_ID" = "latest" ]; then
    RUN_ID="$(gh run list -R "$REPO" -w ffmpeg.yml -b main -s success -L1 --json databaseId -q '.[0].databaseId')"
    [ -n "$RUN_ID" ] || { echo "no successful ffmpeg workflow run found" >&2; exit 1; }
    echo "using latest successful ffmpeg workflow run $RUN_ID"
fi

fetch_one() {
    local rid="$1"
    local out="$OUT_BASE/$rid"
    local tmp
    tmp="$(mktemp -d)"
    echo "[$rid] downloading artifact ffmpeg-$rid from run $RUN_ID"
    gh run download "$RUN_ID" -R "$REPO" -n "ffmpeg-$rid" -D "$tmp"
    rm -rf "$out"
    mkdir -p "$out"
    cp "$tmp"/* "$out"/
    rm -rf "$tmp"
    echo "[$rid] $(ls "$out" | tr '\n' ' ')"
}

case "${1:-}" in
    all) for rid in $RIDS; do fetch_one "$rid"; done ;;
    "")  echo "usage: $0 <rid>|all   (rids: $RIDS)" >&2; exit 2 ;;
    *)   case " $RIDS " in *" $1 "*) fetch_one "$1" ;; *) echo "unknown rid '$1' (rids: $RIDS)" >&2; exit 2 ;; esac ;;
esac
