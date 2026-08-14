//
//  MemoryUsageMonitor.swift
//  MeloNX
//
//  Created by Stossy11 on 21/12/2024.
//

import Foundation
import SwiftUI

@MainActor
class MemoryUsageMonitor: ObservableObject {
    @Published private(set) var memoryUsage: UInt64 = 0
    private var task: Task<Void, Never>?

    init() {
        task = Task.detached(priority: .utility) { [weak self] in
            while !Task.isCancelled {
                let memoryUsage = Self.currentPhysicalFootprint() ?? 0

                await MainActor.run { [weak self] in
                    self?.memoryUsage = memoryUsage
                }

                // A memory label does not benefit from 5 Hz refresh. Sampling at
                // 1 Hz cuts task_info calls and main-actor publications by 80%
                // while remaining responsive enough for human-readable telemetry.
                try? await Task.sleep(nanoseconds: 1_000_000_000)
            }
        }
    }

    deinit {
        task?.cancel()
    }

    nonisolated static func currentPhysicalFootprint() -> UInt64? {
        var taskInfo = task_vm_info_data_t()
        var count = mach_msg_type_number_t(MemoryLayout<task_vm_info_data_t>.stride) / 4

        let result: kern_return_t = withUnsafeMutablePointer(to: &taskInfo) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                task_info(mach_task_self_, task_flavor_t(TASK_VM_INFO), $0, &count)
            }
        }

        if result == KERN_SUCCESS {
            return taskInfo.phys_footprint
        }

        print("[EmulationLifecycle] Failed to read phys_footprint: \(result)")
        return nil
    }

    nonisolated static func logPhysicalFootprint(sessionID: UUID?, stage: String) {
        let session = sessionID?.uuidString ?? "none"
        let bytes = currentPhysicalFootprint() ?? 0
        print("[EmulationLifecycle] session=\(session) stage=\(stage) phys_footprint=\(bytes)")
    }

    func formatMemorySize(_ bytes: UInt64) -> String {
        let formatter = ByteCountFormatter()
        formatter.allowedUnits = [.useMB, .useGB]
        formatter.countStyle = .memory
        return formatter.string(fromByteCount: Int64(bytes))
    }
}
