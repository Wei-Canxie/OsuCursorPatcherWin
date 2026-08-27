# OsuCursorPatcherWin

> This project is developed based on [xyc-233/OsuCursirWin](https://github.com/xyc-233/OsuCursirWin)

English | [中文](README.md)

A Windows global osu!-style cursor replacement tool. It uses a semi-transparent animated GDI cursor overlay in normal desktop scenes, and automatically switches to an osu!-themed system cursor over DirectComposition surfaces (Start menu, Action Center, volume/clipboard flyouts) so the cursor is never lost.

## Features

- **Dual-mode cursor architecture**: normal scenes use a WinForms layered window + GDI rendering with semi-transparency, animation (rotation, scaling, glow); DirectComposition surfaces (Start menu, etc.) automatically switch to the osu!-themed system cursor
- **14 system cursor replacements**: covers all standard Windows pointer styles (arrow, I-beam, hand, move, resize, busy, link, etc.), each with an independently configured hotspot
- **8x supersampled rendering**: the cursor image is rendered at 8x resolution then bilinearly downsampled for smooth edges
- **Click-through**: `WS_EX_TRANSPARENT` + `WS_EX_LAYERED` — never blocks mouse input
- **Drag rotation**: the cursor rotates to follow the drag direction
- **Press scaling & glow**: scales and adds a glow layer (additive blending) while pressed
- **Hover sounds**: plays hover sound when entering clickable elements, tap sound on press/release
- **Auto-restore on exit**: restores the native system cursor on tray exit or abnormal exit
- **Settings window**: cursor size (16–64px), sound toggles/volume, auto-start

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

The settings file is created at `%APPDATA%\OsuCursorWin\settings.json` on first launch; changes apply in real time.

## Build

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
```

Output: `publish\OsuCursorWin.exe` (win-x64 framework-dependent single file, requires .NET 8 Desktop Runtime).

## Cursor not restored?

Normally exiting from the tray restores the system cursor. If the program was force-killed and the system cursor is missing, run:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\restore-cursor.ps1
```

## Project Structure

```
OsuCursorPatcherWin/
├── OsuCursorWin/           # Main program source
│   ├── MainWindow.cs       # WPF main window + mouse hook + state management
│   ├── GdiCursorOverlay.cs # Normal-scene cursor overlay (WinForms layered window)
│   ├── CursorReplacer.cs   # System cursor replacement engine (14 OCR IDs)
│   ├── NativeMethods.cs    # Win32 P/Invoke declarations
│   ├── AppSettings.cs      # Settings persistence
│   ├── SettingsWindow.cs   # WPF settings window
│   ├── TapSoundPlayer.cs   # Sound playback (NAudio low-latency)
│   └── OsuCursorWin.csproj # Project file
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
│   └── smoke.ps1           # Smoke test
└── README.md               # This file
```

## Notes

- Cursor images are from [ppy/osu-resources](https://github.com/ppy/osu-resources); the system cursor replacement theme is adapted from the web implementation of [solstice23/osu-cursor](https://github.com/solstice23/osu-cursor).
- Exclusive fullscreen games may not show the overlay; use borderless or windowed mode.
- The program uses a low-level mouse hook (`WH_MOUSE_LL`) for movement and click events.
- Audio plays on a dedicated NAudio thread and never blocks the UI.

## License

MIT License. See [LICENSE](LICENSE).