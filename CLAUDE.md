# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Cross-platform desktop client for DoorBird video intercom devices, ported from the original [DoorBird-Windows](https://gitlab.com/klikini/DoorBird-Windows) WPF app to Avalonia UI for Linux + Windows support. Licensed under MIT.

## Build & Run

```bash
# Requires .NET 8 SDK (installed at ~/.dotnet via Microsoft install script)
export DOTNET_ROOT="$HOME/.dotnet" && export PATH="$DOTNET_ROOT:$PATH"

dotnet build                              # Build entire solution
dotnet run --project src/DoorBird.App     # Run the desktop app
dotnet build src/DoorBird.API             # Build API library only
```

## Architecture

Two-project solution (`DoorBird.sln`):

### DoorBird.API (class library)
Platform-independent HTTP client for the DoorBird `/bha-api/` REST API. Uses HTTP Basic auth against the device's LAN IP.

- `DoorBirdDevice` → abstract base; `DoorBirdUserDevice` → full API client (live image/video, door open, light, history, notifications, RTSP, audio)
- `Util/BhaHttp.cs` → three HTTP client classes: `BhaHttp` (raw), `BhaHttpMap` (key=value parsing), `BhaHttpJson` (JSON via Newtonsoft.Json, extracts `"BHA"` root)
- `Util/BhaUriTools.cs` → builds authenticated URIs with `/bha-api/` prefix
- `Util/HttpImage.cs` → image download with `X-Timestamp` header parsing and date-organized file saving

### DoorBird.App (Avalonia MVVM)
Cross-platform desktop app using Avalonia UI + ReactiveUI. Convention-based `ViewLocator` maps `*ViewModel` → `*View`.

- **ViewModels**: `MainWindowViewModel` (page navigation + connection), `LiveViewModel` (RTSP video + image polling fallback + door/light controls), `IntercomViewModel` (bidirectional audio), `HistoryViewModel` (image browser), `SettingsViewModel` (device config), `HomeViewModel`
- **Services**: `DeviceService` (manages `DoorBirdUserDevice` lifecycle), `AudioService` (PortAudio-based bidirectional G.711 mu-law audio over HTTP), `MuLawCodec` (G.711 mu-law encode/decode), `PushNotificationServer` (cross-platform `HttpListener` for push events)
- **Models**: `AppSettings` (JSON-persisted to `~/.config/DoorBird/settings.json` on Linux, `%APPDATA%/DoorBird/` on Windows)

## Key Patterns

- All DoorBird API calls go through `BhaHttp*` classes which add Basic auth headers and handle self-signed certs
- ViewModels use `ReactiveCommand` for async operations
- **Video**: LibVLCSharp provides RTSP streaming via `VideoView` control; falls back to 1-second image polling (`image.cgi`) if RTSP fails. Linux requires system `libvlc-dev`; Windows uses the `VideoLAN.LibVLC.Windows` NuGet package.
- **Audio**: PortAudioSharp2 for cross-platform mic capture/playback. Audio is G.711 mu-law at 8 kHz mono — received via HTTP GET from `audio-receive.cgi`, transmitted via HTTP POST to `audio-transmit.cgi`. `MuLawCodec` handles encode/decode.
- The original project used WPF + .NET Framework 4.5 + NAudio; this port uses Avalonia 11 + .NET 8 + LibVLCSharp + PortAudio

## GitHub

Repository: `github.com/Rookie0ne/Doorbird-PC` (private, branch: `main`)
