# osu! Cursor for Windows

[English](README.en.md) | 中文



https://github.com/user-attachments/assets/4e4088e9-9a9e-4c97-8205-bad2f7645db5



注意：部分代码由AI生成

Windows 全局光标覆盖层：

- 使用跟随鼠标的 160px 小窗口、点击穿透的 WPF 窗口绘制光标
- 临时用透明系统光标隐藏 Windows 原生指针
- 支持拖动时旋转、按下时缩放/发光、悬停可点击元素时播放音效
- 退出后自动恢复原系统光标
- 音效支持


## 运行

双击exe程序，不需要安装。

或使用源码运行：

```powershell
dotnet run --project OsuCursorWin\OsuCursorWin.csproj
```

需要 Windows 10/11 和 .NET 8 Desktop Runtime。程序默认请求管理员权限，启动时会弹出 UAC；由于 `uiAccess` 需要位于安全目录并签名，源码直跑可能无法覆盖开始菜单等 Shell 窗口，正式使用请运行安装脚本：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-uiaccess.ps1
```

首次启动会自动打开设置窗口；之后右键托盘图标选择“设置”可重新打开。修改会实时生效并保存到 `%APPDATA%\OsuCursorWin\settings.json`。最小化或关闭设置窗口只会隐藏到后台，不会退出程序。

敲击音效使用 `音效\cursor-tap.wav`，按下和抬起都会播放；悬停音效使用 `音效\default-hover.wav`，进入可点击元素时播放，并使用 osu 20ms 去抖。所有音频通过独立音频线程和 NAudio 低延迟 WaveOut 通道池播放，UI 线程只负责排队，不会因为音效生成或播放而卡顿。

程序默认请求管理员权限，并带有 `uiAccess="true"`，首次启动会弹出 UAC 提示。普通 `SetWindowPos` 只能改变桌面窗口带内的顺序，无法覆盖开始菜单等沉浸式 Shell 窗口；`uiAccess` 会把覆盖窗口放到更高的 `ZBID_UIACCESS` 窗口带。

要启用 `uiAccess`，exe 必须位于 `C:\Program Files\` 等安全目录，并且使用本机受信任证书签名。仓库里提供了安装脚本：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-uiaccess.ps1
```

请以管理员身份运行该脚本。脚本会停止当前实例、创建并信任本地代码签名证书、把 exe 复制到 `C:\Program Files\OsuCursorWin\` 并签名。之后启动 `C:\Program Files\OsuCursorWin\OsuCursorWin.exe`。

程序使用低层鼠标钩子接收移动和点击事件，再由 WPF 按需渲染。

光标覆盖层会检测 Windows 任务栏悬浮预览窗口（`TaskListThumbnailWnd`），并把覆盖层临时提到预览窗口之上，避免光标被任务栏预览遮挡。

## 构建

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
```

构建产物会输出到 `publish\OsuCursorWin.exe`。默认生成 `win-x64` 框架依赖单文件版本，需要安装 .NET 8 Desktop Runtime。

## 如果光标没有恢复

正常情况下从托盘退出会自动恢复。如果程序被任务管理器强制结束，系统光标可能仍处于透明状态，可以运行：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\restore-cursor.ps1
```

## 说明

- 仅适用于 Windows 10/11。
- 独占全屏的游戏可能不会显示覆盖层，建议在无边框或窗口化模式下使用。
- 光标图片来自原 `assets/cursor.png` 和 `assets/cursor-additive.png`。
- 悬停音效需要应用支持，效果并不好建议开启”窗口拉伸时播放“选项以改为光标在窗口可拉伸时播放。

## 参考与感谢

- 参考 [solstice23/osu-cursor](https://github.com/solstice23/osu-cursor) 的网页自定义光标实现。
- 光标图片来自 [ppy/osu-resources](https://github.com/ppy/osu-resources)。

## 预计可能出现的问题

- 任务栏预览应用窗口时光标被覆盖导致光标消失。
- 有光标的游戏可能会出现同时存在两个光标。
- ~~未知情况下光标旋转时卡顿，释放时正常。~~
- 在文件资源管理器中光标快速移动时悬停提示会出现撕裂。
