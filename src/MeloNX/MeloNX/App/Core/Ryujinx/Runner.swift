//
//  Runner.swift
//  MeloNX
//
//  Created by Stossy11 on 16/12/2025.
//

import Foundation

@MainActor
final class Runner {
    private var task: Task<Void, Never>?

    var isActive: Bool {
        task != nil
    }

    @discardableResult
    func start(
        operation: @escaping @Sendable () -> Int,
        completion: @escaping @MainActor @Sendable (Int) -> Void
    ) -> Bool {
        guard task == nil else {
            return false
        }

        task = Task.detached(priority: .userInitiated) { [self] in
            let result = operation()
            await finish(result: result, completion: completion)
        }

        return true
    }

    private func finish(
        result: Int,
        completion: @MainActor @Sendable (Int) -> Void
    ) {
        task = nil
        completion(result)
    }
}
