# MeloNX 目录结构导航

> 用途：快速定位代码与工程资源。本文只记录稳定目录和关键入口，不展开生成物、第三方二进制及全部源码文件。
>
> 当前分支：`XC-ios-ht`
> 最后核对：2026-07-27

## 1. 根目录

```text
melonx_codex/
├── .gitea/                     # Gitea 工作流
├── .github/                    # GitHub 工作流、Issue 模板及仓库配置
├── distribution/              # iOS、macOS、Linux、Windows 分发与构建脚本
├── docs/                      # 编码规范和协作流程文档
├── src/                       # Swift 前端、Ryujinx 核心及测试项目
├── AGENTS.md                  # Codex 项目协作约束
├── ARCHITECTURE.md            # 项目架构说明
├── Compile.md                 # macOS / Xcode 编译说明
├── CONTRIBUTING.md            # 贡献指南
├── Directory.Packages.props   # .NET 依赖版本集中管理
├── global.json                # .NET SDK 版本约束
├── MeloNX-hv.entitlements     # Apple Hypervisor 权限配置
├── nuget.config               # NuGet 源配置
├── README.md                  # 项目介绍、安装和使用说明
├── Ryujinx.sln                # 根级 .NET 解决方案
└── source.json                # 项目源信息
```

## 2. iOS 前端：`src/MeloNX`

```text
src/MeloNX/
├── MeloNX.xcodeproj/          # Xcode 工程、共享 Scheme 和 SwiftPM 配置
├── MeloNX.xcconfig            # Xcode 构建配置
├── MeloNXTests/               # iOS 单元测试
├── MeloNXUITests/             # iOS UI 测试
└── MeloNX/
    ├── App/
    │   ├── Core/              # 非 UI 的平台能力与模拟器接入
    │   │   ├── Controllers/   # 实体、远程手柄，体感与震动
    │   │   ├── Debugging/     # 崩溃回溯与看门狗
    │   │   ├── Entitlements/  # iOS 权限检测
    │   │   ├── Headers/       # Swift、C、Objective-C 桥接头文件
    │   │   ├── JIT/           # JIT 状态与启用方式
    │   │   └── Ryujinx/       # 核心生命周期、桥接、日志及 ROM 管理
    │   ├── Intents/           # Siri / App Intents
    │   ├── Models/            # 游戏、账户、手柄布局及配置模型
    │   └── UI/
    │       ├── Emulation/     # 运行游戏时的画面、触控和虚拟手柄
    │       ├── Main/          # 首页、游戏列表、设置、日志和更新
    │       ├── Setup/         # 首次启动配置
    │       ├── AppDelegate.swift
    │       ├── EmulatorRuntimeView.swift
    │       └── MeloNXApp.swift # SwiftUI 应用入口
    ├── Assets/                # 图片、App Icon、音乐等资源
    └── Dependencies/          # Framework、XCFramework 等原生依赖
```

### iOS 常用定位

| 要查找的功能 | 主要位置 |
| --- | --- |
| 应用启动与页面路由 | `src/MeloNX/MeloNX/App/UI/MeloNXApp.swift`、`EmulatorRuntimeView.swift` |
| 游戏列表与启动流程 | `src/MeloNX/MeloNX/App/UI/Main/Home/GamesList/`、`LaunchGameHandler.swift` |
| 模拟器启动、停止与线程 | `src/MeloNX/MeloNX/App/Core/Ryujinx/Ryujinx.swift`、`Runner.swift` |
| Swift 与 Ryujinx 核心桥接 | `src/MeloNX/MeloNX/App/Core/Ryujinx/RyujinxBridge.swift`、`Core/Headers/` |
| Metal 画面与触控 | `src/MeloNX/MeloNX/App/UI/Emulation/MetalView/` |
| 虚拟手柄与布局编辑 | `src/MeloNX/MeloNX/App/UI/Emulation/ControllerView/` |
| 实体手柄、远程手柄、体感、震动 | `src/MeloNX/MeloNX/App/Core/Controllers/` |
| JIT 检测与启用 | `src/MeloNX/MeloNX/App/Core/JIT/` |
| 权限检测 | `src/MeloNX/MeloNX/App/Core/Entitlements/` |
| 设置页 | `src/MeloNX/MeloNX/App/UI/Main/Home/SettingsView/` |
| 日志与性能数据 | `src/MeloNX/MeloNX/App/Core/Ryujinx/Logs/`、`Display/` |
| 账户与 ROM 目录 | `src/MeloNX/MeloNX/App/Core/Ryujinx/AccountManager/`、`ROMFolders/` |

## 3. Ryujinx 核心：`src`

### 应用入口与 UI

| 目录 | 职责 |
| --- | --- |
| `src/Ryujinx/` | Avalonia 桌面应用入口、视图和资源 |
| `src/Ryujinx.Ava/` | Avalonia UI 组件与视图 |
| `src/Ryujinx.Gtk3/` | GTK3 桌面前端 |
| `src/Ryujinx.Headless.SDL2/` | SDL2 无界面运行入口 |
| `src/Ryujinx.UI.Common/` | 多前端共享的配置、模型、资源与系统信息 |
| `src/Ryujinx.UI.LocaleGenerator/` | UI 本地化代码生成 |

### CPU 与内存

| 目录 | 职责 |
| --- | --- |
| `src/ARMeilleure/` | ARM 指令解码、IR、优化、代码生成与翻译缓存 |
| `src/Ryujinx.Cpu/` | CPU 上下文、JIT、LightningJIT、Apple Hypervisor |
| `src/Ryujinx.Memory/` | 虚拟内存、内存区间与访问追踪 |

### HLE 与 Horizon OS

| 目录 | 职责 |
| --- | --- |
| `src/Ryujinx.HLE/` | Switch 高层模拟、HOS 服务、加载器与虚拟文件系统 |
| `src/Ryujinx.HLE.Generators/` | HLE 相关源码生成器 |
| `src/Ryujinx.Horizon/` | Horizon 用户态服务及 SDK 类型 |
| `src/Ryujinx.Horizon.Common/` | Horizon 公共基础类型 |
| `src/Ryujinx.Horizon.Generators/` | Horizon IPC 源码生成器 |
| `src/Ryujinx.Horizon.Kernel.Generators/` | Horizon 内核源码生成器 |

### 图形、着色器与视频

| 目录 | 职责 |
| --- | --- |
| `src/Ryujinx.Graphics.Device/` | 图形设备抽象 |
| `src/Ryujinx.Graphics.GAL/` | 图形抽象层及多线程命令模型 |
| `src/Ryujinx.Graphics.Gpu/` | GPU 引擎、显存、图像与同步 |
| `src/Ryujinx.Graphics.Host1x/` | NVIDIA Host1x 模拟 |
| `src/Ryujinx.Graphics.Shader/` | 着色器解码、IR、优化和 GLSL/SPIR-V 生成 |
| `src/Ryujinx.Graphics.Texture/` | ASTC、纹理编码与转换 |
| `src/Ryujinx.Graphics.OpenGL/` | OpenGL 后端 |
| `src/Ryujinx.Graphics.Vulkan/` | Vulkan / MoltenVK 后端 |
| `src/Ryujinx.Graphics.Nvdec/` | NVDEC 视频解码公共实现 |
| `src/Ryujinx.Graphics.Nvdec.FFmpeg/` | FFmpeg 视频解码后端 |
| `src/Ryujinx.Graphics.Nvdec.Vp9/` | VP9 解码实现 |
| `src/Ryujinx.Graphics.Vic/` | VIC 图像处理 |
| `src/Ryujinx.Graphics.Video/` | 视频接口与公共类型 |
| `src/Ryujinx.ShaderTools/` | 着色器工具 |
| `src/Spv.Generator/` | SPIR-V 自动生成代码 |

### 音频与输入

| 目录 | 职责 |
| --- | --- |
| `src/Ryujinx.Audio/` | 音频输入、输出、渲染器及后端抽象 |
| `src/Ryujinx.Audio.Backends.Apple/` | Apple 音频后端 |
| `src/Ryujinx.Audio.Backends.OpenAL/` | OpenAL 音频后端 |
| `src/Ryujinx.Audio.Backends.SDL2/` | SDL2 音频后端 |
| `src/Ryujinx.Audio.Backends.SoundIo/` | libsoundio 音频后端 |
| `src/Ryujinx.Input/` | 输入分配、HLE 输入、体感与原生输入 |
| `src/Ryujinx.Input.SDL2/` | SDL2 输入实现 |
| `src/Ryujinx.SDL2.Common/` | SDL2 公共封装 |

### 公共基础与测试

| 目录 | 职责 |
| --- | --- |
| `src/Ryujinx.Common/` | 配置、日志、集合、内存工具及平台互操作 |
| `src/Ryujinx.Tests/` | CPU、HLE、音频、内存等核心测试 |
| `src/Ryujinx.Tests.Memory/` | 内存专项测试 |
| `src/Ryujinx.Tests.Unicorn/` | 基于 Unicorn 的 CPU 对照测试 |
| `src/src.sln` | `src` 下的 .NET 解决方案入口 |

## 4. 构建、分发与文档

```text
distribution/
├── ios/                       # iOS 编译、版本处理与 Xcode 构建脚本
├── legal/                     # 第三方许可证
├── linux/                     # Linux 桌面入口与 MIME 配置
├── macos/                     # macOS App Bundle 与通用二进制构建
├── misc/                      # Logo 和归档辅助脚本
└── windows/                   # Windows 运行时配置

docs/
├── coding-guidelines/         # C# 编码规范
├── workflow/                  # PR 工作流
└── README.md                  # 文档索引
```

构建 iOS 应用时，优先查看：

1. `Compile.md`
2. `src/MeloNX/MeloNX.xcodeproj`
3. `src/MeloNX/MeloNX.xcconfig`
4. `distribution/ios/compile.sh`
5. `distribution/ios/xc-compile.sh`

## 5. 查找建议

```bash
# 按文件名查找
rg --files | rg '关键词'

# 在 Swift 前端中查找符号
rg '符号名' src/MeloNX

# 在 Ryujinx C# 核心中查找符号
rg '符号名' src -g '*.cs'

# 查找项目文件
rg --files src -g '*.csproj' -g '*.xcodeproj/**' -g '*.sln'
```

以下内容不应纳入目录导航：`.git/`、`bin/`、`obj/`、构建产物、缓存，以及 `Dependencies/` 内部的第三方二进制细节。
