// SPDX-License-Identifier: MIT
import Darwin
import Foundation

public struct HardwareSnapshot: Codable, Equatable, Sendable {
    public let available: Bool?
    public let error: String?
    public let checkedAt: Double?

    enum CodingKeys: String, CodingKey {
        case available, error
        case checkedAt = "checked_at"
    }
}

public struct PreviewSnapshot: Codable, Equatable, Sendable {
    public let status: String
    public let remainingSeconds: Double

    enum CodingKeys: String, CodingKey {
        case status
        case remainingSeconds = "remaining_seconds"
    }
}

public struct DaemonSnapshot: Codable, Equatable, Sendable {
    public let ok: Bool
    public let version: String
    public let status: String
    public let desiredStatus: String
    public let light: LightSetting?
    public let tasks: Int
    public let settings: String
    public let settingsError: String?
    public let preview: PreviewSnapshot?
    public let hardware: HardwareSnapshot

    enum CodingKeys: String, CodingKey {
        case ok, version, status, light, tasks, settings, preview, hardware
        case desiredStatus = "desired_status"
        case settingsError = "settings_error"
    }
}

public enum DaemonError: LocalizedError {
    case socket(String)
    case pathTooLong
    case emptyResponse
    case invalidResponse
    case rejected(String)

    public var errorDescription: String? {
        switch self {
        case .socket(let detail): return "无法连接后台服务：\(detail)"
        case .pathTooLong: return "后台服务 socket 路径过长"
        case .emptyResponse: return "后台服务没有返回数据"
        case .invalidResponse: return "后台服务返回了无效数据"
        case .rejected(let detail): return detail
        }
    }
}

public actor DaemonClient {
    public let socketURL: URL

    public init(
        socketURL: URL = SettingsStore.applicationDirectory.appendingPathComponent("status.sock")
    ) {
        self.socketURL = socketURL
    }

    public func ping() throws -> DaemonSnapshot {
        try snapshot(for: ["command": "ping"])
    }

    public func reload() throws -> DaemonSnapshot {
        try snapshot(for: ["command": "reload"])
    }

    public func preview(
        status: StatusKind,
        light: LightSetting,
        seconds: Double = 3.0
    ) throws -> DaemonSnapshot {
        try snapshot(
            for: [
                "command": "preview",
                "state": status.rawValue,
                "color": light.color,
                "brightness": light.brightness,
                "seconds": seconds,
            ]
        )
    }

    private func snapshot(for request: [String: Any]) throws -> DaemonSnapshot {
        let data = try exchange(request)
        guard
            let response = try JSONSerialization.jsonObject(with: data) as? [String: Any],
            let ok = response["ok"] as? Bool
        else {
            throw DaemonError.invalidResponse
        }
        if !ok {
            throw DaemonError.rejected(response["error"] as? String ?? "后台服务拒绝了请求")
        }
        return try JSONDecoder().decode(DaemonSnapshot.self, from: data)
    }

    private func exchange(_ request: [String: Any]) throws -> Data {
        let descriptor = Darwin.socket(AF_UNIX, SOCK_STREAM, 0)
        guard descriptor >= 0 else { throw socketError() }
        defer { Darwin.close(descriptor) }

        var timeout = timeval(tv_sec: 2, tv_usec: 0)
        setsockopt(
            descriptor,
            SOL_SOCKET,
            SO_RCVTIMEO,
            &timeout,
            socklen_t(MemoryLayout<timeval>.size)
        )
        setsockopt(
            descriptor,
            SOL_SOCKET,
            SO_SNDTIMEO,
            &timeout,
            socklen_t(MemoryLayout<timeval>.size)
        )

        let path = socketURL.path
        var address = sockaddr_un()
        let pathCapacity = MemoryLayout.size(ofValue: address.sun_path)
        guard path.utf8.count < pathCapacity else { throw DaemonError.pathTooLong }
        address.sun_family = sa_family_t(AF_UNIX)
        address.sun_len = UInt8(MemoryLayout<sockaddr_un>.size)
        withUnsafeMutablePointer(to: &address.sun_path) { pointer in
            pointer.withMemoryRebound(to: CChar.self, capacity: pathCapacity) { destination in
                path.withCString { source in
                    _ = strlcpy(destination, source, pathCapacity)
                }
            }
        }
        let connected = withUnsafePointer(to: &address) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) { socketAddress in
                Darwin.connect(
                    descriptor,
                    socketAddress,
                    socklen_t(MemoryLayout<sockaddr_un>.size)
                )
            }
        }
        guard connected == 0 else { throw socketError() }

        var payload = try JSONSerialization.data(withJSONObject: request)
        payload.append(0x0A)
        try payload.withUnsafeBytes { bytes in
            guard let base = bytes.baseAddress else { return }
            var sent = 0
            while sent < bytes.count {
                let result = Darwin.send(descriptor, base.advanced(by: sent), bytes.count - sent, 0)
                guard result > 0 else { throw socketError() }
                sent += result
            }
        }

        var response = Data()
        var buffer = [UInt8](repeating: 0, count: 4096)
        while response.count < 65_536 {
            let count = Darwin.recv(descriptor, &buffer, buffer.count, 0)
            if count == 0 { break }
            guard count > 0 else { throw socketError() }
            response.append(buffer, count: count)
            if response.contains(0x0A) { break }
        }
        guard !response.isEmpty else { throw DaemonError.emptyResponse }
        if let newline = response.firstIndex(of: 0x0A) {
            response = response[..<newline]
        }
        return response
    }

    private func socketError() -> DaemonError {
        DaemonError.socket(String(cString: strerror(errno)))
    }
}
