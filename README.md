# AgentKick75 — Codex Kick75 Status Lights for Windows

[![CI](https://github.com/QuinMeow/codex-kick75-status-lights/actions/workflows/ci.yml/badge.svg)](https://github.com/QuinMeow/codex-kick75-status-lights/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

AgentKick75 是面向 Windows 的 Codex 状态灯项目。目标是由当前用户的托盘进程接收
Codex lifecycle Hooks，把任务状态映射到 NuPhy Kick75 NuPhyIO 的五颗侧灯，并通过
loopback 本地页面提供设置。

> [!WARNING]
> 项目目前仅完成 M0 工程与测试基线，没有可用安装包或可供日常使用的托盘应用。
> M1 HID 枚举、握手、读写与恢复尚未实现；Windows 代码从未发送过灯光写入命令。
> 协议 fixtures 只用于测试，不应被当作真机写入工具。

## 当前进度

| 阶段 | 状态 | 交付内容 |
| --- | --- | --- |
| M0 | 已完成 | .NET 10 solution、Windows 项目边界、CI、固定来源的协议 fixtures |
| M1 | 未实现 | HID 枚举、协议 codec、握手、读取、绿色试灯与原样恢复 |
| M2 | 未开始 | Codex Hooks、状态聚合、托盘 Host、Named Pipe |
| M3 | 未开始 | loopback 本地控制页与设置 |
| M4 | 未开始 | 安装、卸载、发布与 Windows QA |

M0 测试只验证 fixture 来源、64 字节长度、checksum、命令和地址允许列表，
不代表 Windows 协议栈或硬件支持已经完成。

## 目标状态语义

| Codex 状态 | 目标侧灯行为 |
| --- | --- |
| Idle | 恢复接管前保存的原始 8 字节侧灯状态 |
| Thinking | 蓝色呼吸 |
| Requires input | 琥珀色闪烁或呼吸 |
| Complete | 绿色保持 10 秒后恢复 |
| Error | 预留；没有可靠事件前不启用 |

多会话目标优先级为
`RequiresInput > Thinking > Complete > Idle`。这些是 MVP 目标，不是 M0 已交付功能。

## 硬件支持状态

| 连接路径 | VID:PID | 当前状态 | 恢复验证 |
| --- | --- | --- | --- |
| Kick75 NuPhyIO USB | `19F5:1026` | Enumerated / Unverified | 0/20 |
| Kick75 + U1 2.4G | `19F5:2620` | Enumerated / Unverified | 0/20 |
| Kick75 High identity | `19F5:1027` | 只读诊断；禁止写入 | 0/20 |
| U1 boot/upgrader | `19F5:1020` | 永久排除 | 不适用 |
| QMK/VIA、Bluetooth | — | 不支持 | 不适用 |

单一路径只有连续完成 20 次“读取 → 写入 → 逐字节恢复”后，才能标记为
`Verified`。USB 与 U1 必须独立验证，不能互相继承结果。

## 计划架构

~~~mermaid
flowchart LR
    C["Codex lifecycle Hooks"] --> H["AgentKick75.exe hook"]
    H --> P["当前用户 Named Pipe"]
    P --> T["Windows 托盘 Host"]
    T --> S["状态聚合与灯效调度"]
    S --> W["单线程 HID Worker"]
    W --> K["Kick75 USB 或 U1"]
    T --> L["Loopback 本地设置页"]
~~~

托盘进程将是唯一 HID 所有者。Hook 进程只传递事件名和必要标识，不传递或记录
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

当前 `AgentKick75.App` 仅初始化 WinForms 后退出。README 暂不提供运行或发布命令，
避免把 M0 骨架误认为可用产品；也不存在可安全运行的 `hardware-test` 命令。

## 项目结构

~~~text
.
├── src/windows/
│   ├── AgentKick75.Core/          # 状态、协议、配置的跨平台核心
│   ├── AgentKick75.Hid.Windows/   # Windows HID/SetupAPI 边界
│   └── AgentKick75.App/           # 托盘 Host、Hook 与本地页面入口
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
- 仅允许 `0xEE`、`0xD5`、`0xD6`，且写入只限侧灯地址 `9`、长度 `8`。
- 永不向 bootloader、QMK/VIA、Bluetooth 或未验证 PID 发送灯光命令。
- 首次写入前必须读取并持久化真实 baseline；退出、异常和空闲时逐字节恢复。
- 灯效模式 `0x04` 存在持久关闭风险，禁止写入。
- 未通过每条 transport 的 20 次恢复闸门前，不宣称硬件支持。

## 文档

- [Windows MVP 实现计划](docs/WINDOWS_CODEX_MVP_PLAN.md)
- [M0 工程与测试基线](docs/M0_BASELINE.md)
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
