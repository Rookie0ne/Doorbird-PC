# DoorBird-PC

A cross-platform desktop client for [DoorBird](https://www.doorbird.com/) video intercom devices. Runs on Linux and Windows.

Based on the original [DoorBird-Windows](https://gitlab.com/klikini/DoorBird-Windows) by Andy Castille.

## Features

- Live camera view with auto-refresh
- Door open and light control
- Historical image browser
- Push notification receiver
- Image recording to disk
- Cross-platform (Linux & Windows)

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

## Build & Run

```bash
dotnet restore
dotnet build
dotnet run --project src/DoorBird.App
```

## Project Structure

- **src/DoorBird.API/** — Platform-independent library for the DoorBird HTTP API (`/bha-api/`)
- **src/DoorBird.App/** — Avalonia UI desktop application (MVVM architecture)

## Configuration

On first run, go to Settings and enter:
- Device IP address or hostname
- Username and password (from DoorBird app)
- Notification port (default: 8080)

Settings are stored in `~/.config/DoorBird/settings.json` (Linux) or `%APPDATA%/DoorBird/settings.json` (Windows).

## License

MIT — see [LICENSE](LICENSE)
