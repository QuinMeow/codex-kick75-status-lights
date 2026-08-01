# M1–M3 Implementation and Acceptance Boundary

> Updated: 2026-08-01

M1–M3 的软件范围已经落地并通过自动化与浏览器验证。本文区分“代码路径完成”和
“物理设备/真实 Codex 会话已验收”：USB profile 已在当前设备与回退固件组合上通过正式
物理闸门；U1 仍为 diagnostic-only，真实 Codex lifecycle Hook 验收仍未完成。

## M1: Windows HID gate

- `Core` 提供严格的 64 字节 `0xEE`、只读 `0xA0`、`0xD5`、`0xD6` codec、checksum/XOR 与响应校验；
  每个 session 从 `0xA0` 解析活动 `currentMode`，D5/D6 使用同一 handle。公开 lighting transport
  只暴露成对的 `WriteSideLightFullStateAsync`；它连续写侧灯 block `9/8` 与 brightness 字节 `10/1`，
  不暴露单片 D6 或任意地址写入 API。
- `Hid.Windows` 通过 SetupAPI/HID API 枚举并要求 65 字节原生 report（Report ID 0 +
  64 字节协议帧）。当前只有 USB `19F5:1026` 可进入显式确认的 guarded 写路径；U1
  `19F5:2620` 与 `1027` 均为 diagnostic-only，`1020` 永久排除。
- `auto` 确定性优先 USB；首选身份存在但 busy、歧义或协议异常时不会静默降级到另一
  transport。
- 每次连接前先读取 owned marker；若存在，worker 会把 marker 的 USB/dongle profile 作为
  本次连接硬约束。即使两个设备同时存在，同一进程也不会通过另一 profile 尝试恢复。
- USB 守护式测试在第一次 D6 前持久化 baseline ownership journal（包含 `currentMode`），目标与恢复均在
  一次 A0 模式校验后连续执行 `9/8 → 10/1`，两个 D6 之间不插入其他包。目标连接在恢复前关闭；
  超时、取消或坏响应会毒化 client，恢复通过同一
  descriptor 的新连接与新 session 执行。两个恢复 ACK 和两次 D5 逐字节读回全部通过后才
  release journal。
- 常驻 `HidLightingWorker` 的 Windows transport 使用同一完整 D6 pair，确保状态更新同时刷新
  `10/1` brightness 镜像；该 USB 写入/恢复序列已通过受监督物理闸门，真实 Codex lifecycle
  状态驱动仍需单独端到端验收。
- 运行时所有物理命令经过同一协调门，包含健康探测、退避重连、异常启动恢复和安全关闭。
  `HidDeviceBusyException` 会被映射为可重试的 `DeviceBusy`，第一次退避为 2 秒；该生产链已有
  mock transport 到 SSE/API 的端到端覆盖，NuPhyIO 真机占用仍需受监督验收。

2026-08-01 的 USB 验收使用 `19F5:1026`、`MI_03`、Usage Page `0001`、Usage `0000`、
`in=65/out=65`，读取到 `currentMode=1` 和 baseline `02 28 01 00 00 44 E7 B3`。5 秒预检的
所有阶段均为 `true`、`Error=null`，用户确认绿色可见、恢复原色、主键灯与按键正常。第一批
20 × 5 秒全部通过协议验证，用户随后确认 20 次绿色与恢复、主键灯、按键、配对和 M1/M2 均无异常，
正式物理门槛由此满足。用户要求的第二批 20 × 5 秒也全部通过协议验证，最终 `isOwned=false`；
该批结束后没有独立人工观察记录，因此只作为额外协议证据。合计 40 个成功协议周期。

同日 30 秒扩展诊断曾出现绿色并恢复，且 `AllBaselinesRestored=true`，但目标阶段的附加检查失败；
原因未定，该次不计入正式周期。当前回退固件的精确版本未记录，修正后的实现未在 `v4.0.18`
上复测。U1 仍为 diagnostic-only，禁止写入。

## M2: state, Hook, and host

- `CodexHookNormalizer` 使用 64 KiB 上限和字段允许列表，只保留事件、session、turn、
  `tool_name` 与 Pre/PostToolUse 的 `tool_use_id`；prompt、tool payload、assistant message 和
  transcript 不进入 IPC 或日志。Pipe 边界再次执行严格 schema 校验，缺失 `kind`、重复或未知字段
  都在触发 reducer 前拒绝。
- reducer 按 `(session, turn)` 聚合，优先级为
  `RequiresInput > Thinking > Complete > Idle`，Stop 使用可配置 TTL，过期会清理。并行
  `request_user_input` 按 `tool_use_id` 精确关联；官方 PermissionRequest 当前没有对应 ID，因此明确
  保留每 turn 一个无关联等待 latch，不猜测 FIFO 或同名工具配对。
- `hook codex` 是 250 ms fail-open 的同步命令；它使用随机 loopback 端口、Host 实例 token
  和版本化 JSON。`status-response` 使用独立 allowlist DTO，Host 与 CLI 两侧都会重建并
  裁剪字符串字段；公开 identity 仅为 `VID:PID`，不包含 path、serial 或 baseline 确认 ID。
  Hook 的 20 样本分布测试分别强制在线 P95 `< 300 ms`、Host 离线 P95 `< 500 ms`。
- Host 在 Pipe 边界同步归约合法 Hook，后台只使用容量 1 的无数据 reconcile 通知；通知可以合并，
  但 `Stop`、`SessionEnd` 等生命周期状态不会因队列压力丢失。Start/Stop/admission 使用原子生命周期，
  停止后先拒绝新事件，已接受事件完成最终 reconcile；状态订阅者异常彼此隔离。
- 托盘 Host、HID worker、baseline/config store、预览/暂停/恢复、健康探测、分层退避和
  崩溃恢复均已实现。健康探测会比较当前侧灯与 desired state；发生外部漂移时只在现有 owned
  session 内重写一次并读回，不重新捕获 baseline，也不会无限重试。Hook 配置合并/卸载逻辑有
  自动化覆盖，但实际安装、Codex 信任确认和登录启动属于 M4，本轮没有修改用户配置。

真实 Codex prompt、审批或 `request_user_input`、Stop、SessionEnd 与并行 turn 仍需在
安装/信任后的会话中做端到端灯光验收。

## M3: loopback control page

- Kestrel 只绑定随机 IPv4 loopback 端口；写 API 要求每次 Host 启动生成的随机 token、自定义
  header、严格 Host/Origin 与 `Sec-Fetch-Site: same-origin`，禁用 CORS/preflight，并限制
  header/body/rate。
- 页面提供 status、settings、3 秒 preview、pause、restore、硬件测试与 SSE；设置包含三种
  状态颜色/亮度、Complete TTL 和登录启动偏好。登录启动偏好只持久化，M4 才写 HKCU Run。
- `wwwroot` 是单一资产来源，HTML/CSS/JS 以资源嵌入应用程序集，不存在手工压缩副本漂移。
- Host 将固定枚举的启停、Hook、状态与设备事件写入脱敏 JSONL：session ID 在入队前以
  进程内随机 HMAC 密钥散列，写路径使用有界非阻塞队列，后台按 UTC 日期/大小轮转并限制
  保留天数/文件数。schema 不存在 prompt、tool payload、assistant/transcript、HID path、serial
  或 token 字段。日志目录除受保护的 Windows ACL 外，还以不共享 DELETE 的目录 handle 固定；
  文件创建、读取和删除在同一 `OPEN_REPARSE_POINT` handle 上校验并拒绝 reparse/多硬链接。
  活跃 reader 会在 logger 释放前排空；删除失败或不可信匹配项占满文件数上限时，新事件
  fail-closed 并计入 dropped count；读取也会在同一已锁定 handle 上先拒绝超大文件。
- worker 在 fresh idle 以 5 秒限速执行 descriptor-only inventory；该路径不建立协议 session、
  不打开写连接，也不发送 HID report。USB 文案区分 `descriptor observed` 与
  `runtime session observed`；严格匹配的 U1/High descriptor 只显示 `DiagnosticOnly`。
  若启动时存在未释放 ownership，恢复、读回验证与释放优先于 inventory。页面/API 只公开
  裁剪后的 `VID:PID`、安全 manufacturer/product、interface fingerprint 和
  `HID descriptor bcdDevice 0x....`；该版本值明确标为 descriptor metadata，不宣称是 NuPhyIO
  固件版本。内部恢复所需的 HID path 或 serial 不进入 GET/SSE，新增元数据也不参与恢复身份匹配。
- SSE 在写 connected 帧前建立订阅，广播按单调 sequence 串行入队；每个订阅有 bounded channel，
  独立限制最多 4 条事件流，并对每次 write/flush 设置 deadline。心跳使用单一周期 timer，避免高频
  状态事件遗留未取消的 delay。确定性测试分别阻塞 write 和 flush，验证 deadline 后 abort、取消、
  订阅释放与 SSE slot 回收。
- 硬件测试按钮可直接运行 USB 的读取、绿灯与恢复流程，不要求矩阵审阅、复选框或额外确认字段。
  U1/dongle 选项在页面上禁用并标注 diagnostic-only，后端 No-Go 保持不变。

### Browser QA and fidelity ledger

设计参考：[`design/m3-control-page-concept.png`](design/m3-control-page-concept.png)。终版使用
本地纯内存 control plane，不创建 Pipe、不打开 HID，并在应用内浏览器中验证：桌面
`1280×720` 为设置/诊断双栏；移动端 `390×844` 为单栏。两者均满足
`scrollWidth == clientWidth`，控制台没有 warning/error。

| Reference decision | Implemented result |
| --- | --- |
| 深石墨背景与蓝色主强调色 | 保留，并为输入请求/完成分别使用琥珀色与绿色 |
| 顶部聚合状态和五段侧灯预览 | 保留；状态、活动会话、Hook 与事件时间来自 Host |
| 左侧灯光设置、右侧设备诊断 | 桌面双栏保留；移动端按操作顺序折叠为单栏 |
| 底部独立硬件测试区 | 保留；明确点击按钮即代表运行 USB 读取、绿灯与恢复测试 |
| 紧凑的本地工具视觉 | 保留；实际页面允许纵向滚动，以容纳可访问标签和诊断详情 |
| 概念中的中文与示例 PID/固件 | 实现使用英文，并只显示 Host 提供的真实值；不伪造固件或 `Verified` 状态 |

浏览器实际点击验证了设置保存、3 秒预览和暂停/恢复。baseline 身份不匹配面板又在桌面和移动尺寸
验证了“默认禁用 → 风险复选框解锁”门禁，
且只显示裁剪后的设备信息。页面可查看并清除当前页面内存中的 SSE 诊断条目；独立的
`Saved diagnostics` 入口通过 `GET /api/v1/diagnostics?limit=...` 加载持久脱敏日志，只返回固定
allowlist DTO，不返回 session hash、原始设备字段、异常或路径。桌面与移动验收均实际加载 2 条
记录，且无横向溢出或控制台 warning/error。当前不提供原始导出，以免扩大本地数据暴露面。

## Automated verification

最终使用 Release 配置完成：

- `dotnet restore AgentKick75.slnx`：通过；
- `dotnet build AgentKick75.slnx -c Release --no-restore`：0 warning、0 error；
- `dotnet test AgentKick75.slnx -c Release --no-restore`：391 passed、1 skipped、
  0 failed；跳过项是当前环境无法创建目录 symlink 的底层 ACL/reparse capability 测试；目录
  lease 的 rename/delete 阻断和文件 hardlink 边界由不依赖 symlink 权限的测试实际执行；
- `dotnet format AgentKick75.slnx --no-restore --verify-no-changes`：通过。

全量测试在普通当前用户权限下运行，以覆盖用户数据 ACL、Named Pipe 状态通道和真实 loopback
Hook 入口；Codex 沙箱通过 loopback 入口投递，不依赖其不可见的当前用户 Named Pipe。

## Remaining supervised acceptance

以下项目在 2026-08-01 按用户决定延期，不阻塞 M1–M3 软件实现目标收敛：

1. 安装并信任项目 Hook 后，用真实 Codex 会话验证 M2 的所有生命周期事件和并行 turn。
2. 在用户明确同意并监督时，让 NuPhyIO 占用 USB 接口，验证真实 `DeviceBusy`、2 秒退避、释放后
   收敛，以及最终 baseline 恢复；mock 生产链通过不能替代该物理证据。
3. M4 再完成安装/卸载、HKCU 登录启动、发布包和完整 Windows 异常/冲突 QA。
4. U1 只有在取得远端型号和独立协议证据后才能重新进入写入评审；当前不安排 dongle 写入闸门。

USB `Verified` 仅适用于本次设备与当前回退固件组合，不涵盖 `v4.0.18` 或 U1。在第 1 项完成前
不得声称真实 Codex 到灯光的端到端行为已验证。
