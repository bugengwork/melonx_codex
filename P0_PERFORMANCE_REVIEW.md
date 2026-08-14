# P0 生命周期方案性能对比

## 评审结论

本批改动解决的是“停止游戏后旧会话仍被线程、静态表和 GPU 对象持有”，收益主要发生在跨局常驻内存，而不是单帧渲染速度。保留条件如下：

- 运行热路径不增加锁、日志或周期性分配；
- 停止路径允许增加一次性 join、排空和资源回收；
- 能通过对象可回收、handle 数量对称或线程归零证明释放收益；
- 无法证明收益的缓存/JIT 生命周期调整不进入本批。

## 原方案与当前方案

| 范围 | 原方案 | 当前方案 | 收益 | 新增成本 | 决策依据 |
| --- | --- | --- | --- | --- | --- |
| Renderer 命令循环 | Stop 直接终止 backend loop，Dispose 期间产生的命令可能残留；异常可能导致等待方永久阻塞；命令结构体转接口产生约 24 B/命令的装箱 | Stop 后排空队列，异常唤醒等待方，Dispose 在 backend 所有者线程执行；泛型约束直接读取命令类型 | 避免 GPU 对象和线程跨局残留，并消除逐命令装箱 | 健康状态每条命令增加一次 `volatile` gate 读取；完整检查仅在停止/故障慢路径执行 | 保留；9000 条稳态命令的新增分配测试从约 216 KB 降至不超过 2 KB |
| Vulkan 初始化 | 分散创建和销毁，部分初始化失败时所有权不完整 | 按 instance/surface/device/swapchain 登记，失败逆序回滚 | 每个成功 handle 恰好释放一次 | 仅初始化/销毁时维护小型动作栈 | 保留；不进入帧循环，故障注入测试覆盖 |
| SurfaceFlinger | 只设置退出标志，不 join composer，也不清 binder/layer | 唤醒并 join composer，注销 binder，清空 layer | 消除旧 `Switch`、BufferQueue 和线程引用 | 每局退出一次 join；运行期仅将退出标志改为 `volatile` | 保留；成本只在 teardown |
| Time/Bluetooth | 进程级静态对象持有旧 KernelContext | 按会话所有者或统一 reset 清理 | 旧 KernelContext 可回收，避免第二局使用旧 handle | 初始化/低频 IPC 增加锁；帧循环无成本 | 保留；静态引用测试覆盖 |
| BSD/NvMap | 静态 registry/map 跨局累积 | 进程退出后清空并关闭 descriptor | socket、descriptor、NvMap handle 不再线性累积 | 每局退出遍历一次现存条目 | 保留；复杂度与需要释放的对象数量成正比 |
| 生命周期观测 | 无法区分系统、GPU、render join 是否完成 | 只在状态迁移和里程碑采样内存、线程和 handle | 可验证释放顺序和跨局增长 | 每局十余次日志/采样，不在逐帧路径 | 保留；20 轮验收完成后可降低日志等级 |
| Swift 内存显示 | 每 200 ms 调用一次 `task_info` 并发布到主线程（5 Hz） | utility task 每秒采样一次（1 Hz） | 系统调用和主线程发布次数降低约 80% | 显示最多延迟约 1 秒 | 保留；内存标签不需要帧级刷新 |
| Swift 会话 UI 订阅 | 每次进入游戏都永久追加 AirPlay 闭包；性能 HUD 每次出现都永久注册电量通知闭包 | AirPlay 使用可注销 token 并在会话视图消失时移除；电量通知改用 SwiftUI `onReceive` 自动管理生命周期 | 跨局回调和闭包数量保持常数，历史会话不再响应电量或屏幕连接事件 | 连接/断开时一次字典增删；不在帧循环执行 | 保留；原实现有明确的只增不减容器，优化后有成对注册/注销路径和行为测试 |
| Swift 日志捕获 | 全进程日志永久追加到数组；UI 数组和 `AsyncStream` 也无界；每处理一行都重新创建正则 | 进程历史改为 4096 条/约 4 MiB 的 O(1) 环形缓冲；UI 和 stream 各只保留最新 4 条；正则进程级复用 | 将三处无界增长变为固定上限，并减少日志线程的正则编译和 UserDefaults 查询 | 写入时计算一次 UTF-8 长度；崩溃分析只保留最近日志 | 保留；历史用途是定位最近故障，不需要从进程启动起永久保留全部日志 |
| Swift 控制器通知 | `ContentView.onAppear` 每次调用 `initAll()` 都追加两枚 NotificationCenter block observer，token 永不释放 | singleton 仅安装一次连接/断开 observer，并保存 token；每次出现仍刷新控制器列表 | 防止页面重现后一次设备事件触发 N 次扫描和 native controller reload | 首次初始化一次锁和两枚 token；后续初始化一次短锁 | 保留；资源所有权本来就是进程级，幂等安装与现有架构一致 |
| Swift 控制器断开 | rumble helper 闭包强持有 `BaseController`，间接保留 haptic engine、motion manager 和 timer；cleanup 的弱 self 可能跳过 native detach；duration timer 在 deinit 未注销 | rumble 改弱捕获；cleanup 幂等且在所有者队列停止 motion/haptic、清 input handler并保证 detach；晚到 handler 使用弱引用；两个 timer 均注销 | 断开后释放控制器大对象，停止 60 Hz motion 与 haptic timer，避免释放后 input 回调 | 创建/断开时使用生命周期锁；input 事件增加一次弱引用读取，但删除逐事件日志后的净成本仍显著下降 | 保留；helper 缺少真正 unregister ABI，字典中的小型 key/弱闭包不能宣称已清零 |
| Swift 控制器按键热路径 | 每次按键变化都字符串插值并写 stdout，再经过 pipe、日志正则和缓冲 | 删除无功能用途的逐事件 `print`，直接调用 native input ABI | 消除每次按键事件的字符串分配、stdout I/O 和日志处理 | 无新增成本 | 保留；日志内容不参与控制器状态或诊断验收 |
| Swift 触摸/虚拟方向键热路径 | 触摸开始及方向键状态变化逐事件写 stdout；每次触摸移动都跨 ABI 重写未变化的 view size | 删除无功能日志；缓存最后一次上报的整数 view size，仅尺寸变化时调用 native | 消除输入事件的字符串/I/O 开销，并把拖动期间的 view-size ABI 调用从每个 move 降为尺寸变化次数 | 每次 move 增加两次整数比较；新 MTKView 首次触摸仍必定上报尺寸 | 保留；native 原逻辑只是覆盖静态 Width/Height，重复写入不改变行为 |
| 远程控制器网络功能 | client/host 会创建 60 Hz 输入发送、CoreMotion、Bonjour/Wi‑Fi Aware 浏览和监听对象 | 按产品要求整体删除远程手柄双端、入口、测试及网络权限 | 不再创建相关计时器、motion 和网络对象，同时移除本地网络弹窗与 Wi‑Fi Aware 签名要求 | 不再支持 iPhone 远程控制器 | 删除；本机实体手柄、屏幕虚拟按键、游戏联网与 LDN 不受影响 |
| Swift Stop | UI 提前释放 Metal layer，并用 Timer 猜测 native 完成 | `mainRyu` 返回作为完成屏障，资源保留到 native teardown 完成 | 避免 surface 使用已释放 layer，并阻止重入 | 状态迁移与一次 watchdog task | 保留；不增加渲染循环工作 |
| JIT mapping | 现有所有权和缓存策略 | 仅累计成功 mapping 数量 | 提供增长证据 | 每次成功创建 mapping 一次原子自增 | 仅观测；未修改 `_mmapPtr`、容量或释放策略 |

## 已有证据与未完成证据

- .NET 生命周期、renderer、Vulkan 回滚和静态资源测试覆盖释放顺序、幂等、fault 与引用清理。
- Renderer 稳态分配测试证明新路径不再产生原方案约 24 B/命令的装箱；测试阈值为 9000 条命令累计不超过 2 KB。
- 日志缓冲使用同一 Swift 优化构建做 50 万条、每条约 128 字节的本机对照：原无界数组峰值 RSS 为 119,193,600 B，当前 4096 条/4 MiB 环形缓冲为 6,848,512 B，下降约 94.3%。user CPU 为 0.52s → 0.51s；指令数约增加 1.3%，周期数约下降 3.8%，未观察到 CPU 回退。
- 日志清洗使用每批 200 行、共 500 批的合成输入，并以相同输出校验码确认语义一致：原实现逐行编译正则并双遍清洗耗时 2.67s、峰值 RSS 343,719,936 B；当前进程级正则加单遍清洗耗时 0.15s、峰值 RSS 10,436,608 B，墙钟下降约 94.4%，峰值 RSS 下降约 97.0%。
- 控制器按键热路径使用 10 万次相同状态更新做 Swift 优化构建对照，双方 stdout 均重定向到 `/dev/null`：原逐事件 `print` 路径耗时 0.03s、约 694,452,316 条指令和 113,417,858 cycles；删除日志后低于 0.01s、约 29,195,852 条指令和 9,832,800 cycles，指令量下降约 95.8%，cycles 下降约 91.3%。真实 App 还会省去 pipe、正则和日志缓冲开销。
- 触摸尺寸上报可由调用关系直接计数：同一尺寸下连续 N 次 `touchesMoved`，原路径执行 N 次 `set_view_size` ABI，当前路径只在首次或尺寸整数值变化时执行；native 实现仅覆盖静态 `Width/Height`，因此省略重复写入不改变结果。逐事件触摸和虚拟方向键 stdout 日志也已删除。
- HLE、Headless、Vulkan 和测试程序集可构建；26 项定向生命周期测试全部通过。
- iOS `build-for-testing` 无签名构建通过，本次生命周期改动引入的 actor 隔离告警已清零。
- Swift 测试目标增加 AirPlay 订阅注销、日志环形淘汰和绝对 cursor 行为覆盖；由于本机没有有效签名身份，目前只能证明测试目标成功编译，不能把该真机测试记为已执行。
- NativeAOT 动态库最低系统版本已从错误继承的 iOS 26.5 对齐到 App target 的 iOS 15.0，并通过 Mach-O `LC_BUILD_VERSION` 验证。
- 真正的内存净收益仍以实体设备 20 轮 `phys_footprint` 为最终证据；未完成该测试前，不把“内存增长已达标”作为已证明结论。
- 当前真机验收缺少可用签名身份、provisioning profile 和 ROM 输入；项目中另有既存 `RyujinxHelper` 最低 iOS 18.2 与 App target iOS 15.0 不一致的链接告警，均不在本批生命周期逻辑内掩盖处理。
- 完整 `Ryujinx.Tests` 曾尝试执行，但 macOS CPU 用例依赖的 `libarmeilleure-jitsupport` 是缺失/无效的 LFS 产物并产生大规模环境失败，另有既存音频结构体尺寸断言失败；该高噪声执行已停止，不把它计作本批回归结果。
- 真机验收若出现帧率回退或第 4～20 轮超过 2 MiB/轮，应优先定位观测和同步开销，不以释放正确性为理由接受性能回退。

## 审计后暂不修改

- FPS 标签仍保持 10 Hz：降低频率虽能减少少量 ABI 与主线程发布，但会改变可见刷新延迟，当前没有真机能耗数据证明净收益。
- motion 的 60 Hz 主队列回调仍保持原样：迁移到后台队列可能改善主线程占用，也可能改变输入时序；缺少真机延迟和丢帧对照前不进入 P0。
