# MeloNX 项目架构文档

## 项目概述

**MeloNX** 是一个基于 Ryujinx 的 iOS Nintendo Switch 模拟器，使用 C#（.NET 8.0）和 Swift 开发，在 GPLv3 许可下开源。项目将 Ryujinx 核心模拟引擎编译为原生代码，通过 Swift 前端为 iOS 设备提供 Switch 游戏模拟能力。

---

## 整体架构

```
┌─────────────────────────────────────────────────────────┐
│                   MeloNX iOS App (Swift)                │
│  ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌───────────┐  │
│  │ UI Layer │ │Controller│ │   JIT    │ │Entitlement│  │
│  │(SwiftUI) │ │ Manager  │ │Enabler   │ │  Checker  │  │
│  └────┬─────┘ └────┬─────┘ └────┬─────┘ └─────┬─────┘  │
│       └─────────────┴────────────┴─────────────┘         │
│                         │                                │
│              RyujinxBridge (Swift ↔ C Interop)           │
└─────────────────────────┬───────────────────────────────┘
                          │ @_silgen_name / C Headers
┌─────────────────────────┴───────────────────────────────┐
│              Ryujinx Core (C# .NET 8.0)                  │
│  ┌──────────┐ ┌──────────┐ ┌────────────────────────┐   │
│  │   HLE    │ │ Horizon  │ │     Graphics Stack     │   │
│  │(Services)│ │   (OS)   │ │ GPU→GAL→Vulkan/OpenGL  │   │
│  └────┬─────┘ └────┬─────┘ └───────────┬────────────┘   │
│       └─────────────┴─────────────────┘                  │
│                         │                                │
│  ┌──────────────────────┴──────────────────────────┐    │
│  │              CPU Emulation                       │    │
│  │  ARMeilleure (Translator) + Ryujinx.Cpu (JIT/hv)│    │
│  └──────────────────────┬──────────────────────────┘    │
│                         │                                │
│  ┌──────────────────────┴──────────────────────────┐    │
│  │        Ryujinx.Memory (Memory Management)        │    │
│  └──────────────────────┬──────────────────────────┘    │
│                         │                                │
│  ┌──────────────────────┴──────────────────────────┐    │
│  │    Ryujinx.Common (Utilities & Configuration)    │    │
│  └─────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────┘
```

---

## 目录结构

```
melonx/
│
├── .gitea/                              # Gitea CI/CD 配置
│   └── workflows/
│       ├── add_release_to_site.yml      # 发布时通知 melonx.org API
│       └── updateApp.yml                # 发布时更新 apps.json 仓库
│
├── .github/                             # GitHub 仓库配置
│   ├── ISSUE_TEMPLATE/                  # Issue 模板
│   ├── workflows/                       # GitHub Actions 工作流
│   ├── FUNDING.yml                      # 赞助配置
│   ├── dependabot.yml                   # 依赖更新配置
│   ├── labeler.yml                      # PR 自动标签
│   └── reviewers.yml                    # 代码审查者配置
│
├── distribution/                        # 各平台分发打包脚本
│   ├── ios/                             # iOS 编译脚本
│   │   ├── compile.sh                   # 主编译脚本
│   │   ├── get_dotnet.sh                # 获取 .NET SDK
│   │   ├── make-macOS-build.sh          # macOS 构建
│   │   ├── set_current_version.sh       # 版本号设置
│   │   └── xc-compile.sh                # Xcode 编译
│   ├── legal/
│   │   └── THIRDPARTY.md                # 第三方许可
│   ├── linux/                           # Linux 分发
│   │   ├── mime/                        # MIME 类型定义
│   │   ├── Ryujinx.desktop              # 桌面入口
│   │   ├── Ryujinx.sh                   # 启动脚本
│   │   └── shortcut-template.desktop    # 快捷方式模板
│   ├── macos/                           # macOS 分发
│   │   ├── Info.plist                   # App Bundle 信息
│   │   ├── Ryujinx.icns                 # 应用图标
│   │   ├── create_app_bundle.sh         # 创建 App Bundle
│   │   ├── create_macos_build_ava.sh    # Avalonia 构建
│   │   ├── create_macos_build_headless.sh # 无头构建
│   │   ├── entitlements.xml             # macOS 权限
│   │   └── updater.sh                   # 自动更新脚本
│   ├── misc/
│   │   ├── Logo.svg                     # 项目 Logo
│   │   └── add_tar_exec.py              # tar 权限修复工具
│   └── windows/
│       └── alsoft.ini                   # OpenAL 配置
│
├── docs/                                # 项目文档
│   ├── coding-guidelines/
│   │   └── coding-style.md              # 编码规范
│   ├── workflow/
│   │   └── pr-guide.md                  # PR 提交指南
│   └── README.md
│
├── src/                                 # 源代码目录
│   │
│   ├── MeloNX/                          # ★ iOS 前端应用 (Swift)
│   │   ├── MeloNX/
│   │   │   ├── App/
│   │   │   │   ├── Core/
│   │   │   │   │   ├── Controllers/     # 手柄管理
│   │   │   │   │   │   ├── BaseController.swift          # 手柄基类
│   │   │   │   │   │   ├── ControllerInputSink.swift     # 输入接收
│   │   │   │   │   │   ├── ControllerManager.swift       # 手柄管理器
│   │   │   │   │   │   ├── Motion/                       # 体感支持
│   │   │   │   │   │   ├── NativeController.swift        # MFi 原生手柄
│   │   │   │   │   │   └── Rumble/                       # 震动反馈
│   │   │   │   │   ├── Debugging/      # 调试工具
│   │   │   │   │   │   ├── MachBacktracer.h/m            # Mach 回溯
│   │   │   │   │   │   └── Watchdog.h/m                  # 看门狗
│   │   │   │   │   ├── Entitlements/   # 权限检查
│   │   │   │   │   │   └── EntitlementChecker.swift      # 内存权限检查
│   │   │   │   │   ├── Headers/        # C/ObjC 桥接头文件
│   │   │   │   │   │   ├── Ryujinx-Header.h              # 核心桥接定义
│   │   │   │   │   │   ├── MobileGestalt.h               # 设备信息
│   │   │   │   │   │   └── HookNSBundle.h/m              # NSBundle Hook
│   │   │   │   │   ├── JIT/            # JIT 编译器管理
│   │   │   │   │   │   ├── AskForJIT.swift                # JIT 请求
│   │   │   │   │   │   ├── BreakpointHandler.swift        # 断点处理
│   │   │   │   │   │   ├── IsJITEnabled.swift             # JIT 状态检测
│   │   │   │   │   │   ├── JitStreamerEB/EnableJIT.swift  # JitStreamer 方式
│   │   │   │   │   │   └── StikJIT/StikEnableJIT.swift    # StikJIT 方式
│   │   │   │   │   └── Ryujinx/        # Ryujinx 引擎交互
│   │   │   │   │       ├── Ryujinx.swift                  # 核心引擎管理类
│   │   │   │   │       ├── RyujinxBridge.swift            # C 互操作桥接层
│   │   │   │   │       ├── RyujinxError.swift             # 错误类型定义
│   │   │   │   │       ├── Runner.swift                   # 模拟线程管理
│   │   │   │   │       ├── AccountManager/                # 账户管理
│   │   │   │   │       ├── Display/                       # 显示监控 (FPS/内存)
│   │   │   │   │       ├── Logs/LogCapture.swift          # 日志捕获
│   │   │   │   │       ├── MetalHUD/MTLHUD.swift          # Metal HUD
│   │   │   │   │       └── ROMFolders/                    # ROM 文件夹管理
│   │   │   │   ├── Intents/            # Siri/快捷指令
│   │   │   │   │   └── LaunchGameIntent.swift
│   │   │   │   ├── Models/             # 数据模型
│   │   │   │   │   ├── Game.swift, Account.swift
│   │   │   │   │   ├── ControllerLayout.swift
│   │   │   │   │   ├── GameScheme.swift
│   │   │   │   │   └── Enums/          # 枚举 (语言/区域/手柄/UI)
│   │   │   │   └── UI/                 # 用户界面
│   │   │   │       ├── AppDelegate.swift
│   │   │   │       ├── EmulatorRuntimeView.swift
│   │   │   │       └── Emulation/      # 模拟界面
│   │   │   │           ├── ControllerView/  # 虚拟手柄视图
│   │   │   │           │   ├── Buttons/     # 按钮视图
│   │   │   │           │   ├── Joystick/    # 摇杆视图
│   │   │   │           │   └── Haptics/     # 触觉反馈
│   │   │   │           ├── AirPlay/         # AirPlay 投屏
│   │   │   │           └── ...              # 其他 UI 组件
│   │   │   └── Assets/                 # 资源文件 (图标/图片)
│   │   ├── MeloNX.xcodeproj/           # Xcode 工程文件
│   │   ├── MeloNXTests/                # 单元测试
│   │   ├── MeloNXUITests/              # UI 测试
│   │   └── MeloNX.xcconfig             # Xcode 构建配置
│   │
│   ├── ARMeilleure/                    # ★ CPU 指令翻译器
│   │   ├── CodeGen/                    # 代码生成 (ARM32/ARM64 → x86/ARM64)
│   │   ├── Common/                     # 通用类型和工具
│   │   ├── Decoders/                   # 指令解码器 (200+ 文件)
│   │   ├── Diagnostics/                # 诊断和调试工具
│   │   ├── Instructions/               # 指令定义 (55 文件)
│   │   ├── IntermediateRepresentation/ # 中间表示 (IR)
│   │   ├── Memory/                     # 模拟器内存管理
│   │   ├── Native/                     # 原生代码接口
│   │   ├── Signal/                     # 信号处理
│   │   ├── State/                      # CPU 状态管理
│   │   ├── Translation/               # 翻译器核心
│   │   ├── Allocators.cs              # 内存分配器
│   │   ├── Optimizations.cs           # 优化配置
│   │   └── Statistics.cs              # 性能统计
│   │
│   ├── Ryujinx.Cpu/                    # ★ CPU 模拟上下文
│   │   ├── AppleHv/                    # Apple Hypervisor 支持
│   │   │   ├── HvApi.cs               # Hypervisor API 封装
│   │   │   ├── HvCpuContext.cs         # HV CPU 上下文
│   │   │   ├── HvMemoryManager.cs      # HV 内存管理
│   │   │   ├── HvVcpu.cs / HvVcpuPool.cs # vCPU 管理
│   │   │   └── HvVm.cs                # 虚拟机管理
│   │   ├── Jit/                        # JIT 编译器支持
│   │   │   ├── JitCpuContext.cs
│   │   │   ├── JitEngine.cs
│   │   │   ├── JitExecutionContext.cs
│   │   │   ├── MemoryManager.cs
│   │   │   ├── MemoryManagerHostMapped.cs   # 主机映射内存
│   │   │   ├── MemoryManagerHostTracked.cs  # 主机追踪内存
│   │   │   └── HostTracked/                 # 主机追踪实现
│   │   ├── LightningJit/              # LightningJIT 编译器
│   │   │   ├── Arm32/                  # ARM32 编译支持
│   │   │   │   ├── Target/Arm64/       # ARM32→ARM64 翻译
│   │   │   │   ├── Decoder.cs
│   │   │   │   └── InstDecoders.cs
│   │   │   └── AarchCompiler.cs        # ARM64 编译器
│   │   ├── Signal/                     # 信号处理
│   │   ├── ICpuContext.cs              # CPU 上下文接口
│   │   ├── ICpuEngine.cs               # CPU 引擎接口
│   │   ├── IExecutionContext.cs        # 执行上下文接口
│   │   └── ...
│   │
│   ├── Ryujinx.Memory/                 # ★ 内存管理子系统
│   │   ├── MemoryBlock.cs              # 内存块管理
│   │   ├── MemoryManager.cs            # 虚拟内存管理器
│   │   ├── Tracking/                   # 内存追踪
│   │   ├── WindowsShared/              # Windows 共享内存
│   │   └── ...
│   │
│   ├── Ryujinx.HLE/                    # ★ 高层模拟 (Switch OS 服务)
│   │   ├── HOS/                        # Horizon OS 模拟
│   │   │   ├── Applets/                # 系统小程序
│   │   │   │   ├── Browser/            # Web 浏览器 Applet
│   │   │   │   ├── Controller/         # 手柄选择 Applet
│   │   │   │   ├── Error/              # 错误显示 Applet
│   │   │   │   ├── PlayerSelect/       # 玩家选择 Applet
│   │   │   │   └── SoftwareKeyboard/   # 软键盘 Applet
│   │   │   ├── Kernel/                 # 内核模拟
│   │   │   ├── Services/               # 系统服务实现
│   │   │   │   ├── Account/            # 账户服务
│   │   │   │   ├── Am/                 # Applet 管理服务
│   │   │   │   ├── Apm/                # 性能管理服务
│   │   │   │   ├── Audio/              # 音频服务
│   │   │   │   ├── Bsd/                # 套接字服务
│   │   │   │   ├── Fs/                 # 文件系统服务
│   │   │   │   ├── Hid/                # 人机交互服务
│   │   │   │   ├── Nv/                 # NVIDIA GPU 服务
│   │   │   │   ├── Nvnflinger/         # 显示合成服务
│   │   │   │   ├── Pctl/               # 家长控制服务
│   │   │   │   ├── Prepo/              # 错误报告服务
│   │   │   │   ├── SurfaceFlinger/     # Surface 管理
│   │   │   │   ├── Time/               # 时间服务
│   │   │   │   └── Vi/                 # 显示服务
│   │   │   ├── Applets/                # 系统 Applet
│   │   │   └── ...
│   │   ├── FileSystem/                 # 虚拟文件系统
│   │   │   ├── ContentManager.cs       # 内容管理器 (NCA/NSP/XCI)
│   │   │   └── VirtualFileSystem.cs    # 虚拟文件系统
│   │   ├── HLEConfiguration.cs         # HLE 全局配置
│   │   └── Exceptions/                 # 异常定义
│   │
│   ├── Ryujinx.Horizon/                # ★ Horizon OS 服务层
│   │   ├── Sdk/                        # Horizon SDK 接口
│   │   │   ├── Account/                # 账户 API
│   │   │   ├── Applet/                 # Applet API
│   │   │   ├── Arp/                    # 应用资源 API
│   │   │   ├── Audio/                  # 音频 API
│   │   │   ├── Fs/                     # 文件系统 API
│   │   │   ├── Sm/                     # 服务管理 API
│   │   │   └── ...
│   │   ├── Arp/                        # 应用资源管理
│   │   ├── Audio/                      # 音频服务
│   │   ├── Bcat/                       # 后台内容服务
│   │   ├── Friends/                    # 好友服务
│   │   ├── Hshl/                       # 手柄 shell 服务
│   │   ├── Ins/                        # 输入服务
│   │   ├── Lbl/                        # 背光服务
│   │   ├── LogManager/                 # 日志管理
│   │   ├── MmNv/                       # 多媒体内存服务
│   │   ├── Ngc/                        # 网络游戏通信
│   │   ├── Ovln/                       # 覆盖层服务
│   │   ├── Prepo/                      # 错误报告
│   │   ├── Psc/                        # 电源状态控制
│   │   ├── Ptm/                        # 温度监控
│   │   └── ...
│   │
│   ├── Ryujinx.Graphics/               # ★ 图形子系统
│   │   ├── Ryujinx.Graphics.GAL/       # 图形抽象层 (68 文件)
│   │   │   └── 定义 IRenderer, ITexture, IBuffer 等接口
│   │   │
│   │   ├── Ryujinx.Graphics.Gpu/       # GPU 模拟引擎
│   │   │   ├── Engine/                 # GPU 引擎
│   │   │   │   ├── Compute/            # 计算引擎
│   │   │   │   ├── Dma/                # DMA 引擎
│   │   │   │   ├── GPFifo/             # GPU 命令队列
│   │   │   │   ├── InlineToMemory/     # 内联写入
│   │   │   │   ├── MME/                # 宏引擎 (Macro)
│   │   │   │   ├── Threed/             # 3D 引擎
│   │   │   │   │   ├── Blender/        # 高级混合
│   │   │   │   │   ├── ComputeDraw/    # 计算绘制
│   │   │   │   │   ├── StateUpdater.cs # 状态更新器 (~1600行)
│   │   │   │   │   └── ...
│   │   │   │   └── Twod/               # 2D 引擎
│   │   │   ├── Image/                  # 纹理管理
│   │   │   │   ├── Texture.cs          # 纹理对象
│   │   │   │   ├── TextureCache.cs     # 纹理缓存
│   │   │   │   ├── TextureGroup.cs     # 纹理组
│   │   │   │   ├── TexturePool.cs      # 纹理池
│   │   │   │   └── Sampler.cs          # 采样器
│   │   │   ├── Memory/                 # GPU 内存
│   │   │   │   ├── Buffer.cs           # 缓冲区
│   │   │   │   ├── BufferCache.cs      # 缓冲缓存
│   │   │   │   ├── MemoryManager.cs    # GPU 内存管理
│   │   │   │   └── PhysicalMemory.cs   # 物理内存
│   │   │   ├── GpuContext.cs           # GPU 上下文
│   │   │   └── GpuChannel.cs           # GPU 通道
│   │   │
│   │   ├── Ryujinx.Graphics.Vulkan/    # Vulkan 渲染后端 (91 文件)
│   │   │   ├── VulkanRenderer.cs       # Vulkan 渲染器
│   │   │   ├── Pipeline/               # 管线管理
│   │   │   ├── Shaders/                # SPIR-V 着色器
│   │   │   ├── Effects/                # 后处理效果 (FSR/FXAA/SMAA)
│   │   │   ├── Window/                 # 窗口/交换链
│   │   │   └── ...                     # (通过 MoltenVK 在 Metal 上运行)
│   │   │
│   │   ├── Ryujinx.Graphics.OpenGL/    # OpenGL 渲染后端 (21 文件)
│   │   ├── Ryujinx.Graphics.Shader/    # 着色器翻译 (25 文件)
│   │   ├── Ryujinx.Graphics.Texture/   # 纹理编解码 (16 文件)
│   │   ├── Ryujinx.Graphics.Device/    # GPU 设备管理
│   │   ├── Ryujinx.Graphics.Host1x/    # Tegra Host1x 同步
│   │   ├── Ryujinx.Graphics.Nvdec/     # NVIDIA 视频解码器
│   │   ├── Ryujinx.Graphics.Nvdec.FFmpeg/ # FFmpeg 解码后端
│   │   ├── Ryujinx.Graphics.Nvdec.Vp9/ # VP9 解码器
│   │   ├── Ryujinx.Graphics.Vic/       # 视频图像合成器
│   │   └── Ryujinx.Graphics.Video/     # 视频解码抽象
│   │
│   ├── Ryujinx.Audio/                  # ★ 音频子系统
│   │   ├── Backends/                   # 音频后端接口
│   │   ├── Common/                     # 通用音频工具
│   │   ├── Input/                      # 音频输入
│   │   ├── Integration/                # 音频集成层
│   │   ├── Output/                     # 音频输出
│   │   ├── Renderer/                   # 音频渲染器
│   │   │   ├── Common/                 # 通用渲染逻辑
│   │   │   └── Server/                 # 音频服务器
│   │   └── AudioManager.cs             # 音频管理器
│   │
│   ├── Ryujinx.Audio.Backends.Apple/   # Apple 平台音频后端 (AudioUnit)
│   ├── Ryujinx.Audio.Backends.OpenAL/  # OpenAL 音频后端
│   ├── Ryujinx.Audio.Backends.SDL2/    # SDL2 音频后端
│   ├── Ryujinx.Audio.Backends.SoundIo/ # libsoundio 音频后端
│   │
│   ├── Ryujinx.Input/                  # ★ 输入抽象层
│   │   ├── IGamepadDriver.cs           # 手柄驱动接口
│   │   ├── IInputDevice.cs             # 输入设备接口
│   │   └── ...
│   ├── Ryujinx.Input.SDL2/             # SDL2 输入实现
│   ├── Ryujinx.SDL2.Common/            # SDL2 通用工具
│   │
│   ├── Ryujinx/                        # ★ 核心启动/配置项目
│   │   ├── Program.cs                  # 程序入口
│   │   ├── AppHost.cs                  # 应用宿主
│   │   ├── App.axaml / App.axaml.cs    # Avalonia UI 应用
│   │   ├── Common/                     # 通用工具
│   │   ├── Input/                      # 输入处理
│   │   ├── Modules/Updater/            # 更新模块
│   │   └── UI/                         # 桌面 UI
│   │
│   ├── Ryujinx.Common/                 # ★ 通用工具库
│   │   ├── Configuration/              # 配置系统 (17 文件)
│   │   ├── Collections/                # 自定义集合
│   │   ├── Extensions/                 # 扩展方法
│   │   ├── GraphicsDriver/             # 图形驱动检测
│   │   ├── Logging/                    # 日志系统
│   │   ├── Memory/                     # 内存工具
│   │   ├── Pools/                      # 对象池
│   │   ├── PreciseSleep/               # 精确睡眠
│   │   ├── SystemInterop/              # 系统互操作
│   │   └── Utilities/                  # 实用工具 (15 文件)
│   │
│   ├── Ryujinx.UI.Common/              # UI 通用代码
│   │   ├── Configuration/              # UI 配置模型
│   │   └── ...
│   │
│   ├── Ryujinx.Ava/                    # Avalonia UI 桌面前端
│   ├── Ryujinx.Gtk3/                   # GTK3 桌面前端 (Linux)
│   ├── Ryujinx.Headless.SDL2/          # SDL2 无头模式
│   ├── Ryujinx.ShaderTools/            # 着色器编译工具
│   │
│   ├── Spv.Generator/                  # SPIR-V 字节码生成器
│   │
│   ├── Ryujinx.Horizon.Common/         # Horizon 通用类型
│   ├── Ryujinx.Horizon.Generators/     # Horizon 代码生成器
│   ├── Ryujinx.Horizon.Kernel.Generators/ # Horizon 内核代码生成器
│   ├── Ryujinx.HLE.Generators/         # HLE 代码生成器
│   ├── Ryujinx.UI.LocaleGenerator/     # 本地化代码生成器
│   │
│   ├── Ryujinx.Tests/                  # 测试项目
│   ├── Ryujinx.Tests.Memory/           # 内存测试
│   ├── Ryujinx.Tests.Unicorn/          # Unicorn 引擎测试
│   │
│   ├── Ryujinx.sln                     # Visual Studio 解决方案 (根目录)
│   └── src.sln                         # Visual Studio 解决方案 (src 目录)
│
├── MeloNX-hv.entitlements              # iOS 权限配置 (内存/超虚拟化/IOKit)
├── Ryujinx.sln                         # 主解决方案文件
├── Ryujinx.sln.DotSettings             # ReSharper 设置
├── Directory.Packages.props            # NuGet 中央包管理
├── global.json                         # .NET SDK 版本 (8.0.100)
├── nuget.config                        # NuGet 源配置
│
├── .editorconfig                       # 代码风格配置
├── .gitattributes                      # Git 属性
├── .gitignore                          # Git 忽略规则
├── .swift-version                      # Swift 版本
│
├── crowdin.yml                         # Crowdin 翻译配置
├── source.json                         # 源码信息
├── default.profraw                     # 性能分析数据
│
├── CONTRIBUTING.md                     # 贡献指南
├── Compile.md                          # 编译说明
├── LICENSE.txt                         # GPLv3 许可证
└── README.md                           # 项目说明
```

---

## 核心架构分层

### 第一层：iOS 应用层 (Swift)
| 模块 | 职责 |
|------|------|
| UI Layer (SwiftUI/UIKit) | 主界面、设置、游戏库、模拟界面 |
| Controller Manager | MFi 手柄、虚拟手柄、远程手柄管理 |
| JIT Enabler | JitStreamerEB / StikJIT JIT 编译启用 |
| Entitlement Checker | 内存限制、扩展虚拟地址权限检查 |
| RyujinxBridge | Swift ↔ C 函数互操作层 |

### 第二层：模拟核心层 (C# .NET 8.0)
| 模块 | 职责 |
|------|------|
| **ARMeilleure** | ARMv8 指令 → 中间 IR → 原生代码翻译 |
| **Ryujinx.Cpu** | CPU 上下文管理，JIT/Apple Hypervisor 引擎 |
| **Ryujinx.Memory** | 虚拟内存管理，追踪式/映射式内存 |
| **Ryujinx.HLE** | Switch 系统服务高层模拟 (FS, HID, Audio, etc.) |
| **Ryujinx.Horizon** | Horizon OS 服务层实现 |

### 第三层：图形与多媒体层
| 模块 | 职责 |
|------|------|
| **Graphics.GAL** | 图形 API 抽象层 (IRenderer, ITexture, IBuffer) |
| **Graphics.Gpu** | Maxwell GPU 引擎模拟 (3D/2D/DMA/Compute) |
| **Graphics.Vulkan** | Vulkan 渲染后端 (通过 MoltenVK → Metal) |
| **Graphics.OpenGL** | OpenGL 渲染后端 (桌面平台) |
| **Graphics.Shader** | 着色器二进制翻译 |
| **Audio** | 音频渲染器 (支持 Apple/OpenAL/SDL2/SoundIo) |
| **Nvdec/FFmpeg/Vp9** | 硬件视频解码 |

### 第四层：平台抽象层
| 模块 | 职责 |
|------|------|
| **Common** | 配置、日志、内存工具、性能计数器 |
| **Input** | 输入设备抽象 (手柄/键盘/触摸) |
| **UI.Common** | 跨平台 UI 配置模型 |
| **SDL2.Common** | SDL2 库通用封装 |

---

## 关键技术栈

- **语言**: C# (.NET 8.0), Swift 5.x, Objective-C, C/C++
- **UI 框架**: SwiftUI, UIKit, Avalonia (桌面)
- **图形 API**: Vulkan (via MoltenVK → Metal), OpenGL
- **音频**: OpenAL, SDL2 Audio, libsoundio, Apple AudioUnit
- **CPU 虚拟化**: Apple Hypervisor (hv), JIT (LightningJIT)
- **视频解码**: FFmpeg, libvpx (VP9)
- **着色器**: SPIR-V 生成 (Spv.Generator)
- **文件系统**: LibHac
- **测试**: NUnit, Unicorn Engine

---

## 数据流概览

```
游戏 ROM (.xci/.nsp)
    │
    ▼
LibHac 文件系统解析
    │
    ▼
[HLE Loader] 加载 NPDM/NSO/NRO
    │
    ├──► [CPU: ARMeilleure] ARM → IR → 原生代码
    │         │
    │         ▼
    │    [Memory Manager] 虚拟地址 → 物理地址
    │
    ├──► [GPU Engine] 解析 Maxwell 命令流
    │         │
    │         ▼
    │    [GAL → Vulkan] 渲染命令 → MoltenVK → Metal
    │         │
    │         ▼
    │    [Metal Layer] → CAMetalLayer 显示
    │
    ├──► [Audio Renderer] 音频命令 → AudioUnit 输出
    │
    └──► [HID Services] 输入事件 → 游戏逻辑
              ▲
              │
    [Swift Controller Manager] MFi/虚拟/远程手柄
```

---

## iOS 特有机制

1. **JIT 要求**: iOS 禁止动态代码执行，MeloNX 通过 JitStreamer 或 StikJIT 启用
2. **Hypervisor**: 利用 Apple Hypervisor (`com.apple.private.hypervisor`) 加速 CPU 模拟
3. **内存限制**: 通过 `Increased Memory Limit` 和 `Extended Virtual Addressing` 权限突破限制
4. **MoltenVK**: Vulkan 通过 MoltenVK 转换为 Metal API 调用在 iOS 上运行
5. **Metal HUD**: 直接使用 CAMetalLayer 进行画面渲染显示
