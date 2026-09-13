# OsuCursorPatcherWin

> This project is developed based on [xyc-233/OsuCursirWin](https://github.com/xyc-233/OsuCursirWin)

English | [中文](README.md)

A Windows global osu!-style cursor replacement tool. It uses a semi-transparent animated GDI cursor overlay in normal desktop scenes, and automatically switches to an osu!-themed system cursor over DirectComposition surfaces (Start menu, Action Center, volume/clipboard flyouts) so the cursor is never lost.

## Download

Current version **v1.1.1**, in three packaging flavours (identical features, only the runtime packaging differs):

| Asset | Dependencies | Size |
|---|---|---|
| `OsuCursorWin.exe` | None (.NET and the Windows App SDK are bundled) - download and run | ~308 MB |
| `OsuCursorWin-dotnet-only.zip` (recommended) | Only the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) | ~55 MB |
| `OsuCursorWin-full-framework-dependent.zip` | .NET 8 + the [Windows App SDK Runtime 2.4](https://aka.ms/windowsappsdk/2.4/latest/windowsappruntimeinstall-x64.exe) | ~29 MB |

Downloads: [latest release](https://github.com/Wei-Canxie/OsuCursorPatcherWin/releases/latest). For the zips, extract anywhere and run `OsuCursorWin.exe` inside (it requests UAC elevation itself).

## Features

- **Dual-mode cursor architecture**: normal scenes use a WinForms layered window + GDI rendering with semi-transparency, animation (rotation, scaling, glow); DirectComposition surfaces (Start menu, etc.) automatically switch to the osu!-themed system cursor
- **14 system cursor replacements**: covers all standard Windows pointer styles (arrow, I-beam, hand, move, resize, busy, link, etc.), each with an independently configured hotspot
- **Click-through**: `WS_EX_TRANSPARENT` + `WS_EX_LAYERED` — never blocks mouse input
- **Drag rotation**: the cursor rotates to follow the drag direction
- **Press scaling & glow**: scales and adds a glow layer (additive blending) while pressed
- **Hover sounds**: UIA-based detection plays a hover sound when entering clickable elements (buttons/links/menu items), and a tap sound on press/release
- **Auto-restore on exit**: restores the native system cursor on tray exit or abnormal exit
- **Sounds**: independent toggles and volume for tap/hover sounds
- **System**: Windows service install/uninstall, auto-start on boot
- **WinUI 3 settings window** (`OsuCursorWin3`):
  - Appearance: theme (follow system/light/dark), window opacity (0.3–1.0, pinned to 100% under Mica/Acrylic), background image (pick/restore default), background blur radius
  - Scene alignment: independent hotspot offset tuning for normal and DC scenes
  - Sidebar: compact mode, theme-following background color, rounded corners, 1px inset, expand/collapse animation (collapse replays the expand curve in reverse)
  - **Apply on demand**: edits go into a draft first, with floating Cancel / Apply buttons in the window's bottom-right corner; dragging a slider no longer rebuilds the page or interrupts the drag
  - Caption buttons (minimize/maximize/close) follow the in-app theme (black in light mode)
- **Mica / Acrylic backgrounds**: WinUI 3 system backdrops, Windows 11 only; the window opacity is pinned to 100% while one is active, since the backdrop owns the window surface
- **Cursor animation speed**: rotation, scaling, glow and drag-return all run at 1.33x, keeping the original trajectory shape and just speeding it up

## Requirements

- Windows 10 / 11 (64-bit)
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (Windows version)
- Administrator privileges (UAC is requested automatically)

## Quick Start

```powershell
# Clone the repository
git clone https://github.com/Wei-Canxie/OsuCursorPatcherWin.git
cd OsuCursorPatcherWin

# Build (requires .NET 8 SDK)
powershell -ExecutionPolicy Bypass -File scripts\build.ps1

# Build output is at publish\OsuCursorWin.exe
# Double-click to run
```

## Run

Double-click `publish\OsuCursorWin.exe`. The program elevates via UAC and hides to the system tray. Right-click the tray icon to open settings or exit.

The settings file is created at `%LOCALAPPDATA%\OsuCursorPatcherWin\settings.json` on first launch. Edits go into a draft: press Apply in the window's bottom-right corner to commit them, or Cancel to drop the change.

## Build

```powershell
# One-click build (outputs to publish\ or publish-v2\)
powershell -ExecutionPolicy Bypass -File scripts\build.ps1

# Or build the WinUI 3 main program directly
cd OsuCursorWin3
dotnet build -c Release
```

Build output: the WinUI 3 main program is at `OsuCursorWin3\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\OsuCursorWin.exe` (requires .NET 8 Desktop Runtime; `background-default.jpg` is shipped alongside the exe).

Release builds (the three flavours published in each release):

```powershell
$P = "OsuCursorWin3\OsuCursorWin3.csproj"

# Self-contained single file (~308 MB, no dependencies)
dotnet publish $P -c Release -p:Platform=x64 -r win-x64 --self-contained true `
    -p:WindowsAppSDKSelfContained=true -p:PublishSingleFile=true `
    -p:IncludeAllContentForSelfExtract=true -p:EnableMsixTooling=true -o publish\selfcontained

# .NET-only (~55 MB, Windows App SDK bundled)
dotnet publish $P -c Release -p:Platform=x64 -r win-x64 --self-contained false `
    -p:WindowsAppSDKSelfContained=true -o publish\dotnet-only

# Fully framework-dependent (~29 MB)
dotnet publish $P -c Release -p:Platform=x64 -r win-x64 --self-contained false `
    -p:WindowsAppSDKSelfContained=false -o publish\full-fwdep
```

> Note: `PublishSingleFile` requires `EnableMsixTooling=true` (the Windows App SDK's SingleFile.targets insists on it). `assets/` is remapped to `Assets/` by the Windows App SDK, so the default background lives at the project root and `assets/**` is excluded from the Content glob - otherwise single-file bundling fails on duplicate entries.

> Note: `OsuCursorWin/` is the legacy WPF implementation (kept for reference); the current main program is `OsuCursorWin3/` (WinUI 3).

## Cursor not restored?

Normally exiting from the tray restores the system cursor. If the program was force-killed and the system cursor is missing, run:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\restore-cursor.ps1
```

## Project Structure

```
OsuCursorPatcherWin/
├── OsuCursorWin3/          # Main program source (WinUI 3)
│   ├── App.xaml.cs         # App startup: sound players, overlay, engine, settings window
│   ├── AppLog.cs           # Logging (%TEMP%\OsuCursorWin.log)
│   ├── SettingsWindow.cs   # WinUI 3 settings window (appearance/cursor/align/sound/system)
│   ├── AppearanceManager.cs# Background/blur/opacity (Mica/Acrylic/default)
│   ├── CursorEngine.cs     # Rendering engine (mouse hook + high-res timer + animation + UIA hover detection)
│   ├── CursorReplacer.cs   # System cursor replacement engine (14 OCR IDs)
│   ├── GdiCursorOverlay.cs # Normal-scene cursor overlay (WinForms layered window)
│   ├── NativeMethods.cs    # Win32 P/Invoke declarations
│   ├── AppSettings.cs      # Settings persistence
│   ├── AutoStartManager.cs # Auto-start management
│   ├── TapSoundPlayer.cs   # Sound playback (NAudio low-latency)
│   ├── TrayIcon.cs         # System tray
│   ├── ServiceManager.cs   # Windows service management
│   ├── background-default.jpg  # Default background image (shipped beside the exe)
│   └── OsuCursorWin3.csproj# Project file (Windows App SDK 2.4.0)
├── OsuCursorWin/           # Legacy WPF implementation (reference)
├── assets/                 # Cursor resources
│   ├── cursor.png          # Normal-scene cursor image (main)
│   ├── cursor-additive.png # Glow overlay image
│   └── ex/                 # System cursor replacement theme (.cur/.ani)
│       ├── hand.cur, link.cur, text.cur, ...
│       ├── work.ani, busy.png
│       └── ...
├── scripts/                # Build/utility scripts
│   ├── build.ps1           # Build script
│   ├── restore-cursor.ps1  # Restore system cursor
│   ├── smoke.ps1           # Smoke test
│   ├── install-uiaccess.ps1# Install into Program Files (UIAccess)
│   └── stop-running.ps1    # Kill a running instance
└── README.md               # This file
```

## Notes

- Cursor images are from [ppy/osu-resources](https://github.com/ppy/osu-resources); the system cursor replacement theme is adapted from the web implementation of [solstice23/osu-cursor](https://github.com/solstice23/osu-cursor).
- Exclusive fullscreen games may not show the overlay; use borderless or windowed mode.
- The program uses a low-level mouse hook (`WH_MOUSE_LL`) for movement and click events.
- Audio plays on a dedicated NAudio thread and never blocks the UI.

## License

MIT License. See [LICENSE](LICENSE).
