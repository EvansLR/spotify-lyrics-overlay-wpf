# Spotify Lyrics Overlay WPF

Native Windows rewrite of the Electron lyrics overlay. It keeps the core behavior while avoiding Chromium:

- Spotify OAuth PKCE login
- Current playback polling through Spotify Web API
- LRCLIB synced/plain lyrics lookup
- Local token, settings, and lyrics cache
- Transparent always-on-top lyrics window
- Tray show/hide and quit
- `Ctrl+Alt+L` lock toggle
- Click-through lock mode
- One-line/two-line mode
- Font size and text color settings

## Requirements

- Windows
- .NET 10 SDK or newer with Windows Desktop workload
- Spotify Developer app with Redirect URI:

```text
http://127.0.0.1:8766/callback
```

This machine currently has the .NET runtime but no SDK, so the project cannot be built here until the SDK is installed.

## Run

```powershell
dotnet run
```

## Publish

Framework-dependent:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false
```

Self-contained single folder:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true
```

## Data

Settings, token, and lyrics cache are stored under:

```text
%APPDATA%\SpotifyLyricsOverlayWpf
```

## Design Notes

This version avoids Electron's Chromium renderer and uses one WPF window plus a tray icon. Lyrics rendering is event-like: it schedules the next update around the next lyric timestamp instead of repainting at a fixed high frequency.
