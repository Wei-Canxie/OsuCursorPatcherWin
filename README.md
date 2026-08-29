# OsuCursorPatcherWin

> 此项目基于 [xyc-233/OsuCursirWin](https://github.com/xyc-233/OsuCursirWin) 开发

[English](README.en.md) | 中文

Windows 全局 osu! 风格光标替换工具。在普通桌面场景使用半透明 GDI 动画光标覆盖层，在开始菜单、操作中心、音量/剪贴板浮出等 DirectComposition 表面自动切换为 osu! 主题系统光标，实现无缝覆盖。

## 功能

- **双模式光标架构**：普通场景 → WinForms layered 窗口 + GDI 渲染，半透明、动画（旋转、缩放、发光）；DirectComposition 表面（开始菜单等）→ 自动切换为 osu! 主题系统光标，保证可见性
- **14 个系统光标替换**：覆盖所有标准 Windows 指针样式（箭头、I 型、手型、移动、调整大小、忙、链接等），每个光标独立配置热点位置
- **点击穿透**：`WS_EX_TRANSPARENT` + `WS_EX_LAYERED`，不影响鼠标操作
- **拖动旋转**：按住鼠标拖拽时光标跟随方向旋转
- **按下缩放与发光**：按下时缩放并叠加发光层（additive blending）
- **悬停音效**：UIA 检测进入可点击元素（按钮/链接/菜单项等）时播放悬停音效，按下/抬起播放敲击音效
- **退出自动恢复**：托盘退出或异常退出自动恢复系统原生光标
- 音效：敲击/悬停音效开关与音量
- 系统：Windows 服务安装/卸载、开机自启
- **WinUI 3 设置窗口**（`OsuCursorWin3`）：
  - 外观：主题（跟随系统/亮色/暗色）、窗口不透明度（0.3–1.0）、背景图片（选择/恢复默认）、背景模糊半径
  - 场景对齐：普通场景与 DC 场景的独立热点偏移调校
  - 侧边栏：紧凑模式、背景随主题变色、圆角、全高布局

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

首次启动会自动创建设置文件到 `%LOCALAPPDATA%\OsuCursorPatcherWin\settings.json`，修改设置实时生效。

## 构建

```powershell
# 一键构建（输出到 publish\ 或 publish-v2\）
powershell -ExecutionPolicy Bypass -File scripts\build.ps1

# 或直接构建 WinUI 3 主程序
cd OsuCursorWin3
dotnet build -c Release
```

构建产物：WinUI 3 主程序位于 `OsuCursorWin3\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\OsuCursorWin.exe`（需 .NET 8 Desktop Runtime；`background-default.jpg` 随 exe 一同输出）。

> 注：`OsuCursorWin/` 为旧版 WPF 实现（保留参考），当前主程序为 `OsuCursorWin3/`（WinUI 3）。

## 光标没有恢复？

正常情况下从托盘退出会自动恢复系统光标。如果程序被任务管理器强制结束导致系统光标不可见，运行：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\restore-cursor.ps1
```

## 项目结构

```
OsuCursorPatcherWin/
├── OsuCursorWin3/          # 主程序源代码（WinUI 3）
│   ├── App.xaml.cs         # 应用启动：音效播放器、覆盖层、引擎、设置窗口
│   ├── SettingsWindow.cs   # WinUI 3 设置窗口（外观/光标/对齐/音效/系统）
│   ├── AppearanceManager.cs# 背景/模糊/不透明度应用（Mica/Acrylic/默认）
│   ├── CursorEngine.cs     # 渲染引擎（鼠标钩子 + 高精度定时器 + 动画 + UIA 悬停检测）
│   ├── CursorReplacer.cs   # 系统光标替换引擎（14 个 OCR ID）
│   ├── GdiCursorOverlay.cs # 普通场景光标覆盖层（WinForms layered 窗口）
│   ├── NativeMethods.cs    # Win32 P/Invoke 声明
│   ├── AppSettings.cs      # 设置持久化
│   ├── TapSoundPlayer.cs   # 音效播放（NAudio 低延迟）
│   ├── TrayIcon.cs         # 系统托盘
│   ├── ServiceManager.cs   # Windows 服务管理
│   └── OsuCursorWin3.csproj# 项目文件（Windows App SDK 2.4.0）
├── OsuCursorWin/           # 旧版 WPF 实现（参考）
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
