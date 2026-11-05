# DoorBird-PC

A cross-platform desktop client for [DoorBird](https://www.doorbird.com/) video intercom devices. Runs on Linux, Windows, and macOS.

Based on the original [DoorBird-Windows](https://gitlab.com/klikini/DoorBird-Windows) by Andy Castille.

## Features

- **Live View** — Three video modes: RTSP stream (H.264, decoded in-process with bundled FFmpeg, GPU zero-copy where available), MJPEG stream, or snapshot polling. Maximizable view with sidebar controls.
- **Door & Light Control** — Open door relay and activate IR light directly from the live view
- **Screenshot & Recording** — Save a snapshot as PNG or record video+audio to MP4 directly from the live view (H.264 remuxed without re-encoding, audio as AAC)
- **Intercom** — Full-duplex two-way audio (G.711 mu-law) with speaker and mic toggle buttons available in both the Intercom page and the Live View toolbar
- **History Browser** — Browse historical doorbell/motion images with previous/next navigation
- **Doorbell Chime** — Plays a synthesized two-tone chime through the selected audio output when someone rings the doorbell. The app registers a notification rule with the device on connect and runs a built-in HTTP listener for incoming events.
- **Audio Device Selection** — Choose specific speaker and microphone devices, with automatic fallback to system defaults
- **Auto-Connect** — Optionally connect to the device and open Live View automatically on startup
- **Settings Persistence** — All configuration saved to a local JSON file
- **Cross-Platform** — Runs on Linux, Windows, and macOS using Avalonia UI and .NET 8

## Download

Prebuilt, self-contained binaries for every platform are on the [Releases](https://github.com/Rookie0ne/Doorbird-PC/releases/latest) page. No .NET or FFmpeg install is needed.

| File | Platform |
|------|----------|
| `DoorBird-PC-linux-x64.zip` | Linux x64 (glibc 2.29 or newer: Ubuntu 20.04+, Debian 11+, Fedora, Arch) |
| `DoorBird-PC-linux-arm64.zip` | Linux ARM64 (Raspberry Pi 4/5, other aarch64 boards) |
| `DoorBird-PC-win-x64.zip` | Windows 10/11 x64 |
| `DoorBird-PC-macos-arm64.zip` | macOS 11+ on Apple Silicon (M1/M2/M3/M4) |
| `DoorBird-PC-macos-x64.zip` | macOS 11+ on Intel |

Unzip and run `DoorBird.App` (`DoorBird.App.exe` on Windows). The binaries are not code-signed: Windows SmartScreen and macOS Gatekeeper will warn on first launch. On macOS, if the app refuses to open, clear the quarantine flag on the unzipped folder:

```bash
xattr -dr com.apple.quarantine /path/to/unzipped/folder
```

## Prerequisites

### Linux (building from source)

```bash
# .NET 8 SDK (only needed for building, not for running published binaries)
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"

# PortAudio (required for intercom audio)
sudo apt install libportaudio2    # Debian/Ubuntu
# or
sudo dnf install portaudio        # Fedora
# or
sudo pacman -S portaudio           # Arch

# Bundled FFmpeg libraries (RTSP video mode and recording). Downloaded once from
# this repo's ffmpeg workflow artifacts (needs `gh auth login`), not installed
# system-wide; see native/ffmpeg/README.md
scripts/fetch-ffmpeg.sh linux-x64  # or linux-arm64
```

### Linux (running published binary)

```bash
sudo apt install libportaudio2     # Required — intercom audio
```

No FFmpeg install is needed: the release ships its own FFmpeg libraries next to the executable.
For GPU zero-copy video on Intel or AMD, the VA-API driver for your GPU must be present
(`intel-media-driver` / `mesa` on most distributions). Without it, or on NVIDIA, the app
decodes in software, which still uses far less CPU than 0.4.x. See
[HARDWARE-ACCELERATION.md](HARDWARE-ACCELERATION.md) for the measurements and why.

### macOS (building from source)

```bash
# .NET 8 SDK
brew install dotnet-sdk

# PortAudio (required for intercom audio)
brew install portaudio

# Bundled FFmpeg libraries (needs the GitHub CLI, `brew install gh`)
scripts/fetch-ffmpeg.sh osx-arm64  # or osx-x64
```

### macOS (running published binary)

```bash
brew install portaudio             # Required — intercom audio
```

No FFmpeg install is needed; hardware decode uses VideoToolbox when *Hardware Acceleration* is set to *Enabled*.

### Windows

- For building from source: install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- For building from source, also run `scripts/fetch-ffmpeg.sh win-x64` (Git Bash or WSL, needs the GitHub CLI) to download the bundled FFmpeg libraries
- For running a published binary: no prerequisites. PortAudio and FFmpeg are bundled.

## Build & Run

```bash
dotnet restore
dotnet build
dotnet run --project src/DoorBird.App
```

## Publish Standalone Executables

Build self-contained executables that don't require the .NET SDK to be installed:

```bash
# Linux x64
dotnet publish src/DoorBird.App -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o publish/linux-x64

# Linux ARM64 (Raspberry Pi 4/5, other aarch64 boards)
dotnet publish src/DoorBird.App -c Release -r linux-arm64 --self-contained -p:PublishSingleFile=true -o publish/linux-arm64

# Windows x64
dotnet publish src/DoorBird.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish/win-x64

# macOS Apple Silicon (M1/M2/M3/M4)
dotnet publish src/DoorBird.App -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -o publish/osx-arm64

# macOS Intel
dotnet publish src/DoorBird.App -c Release -r osx-x64 --self-contained -p:PublishSingleFile=true -o publish/osx-x64
```

The resulting binary is in the `publish/` directory and can be run directly:

```bash
# Linux (x64 or arm64)
./publish/linux-x64/DoorBird.App
./publish/linux-arm64/DoorBird.App

# Windows
publish\win-x64\DoorBird.App.exe

# macOS
./publish/osx-arm64/DoorBird.App
```

**Note:** On Linux and macOS, `libportaudio` is required for intercom audio (the app will show an error on startup if missing). FFmpeg is bundled on every platform; run `scripts/fetch-ffmpeg.sh <rid>` before publishing so the libraries are copied next to the executable. If they are missing, MJPEG and Snapshot modes still work and the Info page says why RTSP and recording are unavailable.

## Configuration

On first run, go to **Settings** and configure:

- **Device Host** — IP address or hostname of your DoorBird device
- **Username / Password** — Credentials from the DoorBird mobile app
- **Video Mode** — RTSP Stream (H.264, lowest latency), MJPEG Stream (default), or Snapshot Polling (1 fps fallback)
- **Hardware Acceleration** (RTSP only) — *Auto* (default) uses the GPU only when decoded frames can stay on it all the way to the screen, and verifies that by producing real frames; otherwise it decodes in software. *Enabled* forces GPU decoding even when frames must be copied back. *Disabled* always decodes on the CPU. The status line under the video shows which path is active, e.g. `RTSP (VAAPI zero-copy)`. Zero-copy exists on Linux (VA-API) today; Windows and macOS decode in software in *Auto* and through D3D11VA / VideoToolbox in *Enabled*. Background: [HARDWARE-ACCELERATION.md](HARDWARE-ACCELERATION.md).
- **Audio Output / Input** — Select specific speaker and microphone, or leave as system default
- **Notification Port** — Port for receiving push notifications (default: 8080)
- **Recording Path** — Directory for saving captured images
- **Auto-Connect** — Automatically connect and show Live View on startup

Settings are stored in:
- **Linux:** `~/.config/DoorBird/settings.json`
- **macOS:** `~/Library/Application Support/DoorBird/settings.json`
- **Windows:** `%APPDATA%\DoorBird\settings.json`

### Doorbell chime — Windows note

On Linux and macOS the notification listener binds to all interfaces (`http://+:<port>/`) without elevated privileges. On Windows, `HttpListener` rejects wildcard prefixes unless the binding is reserved with `netsh http add urlacl` or the app is running as administrator. The app handles this automatically by falling back to binding the specific LAN IP that routes to the device, which works without admin rights in nearly all home setups.

If for some reason that fallback also fails (e.g. the chosen `Notification Port` is already in use, or a firewall blocks inbound traffic), the listener will bind to `localhost` only — the app will still launch and everything else will work, but the device on your LAN won't be able to reach the listener and the doorbell chime won't sound. To fix it, either pick a different `Notification Port` in Settings, allow the app through Windows Firewall, or run once as administrator with:

```cmd
netsh http add urlacl url=http://+:8080/ user=Everyone
```

(replace `8080` with your configured `Notification Port`).

## Project Structure

- **src/DoorBird.API/** — Platform-independent library for the DoorBird HTTP API (`/bha-api/`)
- **src/DoorBird.App/** — Avalonia UI desktop application (MVVM with ReactiveUI)
  - **Video/** — in-process RTSP decoder, MP4 recorder, and the GPU zero-copy presenters (FFmpeg.AutoGen on the bundled FFmpeg libraries)
- **tests/DoorBird.VideoSmoke/** — headless smoke test: records a few seconds from a real device and verifies the MP4
- **native/ffmpeg/** — bundled FFmpeg libraries per runtime identifier, populated by `scripts/fetch-ffmpeg.sh` (see its README for what is compiled in)
- **.github/workflows/ffmpeg.yml** — builds the trimmed LGPL FFmpeg libraries for all five platforms; `scripts/fetch-ffmpeg.sh` downloads its artifacts
- **HARDWARE-ACCELERATION.md** — research notes and measurements behind the video pipeline design

## License

MIT — see [LICENSE](LICENSE)

## Trademark Notice

"DoorBird" and the DoorBird logo are registered trademarks of Bird Home Automation GmbH. This project is an independent, unofficial client and is not affiliated with, endorsed by, or sponsored by Bird Home Automation GmbH.
