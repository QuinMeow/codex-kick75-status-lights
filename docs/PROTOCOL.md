# Kick75 IO side-light protocol notes

本文保存 Windows 实现使用的 NuPhy Kick75 IO HID 协议证据。它不是官方规范；字段名称来自固定上游
实现的行为验证和 NuPhyIO 公开客户端静态分析，固件更新后可能变化。当前官方 NuPhyIO
`main.f6f60294.js` 与灯光 chunk `686.189b2dd0.chunk.js` 显示：页面先以 `0xA0 GetBase` 读取活动
`currentMode`，D5/D6 的 byte 7 都使用 `currentMode XOR sessionKey`；完整侧灯状态在一次 A0
模式校验后，连续写地址 `9`、长度 `8` 和 brightness 地址 `10`、长度 `1`，两个 D6 之间不插入
A0、D5 或其他协议包。此前 Windows 路径固定写 mode/bank `0`，收到
ACK/readback 但没有可见变化；这只证明固件解析了命令，不能证明它修改了当前可见 bank。

## 已验证设备边界

```text
Product:   Kick75 IO
USB VID:   0x19f5
USB PID:   0x1026
Report:    64-byte input and output
Interface: Usage Page 0x01, Usage 0x00
```

Pixelmoss `v0.2.0` 曾在 macOS 上通过 IOKit 访问 report ID `0`。2026-08-01，当前 Windows
实现又在一台 Kick75 与用户当前回退固件组合上通过 20 次正式物理恢复闸门。回退固件的精确
版本未记录，修正后的实现尚未在 `v4.0.18` 上复测，因此验证范围不能外推到其他固件或 U1。

## 帧格式

协议帧固定为 64 字节。当前 Windows HID descriptor 的原生输入/输出缓冲区为 65 字节：首字节是
外层 Report ID `0`，后 64 字节才是下表中的协议帧。transport 只接受这一精确能力组合：

| 偏移 | 长度 | 含义 |
| --- | --- | --- |
| `0` | 1 | 方向标志：主机写 `0x55`，键盘响应 `0xaa` |
| `1` | 1 | 命令字 |
| `2` | 1 | 保留，当前写 `0x00` |
| `3` | 1 | 校验和 |
| `4` | 1 | 子命令/片段长度，与会话密钥 XOR；侧灯为 `0x08`，brightness 为 `0x01` |
| `5..6` | 2 | 小端地址，与会话密钥 XOR |
| `7` | 1 | 活动 `currentMode` 句柄，与会话密钥 XOR |
| `8..63` | 56 | 数据载荷 |

校验算法：

```text
checksum = sum(report[4..63]) & 0xff
```

## 临时会话密钥

灯光状态读写前发送命令 `0xee`。当前实现：

1. 生成 56 字节随机数据，写入偏移 `8..63`。
2. 选择报告偏移 `28` 的随机字节作为临时会话密钥；如果为零则使用 `0xaa`。
3. 写入校验和并发送。
4. 验证响应为完整 64 字节，方向为 `0xaa`、命令为 `0xee`，且 checksum 正确。
5. 要求响应 bytes `4..7` 均为本次 key，并验证响应 payload 的每个字节都等于请求 challenge
   与 key 的 XOR，拒绝只匹配 envelope 的伪握手。

后续报告的长度、地址、句柄和有效载荷均使用该单字节密钥 XOR。
这是一种协议级掩码，并非密码学安全加密。

`0xee` 的 challenge-response 是本次会话身份的一部分，不是固件可忽略的任意 payload。

## 灯光命令

| 命令 | 值 | 用途 |
| --- | --- | --- |
| `GetBase` | `0xa0` | 只读 8 字节基础状态；payload byte 0 是活动 `currentMode` (`0` 或 `1`) |
| `SetSecretKey` | `0xee` | 建立临时会话密钥 |
| `GetLightState` | `0xd5` | 读取 17 字节灯光状态 |
| `SetLightState` | `0xd6` | 写入指定灯光状态片段 |

会话建立后先发送只读 `A0 address=0,length=8,handle=0`，解析出活动 `currentMode`。随后读取灯光
状态时：

```text
length  = 17
address = 0
handle  = currentMode
```

解码后的 17 字节由两个区域组成：

```text
bytes 0..8   主键区状态（9 字节）
bytes 9..16  侧灯状态（8 字节）
```

写入严格限制为两个专用切片：

| 阶段 | address | length | 数据 |
| --- | ---: | ---: | --- |
| 侧灯完整状态 | `9` | `8` | 原始或目标 8 字节 |
| brightness 刷新 | `10` | `1` | 同一状态的相对偏移 `1` |

地址 `10` 位于侧灯区间 `9..16` 内，不触及主键区。实现不得提供任意 address/length 写入入口。
每个 `0xd6` ACK 都必须同时通过方向、命令、checksum、本阶段的精确 length/address 和本次
`currentMode` 句柄校验。迟到的旧 session ACK 或任一编码头字段不匹配都会被拒绝。当前 response fixture 是
来源推导向量，不是真机抓包，因此实现不把 ACK payload 回显当作已证实协议。超时、取消或坏响应会
毒化当前连接；恢复必须关闭它，并通过同一已选 USB descriptor 建立新连接和新 `0xee` session。

## 侧灯 8 字节状态

根据当前固件和客户端解析器：

| 相对偏移 | 含义 |
| --- | --- |
| `0` | 模式 |
| `1` | 亮度，范围 `0` 到 `100` |
| `2` | 速度编码 |
| `3` | `isRGB`/颜色来源：侧灯自定义 RGB 为 `0`，预设色板为 `1` |
| `4` | `colorIndex`；自定义 RGB 为 `0` |
| `5` | 红色分量 |
| `6` | 绿色分量 |
| `7` | 蓝色分量 |

正式 USB 闸门读取到的 baseline（`currentMode=1`）：

```text
02 28 01 00 00 44 e7 b3
```

静态颜色使用模式 `0x02`：

```text
红色: 02 64 01 00 00 ff 00 00
黄色: 02 64 01 00 00 ff b4 00
绿色: 02 64 01 00 00 00 ff 00
```

这些 `byte3=0,byte4=0` 向量与旧上游可见记录及当前 NuPhyIO 侧灯自定义色 serializer 一致。
受监督绿色候选固定为 `02 64 01 00 00 00 ff 00`：静态、亮度 100、速度 1、自定义 RGB、
color index 0、绿色。它同时符合固定旧上游的可见向量与当前 NuPhyIO 侧灯 serializer；该候选
已在上述 USB 设备/回退固件组合上产生肉眼可见的绿色并恢复原色；真实 Codex Desktop 的
Thinking → Complete 常驻 runtime 链路也已另行受监督通过。

亮度字节接受 `0` 到 `100`，最后三个字节接受任意 RGB 分量。Windows 实现必须从经过验证的配置
动态生成这 8 字节，不能把示例颜色当作固定写入值。

## 恢复策略

完整 D6 pair 同时用于 USB guarded hardware-test 与常驻 Windows transport；下列 baseline
ownership、人工观察与恢复事务只适用于用户显式启动的 guarded hardware-test：

1. 完成 `0xEE` 后用只读 `0xA0` 获取活动 `currentMode`，并把它持久化到 ownership journal。
2. 使用同一 `currentMode` 的 `GetLightState` 读取完整灯光状态，保存侧灯原始 8 字节。
3. 一次 A0 校验后按 `9/8 target → 10/1 target[1]` 连续写入上述 current-NuPhyIO candidate，
   中间不插入其他包；两次 ACK 后等待 `100 ms` settle，再执行 immediate D5。
4. immediate D5 不匹配仍完整保留人工观察窗口，并在窗口末再次 D5；协议 gate 仍判失败。
   immediate D5 若超时或响应非法，连接视为已毒化，但在未取消时仍尽量完成观察窗口，随后直接
   使用新连接恢复。调用方取消则立即进入恢复。
5. 恢复的新 session 必须重新读取 `currentMode`；若与 journal 不一致则 fail closed。匹配时按
   `9/8 baseline → 10/1 baseline[1]` 原样写回，再执行两次同 handle 的 `0xd5` 逐字节验证。
6. 任一恢复子写、任一稳定读回或 ownership release 失败时保留 journal，不宣称恢复成功。

官方页面在实际活动状态 brightness 从 `0` 提升到正值时，还会清除独立的
`gameOptimization` 状态。该状态不属于侧灯 8 字节，当前安全边界不自动修改它；人工验收前应确认
键盘已唤醒、`Fn+Tab` 全灯开关为开启且侧灯亮度非零。若未来要自动控制该全局状态，必须先读取、
持久化并恢复它，不能把它夹带进侧灯测试。

常驻 `HidLightingWorker` 通过 Windows transport 复用同一完整 pair，因此每次状态写入也会刷新
`10/1` brightness 镜像；该 USB pair 的物理写入、恢复以及真实 Codex Thinking → Complete
状态驱动均已经受监督验证。

这种方式可以恢复用户原本的模式、颜色、亮度和速度，而不假设其具体含义。未来本地页面的预览也只能
请求托盘 Host 执行；页面不得直接访问 HID。预览结束后必须恢复原 baseline 或重新应用当前任务状态。

## Windows USB 验证记录

2026-08-01 的受监督测试使用 `19F5:1026`、`MI_03`、Usage Page `0001`、Usage `0000` 和
65 字节原生 input/output report。A0 返回 `currentMode=1`，baseline 为
`02 28 01 00 00 44 e7 b3`。5 秒预检的所有阶段均为 `true`、`Error=null`，用户确认绿色可见、
恢复原色且主键灯与按键正常。随后第一批 20 × 5 秒正式周期全部通过，用户确认 20 次绿色与恢复、
主键灯、按键、配对和 M1/M2 均无异常；该批满足 USB 物理门槛。

用户要求的第二批 20 × 5 秒全部通过协议验证，最终 `isOwned=false`，但结束后没有单独的人工观察
记录，因此只作为补充协议证据。两批合计 40 个成功协议周期。另一次 30 秒扩展诊断中绿色可见、
原色恢复且 `AllBaselinesRestored=true`，但目标阶段附加检查失败，原因未定，不计入正式周期。

## 不使用的命令

Windows 实现不得发送：

- 固件升级命令。
- 恢复出厂命令。
- 键位映射命令。
- 主键区灯光写入。
- 未经行为验证的 HID 报告。

## Bluetooth 限制

蓝牙键盘输入可以正常使用，但当前发现的蓝牙接口不提供与 USB Raw HID 等价的 64 字节任意 RGB
写入通道，因此不进入灯控白名单。Windows MVP 只面向 USB `19F5:1026` 与 U1 2.4G 接收器
`19F5:2620`。当前官方网页只把 `0x1026` 纳入 Kick75 keyboard API；`0x2620` 是 U1 dongle
capability，不能证明可直接发送 Kick75 D6。因此 U1 当前为 diagnostic-only，所有写入均被拒绝。

## 证据来源

旧的静态分析脚本已从 Windows-only 工作树移除。其结果和原始协议实现仍可在
[Pixelmoss `v0.2.0`](https://github.com/Pixelmoss/codex-kick75-status-lights/tree/e32648ee86a8a729734060ac09bd7f8a1213876f)
及本仓库历史中审计；它们不是当前构建或运行时依赖。当前官方网页证据固定到
[NuPhyIO main bundle](https://drive.nuphy.io/static/js/main.f6f60294.js) 与
[灯光 chunk 686](https://drive.nuphy.io/static/js/686.189b2dd0.chunk.js)。Kick75 IO `v4.0.18`
元数据的更新说明为“更新了灯光的执行逻辑”，见
[官方固件 API](https://drive.nuphy.io/api/nuphyIo/getLastFirmwareVersionsByType?businessId=1930094363885735938&type=1)。
