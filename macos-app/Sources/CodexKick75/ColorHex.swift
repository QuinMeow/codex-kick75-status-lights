// SPDX-License-Identifier: MIT
import AppKit
import SwiftUI

extension Color {
    init(kick75Hex value: String) {
        let hex = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard
            hex.count == 7,
            hex.first == "#",
            let number = UInt64(hex.dropFirst(), radix: 16)
        else {
            self = .gray
            return
        }
        self.init(
            .sRGB,
            red: Double((number >> 16) & 0xFF) / 255,
            green: Double((number >> 8) & 0xFF) / 255,
            blue: Double(number & 0xFF) / 255,
            opacity: 1
        )
    }

    var kick75Hex: String? {
        guard let rgb = NSColor(self).usingColorSpace(.sRGB) else { return nil }
        return String(
            format: "#%02X%02X%02X",
            Int(round(rgb.redComponent * 255)),
            Int(round(rgb.greenComponent * 255)),
            Int(round(rgb.blueComponent * 255))
        )
    }
}
