// SPDX-License-Identifier: MIT
import AppKit
import CodexKick75Core
import SwiftUI

struct ContentView: View {
    @ObservedObject var model: SettingsViewModel

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            ScrollView {
                VStack(spacing: 12) {
                    StatusEditor(
                        title: "执行中",
                        subtitle: "Codex 正在处理任务",
                        systemImage: "bolt.fill",
                        light: $model.settings.states.running,
                        isWorking: model.isWorking,
                        preview: { model.preview(.running) }
                    )
                    StatusEditor(
                        title: "等待权限",
                        subtitle: "需要你确认权限请求",
                        systemImage: "hand.raised.fill",
                        light: $model.settings.states.permission,
                        isWorking: model.isWorking,
                        preview: { model.preview(.permission) }
                    )
                    StatusEditor(
                        title: "工具失败",
                        subtitle: "工具返回了明确错误",
                        systemImage: "exclamationmark.triangle.fill",
                        light: $model.settings.states.failure,
                        isWorking: model.isWorking,
                        preview: { model.preview(.failure) }
                    )
                    StatusEditor(
                        title: "已完成",
                        subtitle: "所有跟踪任务均已结束",
                        systemImage: "checkmark.circle.fill",
                        light: $model.settings.states.completed,
                        isWorking: model.isWorking,
                        preview: { model.preview(.completed) }
                    )
                }
                .padding(16)
            }
            .frame(maxHeight: 520)
            Divider()
            controls
        }
        .frame(width: 390)
        .onAppear { model.startPolling() }
        .onDisappear { model.stopPolling() }
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Image(systemName: "keyboard")
                    .font(.system(size: 22, weight: .semibold))
                VStack(alignment: .leading, spacing: 2) {
                    Text("Codex Kick75")
                        .font(.headline)
                    Text("状态灯设置 · \(appVersion)")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                Spacer()
                Circle()
                    .fill(serviceIndicatorColor)
                    .frame(width: 9, height: 9)
            }
            HStack(spacing: 8) {
                Label(model.serviceSummary, systemImage: "waveform.path")
                Spacer()
                Text(model.hardwareSummary)
            }
            .font(.caption)
            .foregroundStyle(.secondary)
            if let detail = model.displayMessage {
                Text(detail)
                    .font(.caption)
                    .foregroundStyle(model.displayMessageIsError ? Color.red : Color.secondary)
                    .lineLimit(2)
            }
        }
        .padding(16)
    }

    private var controls: some View {
        VStack(spacing: 10) {
            HStack {
                Button("恢复默认") { model.restoreDefaults() }
                Button("重新读取") { model.reloadFromDisk() }
                Spacer()
                Button("保存并应用") { model.save() }
                    .buttonStyle(.borderedProminent)
                    .disabled(!model.canSave)
            }
            HStack {
                Button("打开配置目录") { model.openSettingsFolder() }
                    .buttonStyle(.link)
                Spacer()
                Button("退出") { NSApplication.shared.terminate(nil) }
                    .buttonStyle(.link)
            }
            .font(.caption)
        }
        .padding(14)
    }

    private var appVersion: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String
            ?? "0.2.0"
    }

    private var serviceIndicatorColor: Color {
        if model.snapshot != nil { return .green }
        if model.serviceError != nil { return .red }
        return .gray
    }
}

private struct StatusEditor: View {
    let title: String
    let subtitle: String
    let systemImage: String
    @Binding var light: LightSetting
    let isWorking: Bool
    let preview: () -> Void

    private var color: Binding<Color> {
        Binding(
            get: { Color(kick75Hex: light.color) },
            set: { selected in
                if let value = selected.kick75Hex { light.color = value }
            }
        )
    }

    private var brightness: Binding<Double> {
        Binding(
            get: { Double(light.brightness) },
            set: { light.brightness = Int($0.rounded()) }
        )
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Image(systemName: systemImage)
                    .foregroundStyle(color.wrappedValue)
                    .frame(width: 20)
                VStack(alignment: .leading, spacing: 1) {
                    Text(title).font(.subheadline.weight(.semibold))
                    Text(subtitle).font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                Button("预览", action: preview)
                    .controlSize(.small)
                    .disabled(!LightSetting.isValidColor(light.color) || isWorking)
            }
            HStack {
                ColorPicker("颜色", selection: color, supportsOpacity: false)
                TextField("#RRGGBB", text: $light.color)
                    .textFieldStyle(.roundedBorder)
                    .frame(width: 92)
                    .font(.system(.caption, design: .monospaced))
            }
            if !LightSetting.isValidColor(light.color) {
                Label("请输入 #RRGGBB 格式的颜色", systemImage: "exclamationmark.circle.fill")
                    .font(.caption2)
                    .foregroundStyle(.red)
            }
            HStack {
                Text("亮度")
                Slider(value: brightness, in: 0...100, step: 1)
                Text("\(light.brightness)%")
                    .monospacedDigit()
                    .frame(width: 38, alignment: .trailing)
            }
            .font(.caption)
        }
        .padding(12)
        .background(.quaternary.opacity(0.45), in: RoundedRectangle(cornerRadius: 10))
    }
}
