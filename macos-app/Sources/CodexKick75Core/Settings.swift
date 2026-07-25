// SPDX-License-Identifier: MIT
import Foundation

public enum StatusKind: String, CaseIterable, Codable, Identifiable, Sendable {
    case running
    case permission
    case failure
    case completed

    public var id: String { rawValue }
}

public struct LightSetting: Codable, Equatable, Sendable {
    public var color: String
    public var brightness: Int

    public init(color: String, brightness: Int) {
        self.color = color
        self.brightness = brightness
    }

    public func validated() throws -> LightSetting {
        guard Self.isValidColor(color) else {
            throw SettingsError.invalidColor(color)
        }
        guard (0...100).contains(brightness) else {
            throw SettingsError.invalidBrightness(brightness)
        }
        return LightSetting(color: color.uppercased(), brightness: brightness)
    }

    public static func isValidColor(_ value: String) -> Bool {
        guard value.count == 7, value.first == "#" else { return false }
        return value.dropFirst().allSatisfy { $0.isHexDigit }
    }
}

public struct StateSettings: Codable, Equatable, Sendable {
    public var running: LightSetting
    public var permission: LightSetting
    public var failure: LightSetting
    public var completed: LightSetting

    public init(
        running: LightSetting,
        permission: LightSetting,
        failure: LightSetting,
        completed: LightSetting
    ) {
        self.running = running
        self.permission = permission
        self.failure = failure
        self.completed = completed
    }

    private enum CodingKeys: String, CodingKey {
        case running, permission, failure, completed
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        let defaults = AppSettings.defaults.states
        running = try container.decodeIfPresent(LightSetting.self, forKey: .running)
            ?? defaults.running
        permission = try container.decodeIfPresent(LightSetting.self, forKey: .permission)
            ?? defaults.permission
        failure = try container.decodeIfPresent(LightSetting.self, forKey: .failure)
            ?? defaults.failure
        completed = try container.decodeIfPresent(LightSetting.self, forKey: .completed)
            ?? defaults.completed
    }

    public subscript(status: StatusKind) -> LightSetting {
        get {
            switch status {
            case .running: running
            case .permission: permission
            case .failure: failure
            case .completed: completed
            }
        }
        set {
            switch status {
            case .running: running = newValue
            case .permission: permission = newValue
            case .failure: failure = newValue
            case .completed: completed = newValue
            }
        }
    }
}

public struct AppSettings: Codable, Equatable, Sendable {
    public var version: Int
    public var states: StateSettings

    public init(version: Int = 1, states: StateSettings) {
        self.version = version
        self.states = states
    }

    private enum CodingKeys: String, CodingKey {
        case version, states
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        version = try container.decodeIfPresent(Int.self, forKey: .version) ?? 1
        states = try container.decodeIfPresent(StateSettings.self, forKey: .states)
            ?? AppSettings.defaults.states
    }

    public static let defaults = AppSettings(
        states: StateSettings(
            running: LightSetting(color: "#FFB400", brightness: 100),
            permission: LightSetting(color: "#FF0000", brightness: 100),
            failure: LightSetting(color: "#FF0000", brightness: 100),
            completed: LightSetting(color: "#00FF00", brightness: 100)
        )
    )

    public func validated() throws -> AppSettings {
        guard version == 1 else { throw SettingsError.unsupportedVersion(version) }
        var normalized = self
        for status in StatusKind.allCases {
            normalized.states[status] = try states[status].validated()
        }
        return normalized
    }
}

public enum SettingsError: LocalizedError, Equatable {
    case unsupportedVersion(Int)
    case invalidColor(String)
    case invalidBrightness(Int)

    public var errorDescription: String? {
        switch self {
        case .unsupportedVersion(let version):
            return "不支持配置版本 \(version)"
        case .invalidColor(let color):
            return "颜色 \(color) 无效，请使用 #RRGGBB"
        case .invalidBrightness(let brightness):
            return "亮度 \(brightness) 无效，请使用 0 到 100"
        }
    }
}

public struct SettingsStore: Sendable {
    public let url: URL

    public init(url: URL = SettingsStore.defaultURL) {
        self.url = url
    }

    public static var applicationDirectory: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/CodexKick75", isDirectory: true)
    }

    public static var defaultURL: URL {
        applicationDirectory.appendingPathComponent("settings.json")
    }

    public func load() throws -> AppSettings {
        guard FileManager.default.fileExists(atPath: url.path) else {
            return .defaults
        }
        let data = try Data(contentsOf: url)
        return try JSONDecoder().decode(AppSettings.self, from: data).validated()
    }

    public func save(_ settings: AppSettings) throws {
        let normalized = try settings.validated()
        let directory = url.deletingLastPathComponent()
        try FileManager.default.createDirectory(
            at: directory,
            withIntermediateDirectories: true,
            attributes: [.posixPermissions: 0o700]
        )
        try FileManager.default.setAttributes(
            [.posixPermissions: 0o700],
            ofItemAtPath: directory.path
        )
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        var data = try encoder.encode(normalized)
        data.append(0x0A)
        try data.write(to: url, options: .atomic)
        try FileManager.default.setAttributes(
            [.posixPermissions: 0o600],
            ofItemAtPath: url.path
        )
    }
}
