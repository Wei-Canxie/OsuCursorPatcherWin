# osu! Cursor for Windows

[中文](README.md) | English

This project turns the dynamic cursor effect from the web version of the osu cursor into a Windows global cursor overlay.

- Uses a transparent, click-through WPF window to draw the cursor.
- Temporarily hides the native Windows pointer with a transparent system cursor.
- Supports rotation while dragging, scale/glow on press, and glow on clickable elements.
- Restores the original system cursor when the program exits.

The runtime uses a 160px window that follows the mouse instead of a full-screen overlay. Topmost updates are driven by mouse movement, clicks, Win key presses, or system cursor state changes, with a lightweight 250ms fallback to avoid immersive shell windows covering the cursor.

## Run

Run the published build directly:

```text
publish\OsuCursorWin.exe
```

Or run from source:

```powershell
dotnet run --project OsuCursorWin\OsuCursorWin.csproj
```

Requirements: Windows 10/11 and .NET 8 Desktop Runtime. The program requests administrator privileges and shows a UAC prompt on startup. Because `uiAccess` requires a signed executable in a secure location, running the source build directly may not overlay immersive shell windows such as the Start menu. For normal use, run the installer script:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-uiaccess.ps1
```

The program does not appear in the taskbar. It shows a tray icon, and selecting "Exit" from the tray restores the system cursor.

## Settings

The settings window opens automatically on first launch and can be reopened from the tray menu. It supports:

- Cursor size (16-64px)
- Auto-start
- Tap sound
- Tap sound volume
- Hover sound
- Hover sound volume
- "Play when stretching windows" mode

Changes are saved to `%APPDATA%\OsuCursorWin\settings.json`. Minimizing or closing the settings window hides it to the background instead of exiting the program.

The tray menu contains "Settings", "Disable Cursor / Enable Cursor", and "Exit". Disabling the cursor restores the system cursor and hides the osu cursor overlay while the program keeps running. Auto-start writes to the current user's `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

Tap sound uses `音效\cursor-tap.wav` and plays on press and release. Hover sound uses `音效\default-hover.wav` and plays when entering clickable elements, with the same 20ms global debounce as osu. All audio is played through a dedicated audio thread and a NAudio low-latency WaveOut channel pool, so UI rendering is not blocked by sound generation or playback.

The program icon is generated from `1.png` into `OsuCursorWin\1.ico` and is used for the exe, tray icon, and settings window.

## Build

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
```

The build outputs `publish\OsuCursorWin.exe`. It is a `win-x64` framework-dependent single-file build and requires .NET 8 Desktop Runtime.

## If the Cursor Is Not Restored

Normally, exiting from the tray restores the cursor automatically. If the program is force-killed and the system cursor remains transparent, run:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\restore-cursor.ps1
```

## Known Issues

- The cursor may be covered when a taskbar app thumbnail preview is open.
- Games that draw their own cursor may show two cursors at the same time.
- In unknown cases, cursor rotation can stutter while dragging and return to normal on release.

## Can Users Download and Run the exe Directly?

Yes, with these requirements:

- `publish\OsuCursorWin.exe` is a framework-dependent build and requires .NET 8 Desktop Runtime.
- The program requests administrator privileges and uses `uiAccess`. Double-clicking it directly may not overlay immersive shell windows such as the Start menu because it is not signed or installed in a secure location.
- It is recommended to run `scripts\install-uiaccess.ps1` as administrator after downloading. The script signs the exe and copies it to `C:\Program Files\OsuCursorWin\`.

## Notes

- Only Windows 10/11 is supported.
- Exclusive fullscreen games may not show the overlay; borderless or windowed mode is recommended.
- Cursor images come from the original `assets/cursor.png` and `assets/cursor-additive.png`.

## Credits

- Based on the web custom cursor implementation from [solstice23/osu-cursor](https://github.com/solstice23/osu-cursor).
- Cursor images are from [ppy/osu-resources](https://github.com/ppy/osu-resources).
