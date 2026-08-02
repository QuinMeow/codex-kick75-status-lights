# AgentKick75 — Codex Kick75 Status Lights for Windows

[![CI](https://github.com/QuinMeow/codex-kick75-status-lights/actions/workflows/ci.yml/badge.svg)](https://github.com/QuinMeow/codex-kick75-status-lights/actions/workflows/ci.yml)
[![Release: v0.1](https://img.shields.io/badge/release-v0.1-blue.svg)](https://github.com/QuinMeow/codex-kick75-status-lights/releases/tag/v0.1)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

AgentKick75 是面向 Windows 的 Codex 状态灯项目。目标是由当前用户的托盘进程接收
Codex lifecycle Hooks，把任务状态映射到 NuPhy Kick75 NuPhyIO 的五颗侧灯，并通过
loopback 本地页面提供设置。

> [!WARNING]
> M1–M3 的主体实现和自动化测试已经完成，USB profile 已在一台 Kick75 与当前回退固件组合上
> 通过正式真机闸门。回退固件的精确版本未记录，修正后的协议尚未在 `v4.0.18` 上复测，因此
> 此结论不能外推到所有固件。应用运行时固定使用 USB；U1 不在支持范围内，也不参与枚举或选择。M4 已能生成自包含 zip，
> 真实 Codex Desktop 的 Thinking → Complete 灯光链路已受监督通过；NuPhyIO `DeviceBusy`
> 物理验收已按用户决定延期。
> 但安装/卸载与完整 Windows 异常矩阵尚未现场验收。不要把可构建发布包当作已验证的日常使用产品。

## 下载与安装（v0.1 预览版）

从 [GitHub Release v0.1](https://github.com/QuinMeow/codex-kick75-status-lights/releases/tag/v0.1)
下载 `AgentKick75-win-x64.zip`。该包仅面向 Windows x64，已包含 .NET 运行时；当前版本未签名，
Windows 可能显示 SmartScreen 提示。

解压到准备长期保留的目录，然后在 PowerShell 中运行：

~~~powershell
.\AgentKick75.exe install
.\AgentKick75.exe
~~~

`install` 会安装 Codex 集成并注册当前用户登录启动项。安装后请完全退出并重新启动 Codex，
再通过 `/hooks` 检查并信任 Hook。删除程序目录前先执行：

~~~powershell
.\AgentKick75.exe uninstall
~~~

预览版只支持 Kick75 NuPhyIO USB `19F5:1026`；U1、Bluetooth 和 QMK/VIA 不在运行时支持范围内。

## 当前进度

| 阶段 | 状态 | 交付内容 |
| --- | --- | --- |
| M0 | 已完成 | .NET 10 solution、Windows 项目边界、CI、固定来源的协议 fixtures |
| M1 | USB profile 已通过 | USB HID 枚举/筛选、协议 codec、活动 `currentMode`、基线 journal、守护式读取/绿色试灯/读回恢复 |
| M2 | 已完成；Thinking/Complete 真机链路已通过 | Codex 事件裁剪、状态聚合、托盘 Host、独立 Hook helper、完成通知兼容、HID worker 与重连 |
| M3 | 已完成 | 受限 loopback 控制页、设置/预览/暂停/恢复、SSE、桌面与移动端浏览器 QA |
| M4 | 进行中 | 生命周期、显式恢复、self-contained zip、安装/卸载、登录启动与图标已实现；Windows 真机 QA 待完成 |

自动化测试覆盖协议、状态机、配置、Hook、IPC、HID mock、恢复和控制页；它们不等于真机验证。
USB 的 `Verified` 结论来自独立的 20 次物理恢复闸门，不外推到其他连接路径。

## 目标状态语义

| Codex 状态 | 目标侧灯行为 |
| --- | --- |
| Idle | 恢复接管前保存的原始 8 字节侧灯状态 |
| Thinking | 使用网页配置的颜色、静态/呼吸/流光、亮度和流光速度 |
| Requires input | 等待问题或权限确认；使用对应灯效设置 |
| Complete | 完成后按 TTL 恢复 |
| Interrupted | Goal blocked 时保持到下一条用户消息或 SessionEnd |
| Error | 预留；没有可靠事件前不启用 |

多会话目标优先级为
`RequiresInput > Interrupted > Thinking > Complete > Idle`。每个会话只保存一条当前状态，
`UserPromptSubmit` 和与等待工具同名的 `PostToolUse` 会清除该会话的等待状态，其他工具完成事件不会误清除等待；`SessionEnd` 直接删除会话。
Complete 记录只为灯光 TTL 暂存，不计入页面的活动会话数。该语义已在 reducer 和 mock 集成测试中实现；
真实 Codex Desktop 的 Thinking → Complete 页面与侧灯变化已于 2026-08-01 受监督确认。

## 硬件支持状态

| 连接路径 | VID:PID | 当前状态 | 恢复验证 |
| --- | --- | --- | --- |
| Kick75 NuPhyIO USB | `19F5:1026` | `Verified`：当前设备与回退固件组合 | 20/20 正式物理周期；另有 20/20 协议复验 |
| Kick75 + U1 2.4G | `19F5:2620` | 不支持；不参与运行时枚举 | 不适用 |
| Kick75 High identity | `19F5:1027` | 只读诊断；禁止写入 | 不适用 |
| U1 boot/upgrader | `19F5:1020` | 永久排除 | 不适用 |
| QMK/VIA、Bluetooth | — | 不支持 | 不适用 |

2026-08-01，USB 在 `currentMode=1` 下连续完成 20 次“读取活动模式 → 两阶段写入 → 新 session
同模式恢复 → 双读回”，用户确认每次绿色与恢复正常，主键灯、按键、配对和 M1/M2 均无异常；
随后第二批 20 次协议复验也全部通过，最终 `isOwned=false`。

## 计划架构

~~~mermaid
flowchart LR
    C["Codex lifecycle Hooks"] --> H["AgentKick75.Hook.exe"]
    N["agent-turn-complete notify"] --> H
    H --> P["随机 loopback 端口 + Host 实例 token"]
    P --> T["Windows 托盘 Host"]
    T --> S["状态聚合与灯效调度"]
    S --> W["单线程 HID Worker"]
    W --> U["Kick75 USB 灯控"]
    T --> L["Loopback 本地设置页"]
~~~

托盘进程是唯一 HID 所有者。Hook 进程只传递事件名和必要标识，不传递或记录
prompt、tool input/output、assistant message 或 transcript。

## 开发环境

- Windows 11 x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)；版本由
  [global.json](global.json) 固定
- VS Code + C# Dev Kit（仓库包含推荐扩展和任务）

克隆并验证：

~~~powershell
git clone https://github.com/QuinMeow/codex-kick75-status-lights.git
cd codex-kick75-status-lights

dotnet restore AgentKick75.slnx
dotnet format AgentKick75.slnx --no-restore --verify-no-changes
dotnet build AgentKick75.slnx -c Release --no-restore
dotnet test AgentKick75.slnx -c Release --no-build
~~~

开发构建可以启动单实例托盘 Host，也可以查看命令帮助：

~~~powershell
dotnet run --project src/windows/AgentKick75.App -- --help
dotnet run --project src/windows/AgentKick75.App -- status
~~~

生成 `win-x64` self-contained 单文件/zip：

~~~powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\eng\publish-win-x64.ps1
~~~

在发布目录中安装或卸载当前用户集成：

~~~powershell
.\AgentKick75.exe install
.\AgentKick75.exe uninstall
~~~

命令行硬件复验固定使用 USB，不提供 transport 选项：

~~~powershell
dotnet run --project src/windows/AgentKick75.App -- hardware-test
~~~

USB 正式 M1 验收已经在现场监督下通过。需要复验时，直接使用
`hardware-test`；可按需附加 `--cycles` 和 `--green-seconds`。网页不提供硬件测试或连接方式切换入口。
`install` 会安装 Codex 集成并注册当前用户登录启动；`uninstall` 会先通过在线 Host 显式恢复并
读回验证原灯效，等待 Host 退出后再只移除本项目写入的配置。Host 离线但存在
`lighting-restore.json` 时会拒绝卸载。当前 zip 未签名，完整 Windows QA 仍未完成。

## 项目结构

~~~text
.
├── src/windows/
│   ├── AgentKick75.Core/          # 状态、协议、配置的跨平台核心
│   ├── AgentKick75.Hid.Windows/   # Windows HID/SetupAPI 边界
│   ├── AgentKick75.App/           # 托盘 Host 与本地页面入口（WinExe）
│   └── AgentKick75.Hook/          # Codex 控制台 Hook helper（stdin/stdout）
├── tests/windows/
│   ├── AgentKick75.Core.Tests/
│   ├── AgentKick75.Protocol.Tests/
│   └── AgentKick75.Integration.Tests/
├── docs/
├── AgentKick75.slnx
└── Directory.Build.props
~~~

## 安全边界

- 仅允许计划中明确列出的 VID/PID、usage、report 长度和 transport profile。
- 只读允许 `0xA0` 获取活动 `currentMode`、`0xD5` 读取灯态；会话仅允许 `0xEE`，唯一写命令
  `0xD6` 只限侧灯 `9/8` 与 brightness 镜像 `10/1`。
- 永不向 bootloader、QMK/VIA、Bluetooth 或未验证 PID 发送灯光命令。
- 首次写入前必须读取并持久化真实 baseline；退出、异常和空闲时逐字节恢复。
- 灯效模式 `0x04` 存在持久关闭风险，禁止写入。
- 任一新设备、固件或 transport profile 未通过自己的恢复闸门前，不继承 USB 当前组合的
  `Verified` 结论。

## 文档

- [Windows MVP 实现计划](docs/WINDOWS_CODEX_MVP_PLAN.md)
- [M0 工程与测试基线](docs/M0_BASELINE.md)
- [M1–M3 实现与验收边界](docs/M1_M3_IMPLEMENTATION.md)
- [M4 安装、卸载与发布](docs/M4_IMPLEMENTATION.md)
- [Windows 硬件测试矩阵](docs/WINDOWS_TEST_MATRIX.md)
- [Kick75 侧灯协议记录](docs/PROTOCOL.md)
- [来源与第三方说明](NOTICE.md)

## 来源与许可证

本项目保留
[Pixelmoss/codex-kick75-status-lights](https://github.com/Pixelmoss/codex-kick75-status-lights)
`v0.2.0` 的完整 Git 历史，并参考
[alvis-HaoH/agent-kick75-status-lights](https://github.com/alvis-HaoH/agent-kick75-status-lights)
的安全研究。Windows-only 工作树删除了不再使用的 macOS/Python 运行代码，但其来源仍可从
固定提交和历史中审计。

项目使用 [MIT License](LICENSE)，与 OpenAI 或 NuPhy 没有隶属或官方合作关系。
