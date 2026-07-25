// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "CodexKick75",
    platforms: [.macOS(.v13)],
    products: [
        .executable(name: "CodexKick75", targets: ["CodexKick75"]),
        .executable(name: "CodexKick75CoreChecks", targets: ["CodexKick75CoreChecks"]),
    ],
    targets: [
        .target(name: "CodexKick75Core"),
        .executableTarget(
            name: "CodexKick75",
            dependencies: ["CodexKick75Core"]
        ),
        .executableTarget(
            name: "CodexKick75CoreChecks",
            dependencies: ["CodexKick75Core"],
            path: "Tests/CodexKick75CoreChecks"
        ),
    ]
)
