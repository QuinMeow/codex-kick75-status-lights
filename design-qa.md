# AgentKick75 Agent Matrix 栅格图标 Design QA

**Source visual truth**

- 用户选择并要求直接使用的 Agent Matrix 概念：`docs/design/m4-app-icon-agent-matrix-concept.png`。
- 概念图尺寸：1254 x 1254 px；上半部分为浅色背景黑线版，下半部分为深色背景白线版。
- 硬约束：不再人工复刻结构，只允许裁切、背景移除、居中和多尺寸缩放。

**Implementation evidence**

- 黑线 App 透明母版：`src/windows/AgentKick75.App/Assets/AgentKick75.png`。
- 白线托盘透明母版：`src/windows/AgentKick75.App/Assets/AgentKick75Tray.png`。
- App ICO：`src/windows/AgentKick75.App/Assets/AgentKick75.ico`。
- 托盘 ICO：`src/windows/AgentKick75.App/Assets/AgentKick75Tray.ico`。
- 原图与提取结果同画面对比：`docs/design/m4-app-icon-readability-comparison.png`。
- 16 / 20 / 24 / 32 px 黑白双版本矩阵：`docs/design/m4-app-icon-small-size-matrix.png`。
- 24 px 中性背景矩阵：`docs/design/m4-app-icon-theme-matrix.png`。
- 状态：静态应用/托盘图标，无交互状态。
- 输出密度：512 x 512 RGBA 透明母版；ICO 帧为 16、20、24、32、40、48、64、128、256 px，均按 1x 目标像素保存。

**Required fidelity surfaces**

- 字体与排版：图标没有文字，不适用。
- 间距与布局：从原概念各自检测图标边界，保留 24 px 源图安全边距，再按原始宽高比置于 512 px 正方形画布；未重新排列外框、四模块或侧灯。
- 颜色与视觉 token：黑线 App 版本只含黑色与透明；白线托盘版本只含白色与透明。预览只使用白、近黑和中性灰背景，没有任务栏绿色。
- 图像质量与资产一致性：形状直接来自选定的 ImageGen 栅格；去背景使用亮度 alpha，16–32 px 只增强缩放后的 alpha 范围，没有重画路径或更改几何。
- 文案：图标没有文案，不适用。

**Full-view comparison**

- `m4-app-icon-readability-comparison.png` 左侧为选定概念，右侧为实际透明母版在相同浅色/深色画布上的渲染。外框比例、四个互锁模块、右侧灯条、线宽和负空间均来自原图；差异仅为背景移除和统一居中缩放。

**Focused region comparison**

- `m4-app-icon-small-size-matrix.png` 上排为黑线 App 图标，下排为白线托盘图标；显示 16–32 px 的实际帧。
- `m4-app-icon-theme-matrix.png` 分别使用黑线浅色版、白线深色版和白线中性灰版；背景仅用于 QA，不写入透明资产。

**Comparison history**

1. 人工 SVG 复刻比概念更拥挤，护线和模块比例持续偏离（P1）。修复：停止使用 SVG，改为直接提取选定概念图。
2. 单一白线资产在浅色界面不可见（P1）。修复：从概念图上半部分提取黑线 App/资源管理器版本，下半部分提取白线托盘版本；托盘通过嵌入资源加载白线 ICO。
3. 16–32 px 直接缩放后最大 alpha 降低，线条发灰（P2）。修复：逐尺寸拉伸 alpha 动态范围，不改变几何或颜色。
4. 最终轮：未发现仍需修改的 P0、P1 或 P2 问题。

**Findings**

- 没有未解决的 P0、P1 或 P2 视觉问题。
- P3 验证缺口：尚未重启 AgentKick75 并刷新 Windows Shell 图标缓存，当前证据是资产与构建级验证。

**Implementation checklist**

- [x] 直接使用选定概念图，不再人工重画。
- [x] 生成透明黑线 App 母版与白线托盘母版。
- [x] 生成两套九档 ICO。
- [x] 将白线托盘 ICO 嵌入 App 并由 `NotifyIcon` 加载。
- [x] 删除不再使用的 SVG。
- [x] 所有预览均不包含任务栏绿色。

final result: passed
