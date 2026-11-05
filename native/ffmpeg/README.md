# Bundled FFmpeg libraries

`scripts/fetch-ffmpeg.sh <rid>` populates `native/ffmpeg/<rid>/` with the five LGPL
shared libraries the app links against (avutil, swresample, swscale, avcodec,
avformat) plus `FFMPEG-LICENSE.txt`. The csproj copies that directory next to the
executable for the active runtime identifier. The contents are not committed.

| RID | Source |
|---|---|
| linux-x64, linux-arm64, win-x64 | BtbN FFmpeg-Builds, pinned dated autobuild, LGPL shared |
| osx-arm64, osx-x64 | Built by `.github/workflows/ffmpeg-macos.yml` (no LGPL shared build exists off the shelf) |

The FFmpeg major version here must match the `FFmpeg.AutoGen` package version in
`DoorBird.App.csproj`. Both are on 8.1.
