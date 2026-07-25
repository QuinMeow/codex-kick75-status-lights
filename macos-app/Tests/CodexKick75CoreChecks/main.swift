// SPDX-License-Identifier: MIT
import CodexKick75Core
import Foundation

private enum CheckFailure: Error, CustomStringConvertible {
    case failed(String)

    var description: String {
        switch self {
        case .failed(let message): message
        }
    }
}

private func expect(_ condition: @autoclosure () throws -> Bool, _ message: String) throws {
    guard try condition() else { throw CheckFailure.failed(message) }
}

private func expectError(_ message: String, operation: () throws -> Void) throws {
    do {
        try operation()
    } catch {
        return
    }
    throw CheckFailure.failed(message)
}

private func runChecks() throws {
    let defaults = AppSettings.defaults
    try expect(defaults.version == 1, "默认配置版本应为 1")
    try expect(
        defaults.states.running == LightSetting(color: "#FFB400", brightness: 100),
        "执行中默认灯光与后台服务不一致"
    )
    try expect(
        defaults.states.permission == LightSetting(color: "#FF0000", brightness: 100),
        "等待权限默认灯光与后台服务不一致"
    )
    try expect(
        defaults.states.failure == LightSetting(color: "#FF0000", brightness: 100),
        "工具失败默认灯光与后台服务不一致"
    )
    try expect(
        defaults.states.completed == LightSetting(color: "#00FF00", brightness: 100),
        "完成状态默认灯光与后台服务不一致"
    )

    let partialData = Data(
        """
        {"version":1,"states":{"running":{"color":"#123abc","brightness":42}}}
        """.utf8
    )
    let partial = try JSONDecoder().decode(AppSettings.self, from: partialData).validated()
    try expect(
        partial.states.running == LightSetting(color: "#123ABC", brightness: 42),
        "自定义颜色应规范化为大写"
    )
    try expect(
        partial.states.completed == defaults.states.completed,
        "缺少的状态应自动补充默认值"
    )

    try expectError("应拒绝非法颜色") {
        _ = try LightSetting(color: "red", brightness: 50).validated()
    }
    try expectError("应拒绝超出范围的亮度") {
        _ = try LightSetting(color: "#123456", brightness: 101).validated()
    }

    let directory = FileManager.default.temporaryDirectory
        .appendingPathComponent(UUID().uuidString, isDirectory: true)
    defer { try? FileManager.default.removeItem(at: directory) }
    let url = directory.appendingPathComponent("settings.json")
    let store = SettingsStore(url: url)
    var settings = defaults
    settings.states.running = LightSetting(color: "#123456", brightness: 42)
    try store.save(settings)
    try expect(try store.load() == settings, "配置文件写入后无法正确读取")
    let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
    try expect(attributes[.posixPermissions] as? Int == 0o600, "配置文件权限应为 0600")
}

do {
    try runChecks()
    print("Swift 核心自检通过（配置默认值、校验、补全与安全读写）")
} catch {
    fputs("Swift 核心自检失败：\(error)\n", stderr)
    exit(1)
}
