// SPDX-License-Identifier: MIT
import AppKit
import CodexKick75Core
import Foundation

@MainActor
final class SettingsViewModel: ObservableObject {
    @Published var settings: AppSettings
    @Published var snapshot: DaemonSnapshot?
    @Published var message: String?
    @Published var messageIsError = false
    @Published var serviceError: String?
    @Published var isWorking = false

    private let store: SettingsStore
    private let client: DaemonClient
    private var timer: Timer?

    init(store: SettingsStore = SettingsStore(), client: DaemonClient = DaemonClient()) {
        self.store = store
        self.client = client
        do {
            settings = try store.load()
        } catch {
            settings = .defaults
            message = error.localizedDescription
            messageIsError = true
        }
    }

    var menuIcon: String {
        serviceError == nil ? "keyboard.badge.ellipsis" : "keyboard.badge.exclamationmark"
    }

    var serviceSummary: String {
        guard let snapshot else { return serviceError == nil ? "正在检查后台服务" : "后台服务不可用" }
        let status = Self.statusName(snapshot.status)
        if snapshot.tasks == 0 { return "\(status) · 没有跟踪中的任务" }
        return "\(status) · \(snapshot.tasks) 个任务"
    }

    var hardwareSummary: String {
        guard let available = snapshot?.hardware.available else { return "键盘状态未知" }
        return available ? "Kick75 已连接" : "Kick75 不可用"
    }

    var settingsError: String? {
        do {
            _ = try settings.validated()
            return nil
        } catch {
            return error.localizedDescription
        }
    }

    var canSave: Bool {
        settingsError == nil && !isWorking
    }

    var displayMessage: String? {
        message ?? serviceError
    }

    var displayMessageIsError: Bool {
        message != nil ? messageIsError : serviceError != nil
    }

    func startPolling() {
        refresh()
        timer?.invalidate()
        timer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.refresh() }
        }
    }

    func stopPolling() {
        timer?.invalidate()
        timer = nil
    }

    func reloadFromDisk() {
        do {
            settings = try store.load()
            message = "已重新读取配置"
            messageIsError = false
        } catch {
            message = error.localizedDescription
            messageIsError = true
        }
    }

    func save() {
        isWorking = true
        do {
            try store.save(settings)
        } catch {
            isWorking = false
            message = error.localizedDescription
            messageIsError = true
            return
        }
        Task {
            defer { isWorking = false }
            do {
                snapshot = try await client.reload()
                serviceError = nil
                message = "配置已保存并生效"
                messageIsError = false
            } catch {
                serviceError = error.localizedDescription
                message = "配置已保存，后台服务将在下次启动时加载"
                messageIsError = false
            }
        }
    }

    func restoreDefaults() {
        settings = .defaults
        save()
    }

    func preview(_ status: StatusKind) {
        guard LightSetting.isValidColor(settings.states[status].color) else {
            message = "颜色无效，请使用 #RRGGBB 格式"
            messageIsError = true
            return
        }
        isWorking = true
        let light = settings.states[status]
        Task {
            defer { isWorking = false }
            do {
                snapshot = try await client.preview(status: status, light: light)
                serviceError = nil
                message = "正在预览\(Self.statusName(status.rawValue))，3 秒后自动恢复"
                messageIsError = false
            } catch {
                serviceError = error.localizedDescription
                message = error.localizedDescription
                messageIsError = true
            }
        }
    }

    func openSettingsFolder() {
        NSWorkspace.shared.open(SettingsStore.applicationDirectory)
    }

    private func refresh() {
        Task {
            do {
                snapshot = try await client.ping()
                serviceError = nil
            } catch {
                snapshot = nil
                serviceError = error.localizedDescription
            }
        }
    }

    private static func statusName(_ value: String) -> String {
        switch value {
        case "running": "执行中"
        case "permission": "等待权限"
        case "failure": "工具失败"
        case "completed": "已完成"
        case "idle": "空闲"
        default: value
        }
    }
}
