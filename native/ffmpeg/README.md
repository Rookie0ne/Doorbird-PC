# Bundled FFmpeg libraries

`scripts/fetch-ffmpeg.sh <rid>` populates `native/ffmpeg/<rid>/` with the five LGPL
shared libraries the app links against (avutil, swresample, swscale, avcodec,
avformat) plus `FFMPEG-LICENSE.txt`. The csproj copies that directory next to the
executable for the active runtime identifier. The contents are not committed.

All five sets come from `.github/workflows/ffmpeg.yml`, a trimmed build of a pinned
FFmpeg tag (run it manually from the Actions tab; the fetch script downloads the
artifacts with `gh`). Only what the app uses is compiled in, which keeps each set
around 5 MB instead of the ~120 MB of a full-codec build:

| | Compiled in |
|---|---|
| Decoders | h264, pcm_mulaw, aac |
| Encoder | aac |
| Demuxers / muxers | rtsp, sdp, rtp, h264, pcm_mulaw, mov / mp4, mov |
| Protocols | tcp, udp, rtp, http, file |
| Hardware decode | linux: VA-API (`h264_vaapi`); win-x64: D3D11VA; macOS: VideoToolbox |

Platform notes:

- **linux-x64 / linux-arm64** are built in a Debian 11 container (glibc 2.31 floor)
  and call libva/libdrm through [Implib.so](https://github.com/yugr/Implib.so)
  dlopen shims, so they have no hard dependency on a VA-API stack. `RtspVideoDecoder`
  checks `libva.so.2`, `libva-drm.so.2` and `libdrm.so.2` exist before creating a VAAPI
  device, because a shim whose library is missing aborts the process.
- **win-x64** is cross-compiled with mingw-w64 (win32 threads, static libgcc); the
  DLLs import only Windows system libraries.
- **osx-arm64 / osx-x64** are built with `MACOSX_DEPLOYMENT_TARGET=11.0` and
  `@loader_path` install names.

The FFmpeg major version here must match the `FFmpeg.AutoGen` package version in
`DoorBird.App.csproj`. Both are on 8.1; the workflow's `ffmpeg_tag` input and the
`RUN_ID` pin in the fetch script are where to bump.
