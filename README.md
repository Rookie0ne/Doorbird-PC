# DoorBird-PC

A cross-platform desktop client for [DoorBird](https://www.doorbird.com/) video intercom devices. Runs on Linux, Windows, and macOS.

Based on the original [DoorBird-Windows](https://gitlab.com/klikini/DoorBird-Windows) by Andy Castille.

## Features

- **Live View** — Three video modes: RTSP stream (H.264 via ffmpeg), MJPEG stream, or snapshot polling. Maximizable view with sidebar controls.
- **Door & Light Control** — Open door relay and activate IR light directly from the live view
- **Intercom** — Full-duplex two-way audio (G.711 mu-law) with speaker and mic toggle buttons available in both the Intercom page and the Live View toolbar
- **History Browser** — Browse historical doorbell/motion images with previous/next navigation
- **Push Notifications** — Built-in HTTP listener receives doorbell, motion, and door-open events from the device
- **Audio Device Selection** — Choose specific speaker and microphone devices, with automatic fallback to system defaults
- **Auto-Connect** — Optionally connect to the device and open Live View automatically on startup
- **Settings Persistence** — All configuration saved to a local JSON file
- **Cross-Platform** — Runs on Linux, Windows, and macOS using Avalonia UI and .NET 8

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

# FFmpeg (optional — required only for RTSP video mode)
sudo apt install ffmpeg            # Debian/Ubuntu
# or
sudo dnf install ffmpeg            # Fedora
# or
sudo pacman -S ffmpeg              # Arch
```

### Linux (running published binary)

```bash
sudo apt install libportaudio2     # Required — intercom audio
sudo apt install ffmpeg            # Optional — only for RTSP video mode
```

### macOS (building from source)

```bash
# .NET 8 SDK
brew install dotnet-sdk

# PortAudio (required for intercom audio)
brew install portaudio

# FFmpeg (optional — required only for RTSP video mode)
brew install ffmpeg
```

### macOS (running published binary)

```bash
brew install portaudio             # Required — intercom audio
brew install ffmpeg                # Optional — only for RTSP video mode
```

### Windows

- For building from source: install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- For running a published binary: no prerequisites for MJPEG/Snapshot modes
- **Optional:** Install [FFmpeg](https://www.gyan.dev/ffmpeg/builds/) and add it to PATH for RTSP video mode

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

# Windows x64
dotnet publish src/DoorBird.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish/win-x64

# macOS Apple Silicon (M1/M2/M3/M4)
dotnet publish src/DoorBird.App -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -o publish/osx-arm64

# macOS Intel
dotnet publish src/DoorBird.App -c Release -r osx-x64 --self-contained -p:PublishSingleFile=true -o publish/osx-x64
```

The resulting binary is in the `publish/` directory and can be run directly:

```bash
# Linux
./publish/linux-x64/DoorBird.App

# Windows
publish\win-x64\DoorBird.App.exe

# macOS
./publish/osx-arm64/DoorBird.App
```

**Note:** On Linux and macOS, `libportaudio` is required for intercom audio (the app will show an error on startup if missing). FFmpeg is optional and only needed if you select RTSP as the video mode in Settings. On Windows, PortAudio is bundled; FFmpeg must be installed separately for RTSP mode.

## Configuration

On first run, go to **Settings** and configure:

- **Device Host** — IP address or hostname of your DoorBird device
- **Username / Password** — Credentials from the DoorBird mobile app
- **Video Mode** — MJPEG Stream (smooth, default) or Snapshot Polling (1 fps)
- **Audio Output / Input** — Select specific speaker and microphone, or leave as system default
- **Notification Port** — Port for receiving push notifications (default: 8080)
- **Recording Path** — Directory for saving captured images
- **Auto-Connect** — Automatically connect and show Live View on startup

Settings are stored in:
- **Linux:** `~/.config/DoorBird/settings.json`
- **macOS:** `~/Library/Application Support/DoorBird/settings.json`
- **Windows:** `%APPDATA%\DoorBird\settings.json`

## Project Structure

- **src/DoorBird.API/** — Platform-independent library for the DoorBird HTTP API (`/bha-api/`)
- **src/DoorBird.App/** — Avalonia UI desktop application (MVVM with ReactiveUI)

## License

MIT — see [LICENSE](LICENSE)

## Trademark Notice

"DoorBird" and the DoorBird logo are registered trademarks of Bird Home Automation GmbH. This project is an independent, unofficial client and is not affiliated with, endorsed by, or sponsored by Bird Home Automation GmbH.
