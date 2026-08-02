# M4：安装、卸载与发布

## 当前实现

2026-08-02 已完成 M4 的应用生命周期与实时状态切片：

- `eng/publish-win-x64.ps1` 发布 `win-x64` self-contained 单文件 App/Hook，并生成 zip；
- `AgentKick75.exe install` 幂等安装 Codex Hooks、完成通知转发与当前用户登录启动项；
- `HostCoordinator` 是 `Starting / Running / Paused / Stopping / Faulted / Stopped` 的唯一权威；
  Pause、Resume、Exit 与 Uninstall 经过同一串行 HID 路径，Stop 只创建一个 10 秒停止任务；
- 运行时 `lighting-restore.json` 只保存恢复必需的设备身份、接口指纹、`currentMode` 和原始 8 字节，
  第一次 D6 前原子创建，恢复后经 D5 逐字节确认并删除；
- Idle、暂停、退出和卸载都会显式恢复并关闭 HID；重新接管时重新读取原灯效；
- Codex 会话和聚合状态只在内存中从实时 Hook 派生，暂停期间继续更新，恢复时只应用最新状态；
- `AgentKick75.exe uninstall` 使用 `prepare-uninstall` Pipe 握手；只有 Host 恢复并验证成功、响应已发送
  且单实例锁释放后，才移除本项目 Hook、通知包装与 HKCU Run 值；
- Host 离线且存在恢复记录，或在线恢复失败时，卸载拒绝修改外部配置；
- 控制页和托盘的“登录时启动”偏好会同步写入或删除 HKCU Run；
- App EXE 与托盘使用同一套 Kick75 单色图标，矢量母版和多尺寸 ICO 位于
  `src/windows/AgentKick75.App/Assets/`。

发布命令：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\eng\publish-win-x64.ps1
```

发布目录中的安装与卸载命令：

```powershell
.\AgentKick75.exe install
.\AgentKick75.exe uninstall
```

## 当前证据边界

App 已完成相关 Mock 测试与 Release 构建验证，v0.1 zip 已生成 SHA-256 校验值。按本轮范围未执行
固件自动恢复验证、重复硬件循环或完整 M4 真机/Windows
异常矩阵；这些未完成项包括重复安装/卸载现场验收、USB 拔插、睡眠/唤醒、NuPhyIO 冲突、异常
退出与回滚。zip 也未签名，不是 MSI/MSIX。不要把“可发布、可构建”描述为完整 Windows QA 已通过。
