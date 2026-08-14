//
//  EmulatorRuntimeView.swift
//  MeloNX
//
//  Created by Codex on 12/08/2026.
//

import SwiftUI

struct EmulatorRuntimeView<Content: View>: View {
    private let content: Content

    init(environment: [EnvironmentVariable], @ViewBuilder content: () -> Content) {
        MeloNXApp.initializeEmulatorRuntime(environment: environment)
        self.content = content()
    }

    var body: some View {
        content
    }
}
