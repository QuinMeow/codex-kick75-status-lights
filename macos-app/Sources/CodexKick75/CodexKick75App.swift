// SPDX-License-Identifier: MIT
import SwiftUI

@main
struct CodexKick75App: App {
    @StateObject private var model = SettingsViewModel()

    var body: some Scene {
        MenuBarExtra {
            ContentView(model: model)
        } label: {
            Image(systemName: model.menuIcon)
        }
        .menuBarExtraStyle(.window)
    }
}
