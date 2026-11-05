# Hardware acceleration for the RTSP pipeline — measured, then declined

> **Update (v0.5.0, November 2026):** the pipeline this document measured no longer
> exists. RTSP is now decoded in-process with the bundled FFmpeg libraries
> (`src/DoorBird.App/Video/`), and on Linux the frames go VAAPI → DMA-BUF → EGL →
> Avalonia's compositor without touching system memory. The findings below are the
> reason that design looks the way it does: finding 1 is why Auto mode only accepts
> the full zero-copy chain, finding 3 is why it verifies by producing frames, and
> finding 4 is why NVIDIA on Linux stays in software. Measured on the same machine
> and stream, total CPU across all app threads: old pipeline 15.6% of one core,
> new software path ~6.6%, new zero-copy path ~4.4%. The numbers below describe the
> retired transcode pipeline and are kept for that history.


Investigated 2026-08-27 on a Razer Blade 15 (2018 chassis, Intel Alder Lake-P
Iris Xe + NVIDIA RTX 3070 Ti Laptop, Arch Linux, ffmpeg 8.x, Mesa 26.2.1,
NVIDIA 610.57.04). **Deliberately not implemented** — the reasoning is at the
bottom. This file exists so nobody repeats the investigation.

Source stream for every measurement: DoorBird RTSP, H.264 Main L4.0,
1280x720, 30 fps, no audio track.

## Why RTSP mode costs what it costs

`LiveViewModel.RtspLoop` runs ffmpeg with
`-rtsp_transport tcp -i <rtsp> -f mjpeg -q:v 3 -an pipe:1`, i.e. it **transcodes
H.264 to MJPEG**. That is not an oversight: the app has one frame reader,
`ReadMjpegFrames`, shared by RTSP mode and the device's own MJPEG mode. Feeding
it from RTSP means producing JPEGs.

So RTSP mode performs **three codec operations per frame**:

| # | Operation | Where | Cost (100 frames) |
|---|---|---|---|
| 1 | Decode H.264 720p | ffmpeg | 0.33 s CPU |
| 2 | Re-encode to MJPEG | ffmpeg | 0.30 s CPU |
| 3 | Decode the JPEG to a Bitmap | this app | — (see below) |

The **encode is the expensive half of ffmpeg's work**, not the decode. And
ffmpeg is the *smaller* consumer of the two processes: measured instantaneously,
`DoorBird.App` sat at 11.3% of one core against ffmpeg's 4.3%. Operation 3
dominates.

## The full matrix

100 frames per run, frame counts verified every time, MJPEG encode in software
unless the row says "full GPU".

| Pipeline | CPU | vs software | Result |
|---|---|---|---|
| Software (current) | 0.655 s | — | works |
| **Intel QSV, full GPU** (`mjpeg_qsv`) | **0.211 s** | **−68%** | **works, best** |
| Intel VA-API, decode only | 0.780 s | +19% | works, slower |
| Intel VA-API, full GPU (`mjpeg_vaapi`) | — | — | **fails: `-22 Invalid argument`** |
| Intel QSV, decode only | — | — | **fails: format conversion** |
| NVIDIA `cuda` decode | 1.058 s | +62% | works, much slower |
| NVIDIA `cuvid` decode | 1.026 s | +57% | works, much slower |
| NVIDIA MJPEG encode | — | — | **does not exist in ffmpeg** |
| Raw BGRA out (no MJPEG encode) | 0.599 s | −9% | works; see note |

Winning invocation, for the record:

    ffmpeg -hwaccel qsv -hwaccel_output_format qsv \
      -rtsp_transport tcp -i <rtsp> \
      -c:v mjpeg_qsv -global_quality 80 -f mjpeg -an pipe:1

Verified genuinely on-GPU: `libvpl.so.2` mapped, ffmpeg listed as an i915
client, Intel **Video** engine at ~4.8% during a sustained run, NVIDIA decoder
at 0%. Output is correct 1280x720 `yuvj420p` JPEG, visually clean, within ~3.5%
of the software encoder's file size.

## Four findings worth keeping

**1. Only full-GPU pipelines win. Every decode-only variant is slower than
software.** The GPU→CPU frame download costs more than software decode of a
720p stream saves — `sys` time roughly doubles. If frames do not stay on the GPU
from decode through encode, do not bother.

**2. QSV and VA-API are different APIs onto the same Intel silicon, and only one
works here.** VA-API's MJPEG encoder fails with `-22` even with an explicit
`scale_vaapi=format=nv12`; QSV's works perfectly. Do not generalise from one API
to "the hardware".

**3. Listing is not capability.** `vainfo` advertises
`VAProfileJPEGBaseline: VAEntrypointEncPicture` and `ffmpeg -encoders` lists
`mjpeg_vaapi`. Both are true, and the encoder still refuses to run. Any
selection logic must verify by *producing frames*, never by querying support.

**4. NVIDIA cannot do this pipeline at all.** There is no NVIDIA MJPEG encoder in
ffmpeg (`mjpeg`, `mjpeg_qsv`, `mjpeg_vaapi` are the only three), so the full-GPU
path is structurally impossible on NVIDIA, and its decode-only paths are the
worst measured. Worth telling NVIDIA users rather than leaving them hunting for
a setting.

Note on the raw-BGRA row: replacing the MJPEG encode with raw output is nearly a
wash on ffmpeg's side, because swscale's YUV→BGRA conversion costs about what the
MJPEG encoder did (0.269 s vs 0.298 s). Its appeal was removing operation 3 — the
app's JPEG decode — at the price of 3.7 MB per frame through the pipe instead of
~118 KB, plus a raw framing path where any size mismatch desynchronises every
later frame.

## Why this was not implemented

QSV is a real 68% saving on ffmpeg, but ffmpeg is the smaller half of a small
total:

    today:    ffmpeg 4.3% + app 11.3% = 15.6% of ONE core
    with QSV: ffmpeg ~1.4% + app 11.3% = ~12.7% of one core

That is ~3% of one core, or **~0.14% of a 20-thread machine**. It also does
nothing for chassis temperature: with the machine 97% idle the CPU package sat at
72-83 °C, driven by the dGPU holding ~22 W to run an external display, not by
this app.

Doing it properly for other people's hardware is also not a one-line change. A
dropdown of API names would be actively misleading, because capability lists lie
(finding 3) and the winner is machine-specific and counter-intuitive (finding 1).
It would need: a platform-filtered candidate table, a real benchmark that runs
each candidate and requires frames before trusting it, a cache fingerprinted on
ffmpeg version and OS so a driver update re-tests, a runtime fallback for
pipelines that pass the benchmark and then fail live, and UI that presents
measured results rather than API names.

That is the right design if this is ever worth building — for a user on a
low-power CPU with a 4K doorbell it plausibly is. For the measured saving here,
it is not.
