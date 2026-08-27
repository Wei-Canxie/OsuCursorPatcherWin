# OsuCursorPatcherWin

> 此项目基于 [xyc-233/OsuCursirWin](https://github.com/xyc-233/OsuCursirWin) 开发

[English](README.en.md) | 中文

> ⚠️ **当前版本状态：v0.19 pre-release**。此版本处于开发测试阶段，**可能存在未知 Bug，不建议日常使用**。已知待改进项：光标渲染流畅度与原生系统光标仍有差距、边缘抗锯齿仍在优化中。使用前请确保可从任务栏托盘正常退出以恢复系统光标（异常退出可用 `scripts\restore-cursor.ps1` 恢复）。

Windows 全局 osu! 风格光标替换工具。在普通桌面场景使用半透明 GDI 动画光标覆盖层，在开始菜单、操作中心、音量/剪贴板浮出等 DirectComposition 表面自动切换为 osu! 主题系统光标，实现无缝覆盖。

## 功能

- **双模式光标架构**：普通场景 → WinForms layered 窗口 + GDI 渲染，半透明、动画（旋转、缩放、发光）；DirectComposition 表面（开始菜单等）→ 自动切换为 osu! 主题系统光标，保证可见性
- **14 个系统光标替换**：覆盖所有标准 Windows 指针样式（箭头、I 型、手型、移动、调整大小、忙、链接等），每个光标独立配置热点位置
- **8x 超采样渲染**：光标图像先以 8 倍分辨率渲染再双线性下采样，边缘平滑
- **点击穿透**：`WS_EX_TRANSPARENT` + `WS_EX_LAYERED`，不影响鼠标操作
- **拖动旋转**：按住鼠标拖拽时光标跟随方向旋转
- **按下缩放与发光**：按下时缩放并叠加发光层（additive blending）
- **悬停音效**：进入可点击元素时播放悬停音效，按下/抬起播放敲击音效
- **退出自动恢复**：托盘退出或异常退出自动恢复系统原生光标
- **设置窗口**：光标大小（16–64px）、音效开关/音量、开机自启

## 系统要求

- Windows 10 / 11（64 位）
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)（Windows 版本）
- 管理员权限（程序自动请求 UAC）

## 快速开始

```powershell
# 克隆仓库
git clone https://github.com/Wei-Canxie/OsuCursorPatcherWin.git
cd OsuCursorPatcherWin

# 构建（需要 .NET 8 SDK）
powershell -ExecutionPolicy Bypass -File scripts\build.ps1

# 构建产物位于 publish\OsuCursorWin.exe
# 双击运行即可
```

## 运行

双击 `publish\OsuCursorWin.exe`，程序自动启动 UAC 提权并隐藏到系统托盘。右键托盘图标可打开设置窗口或退出程序。

首次启动会自动创建设置文件到 `%APPDATA%\OsuCursorWin\settings.json`，修改设置实时生效。

## 构建

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
```

构建产物：`publish\OsuCursorWin.exe`（win-x64 框架依赖单文件，需 .NET 8 Desktop Runtime）。

## 光标没有恢复？

正常情况下从托盘退出会自动恢复系统光标。如果程序被任务管理器强制结束导致系统光标不可见，运行：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\restore-cursor.ps1
```

## 项目结构

```
OsuCursorPatcherWin/
├── OsuCursorWin/           # 主程序源代码
│   ├── MainWindow.cs       # WPF 主窗口 + 鼠标钩子 + 状态管理
│   ├── GdiCursorOverlay.cs # 普通场景光标覆盖层（WinForms layered 窗口）
│   ├── CursorReplacer.cs   # 系统光标替换引擎（14 个 OCR ID）
│   ├── NativeMethods.cs    # Win32 P/Invoke 声明
│   ├── AppSettings.cs      # 设置持久化
│   ├── SettingsWindow.cs   # WPF 设置窗口
│   ├── TapSoundPlayer.cs   # 音效播放（NAudio 低延迟）
│   └── OsuCursorWin.csproj # 项目文件
├── assets/                 # 光标资源
│   ├── cursor.png          # 普通场景光标图像（主图）
│   ├── cursor-additive.png # 发光叠加层图像
│   └── ex/                 # 系统光标替换主题（.cur/.ani）
│       ├── hand.cur, link.cur, text.cur, ...
│       ├── work.ani, busy.png
│       └── ...
├── scripts/                # 构建/辅助脚本
│   ├── build.ps1           # 构建脚本
│   ├── restore-cursor.ps1  # 恢复系统光标
│   └── smoke.ps1           # 冒烟测试
└── README.md               # 本文件
```

## 说明

- 光标图像来自 [ppy/osu-resources](https://github.com/ppy/osu-resources)，系统光标替换主题基于 [solstice23/osu-cursor](https://github.com/solstice23/osu-cursor) 的网页实现改编。
- 独占全屏游戏可能不显示覆盖层，建议在无边框或窗口化模式下使用。
- 程序使用低层鼠标钩子（`WH_MOUSE_LL`）接收移动和点击事件。
- 音效使用 NAudio 独立音频线程播放，不会造成 UI 卡顿。

## 许可

本项目基于 MIT 许可证发布。详见 [LICENSE](LICENSE)。