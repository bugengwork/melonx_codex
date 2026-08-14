//
//  MeloNXTests.swift
//  MeloNXTests
//
//  Created by Stossy11 on 3/11/2024.
//

import Foundation
import Testing
@testable import MeloNX

struct MeloNXTests {

    @Test
    @MainActor
    func airConnectionCallbackStopsAfterRemoval() {
        var callbackCount = 0
        let token = Air.connection { _ in
            callbackCount += 1
        }

        Air.shared.connected.toggle()
        #expect(callbackCount == 1)

        Air.removeConnectionCallback(token)
        Air.shared.connected.toggle()
        #expect(callbackCount == 1)
    }

    @Test
    func boundedLogBufferEvictsWithoutBreakingAbsoluteCursor() {
        var buffer = BoundedLogBuffer(maxEntries: 2, maxBytes: 1_024)
        buffer.append("first")
        buffer.append("second")
        buffer.append("third")

        let snapshot = buffer.snapshot(since: 0)
        #expect(snapshot.logs == ["second", "third"])
        #expect(snapshot.nextCursor == 3)

        buffer.append("fourth")
        let nextSnapshot = buffer.snapshot(since: snapshot.nextCursor)
        #expect(nextSnapshot.logs == ["fourth"])
        #expect(nextSnapshot.nextCursor == 4)
    }

    @Test
    func boundedLogBufferEnforcesByteLimit() {
        var buffer = BoundedLogBuffer(maxEntries: 8, maxBytes: 6)
        buffer.append("1234")
        buffer.append("5678")

        #expect(buffer.allLogs() == ["5678"])
        #expect(buffer.count == 1)
    }

    @Test
    func emulationStateTransitionMatrix() {
        typealias State = Ryujinx.EmulationState

        let allowedTransitions: [State: Set<State>] = [
            .idle: [.starting],
            .starting: [.running, .stopping, .failed, .idle],
            .running: [.stopping, .failed, .idle],
            .stopping: [.failed, .idle],
            .failed: [.idle],
        ]

        for source in State.allCases {
            for destination in State.allCases {
                let expected = allowedTransitions[source, default: []]
                    .contains(destination)

                #expect(
                    source.canTransition(to: destination) == expected,
                    "\(source.rawValue) -> \(destination.rawValue)"
                )
            }
        }
    }

    @Test
    func onlyIdleAllowsStart() {
        typealias State = Ryujinx.EmulationState

        #expect(State.idle.allowsStart)
        #expect(!State.starting.allowsStart)
        #expect(!State.running.allowsStart)
        #expect(!State.stopping.allowsStart)
        #expect(!State.failed.allowsStart)
    }

    @Test
    func teardownIncompleteResultEntersBlockingFailureState() {
        typealias State = Ryujinx.EmulationState

        #expect(RyujinxBridge.teardownIncompleteResult == -2)
        #expect(State.starting.canTransition(to: .failed))
        #expect(State.running.canTransition(to: .failed))
        #expect(State.stopping.canTransition(to: .failed))
        #expect(!State.failed.allowsStart)
    }

    @Test
    func lifecycleStressAllowsExactlyTwoMiBPerRound() {
        let twoMiB = UInt64(2 * 1_024 * 1_024)
        let samples = lifecycleStressSamples { round in
            256 * 1_024 * 1_024 + UInt64(round - 3) * twoMiB
        }

        let evaluation = EmulationLifecycleStressEvaluator.evaluate(
            samples: samples,
            initialValidationErrorCount: 0,
            initialDeviceLostCount: 0
        )

        #expect(evaluation.passed)
        #expect(
            abs(
                (evaluation.slopeBytesPerRound ?? 0)
                    - Double(twoMiB)
            ) < 0.5
        )
    }

    @Test
    func lifecycleStressRejectsSlopeAboveTwoMiBPerRound() {
        let threeMiB = UInt64(3 * 1_024 * 1_024)
        let samples = lifecycleStressSamples { round in
            256 * 1_024 * 1_024 + UInt64(round - 3) * threeMiB
        }

        let evaluation = EmulationLifecycleStressEvaluator.evaluate(
            samples: samples,
            initialValidationErrorCount: 0,
            initialDeviceLostCount: 0
        )

        #expect(!evaluation.passed)
        #expect(evaluation.failures.contains { $0.contains("slope") })
    }

    @Test
    func lifecycleStressAppliesSixtyFourMiBPeakAllowanceInclusively() {
        let baseline = UInt64(256 * 1_024 * 1_024)
        let allowance = UInt64(64 * 1_024 * 1_024)
        var atLimit = lifecycleStressSamples { _ in baseline }
        atLimit[10] = lifecycleStressSample(
            round: 11,
            footprint: baseline + allowance
        )

        let passingEvaluation = EmulationLifecycleStressEvaluator.evaluate(
            samples: atLimit,
            initialValidationErrorCount: 0,
            initialDeviceLostCount: 0
        )
        #expect(passingEvaluation.passed)

        var aboveLimit = atLimit
        aboveLimit[10] = lifecycleStressSample(
            round: 11,
            footprint: baseline + allowance + 1
        )
        let failingEvaluation = EmulationLifecycleStressEvaluator.evaluate(
            samples: aboveLimit,
            initialValidationErrorCount: 0,
            initialDeviceLostCount: 0
        )
        #expect(!failingEvaluation.passed)
        #expect(failingEvaluation.failures.contains { $0.contains("peak") })
    }

    @Test
    func lifecycleStressUsesTenPercentPeakAllowanceForLargeBaseline() {
        let baseline = UInt64(1_280 * 1_024 * 1_024)
        let tenPercent = UInt64(128 * 1_024 * 1_024)
        var atLimit = lifecycleStressSamples { _ in baseline }
        atLimit[10] = lifecycleStressSample(
            round: 11,
            footprint: baseline + tenPercent
        )

        let passingEvaluation = EmulationLifecycleStressEvaluator.evaluate(
            samples: atLimit,
            initialValidationErrorCount: 0,
            initialDeviceLostCount: 0
        )
        #expect(passingEvaluation.passed)

        atLimit[10] = lifecycleStressSample(
            round: 11,
            footprint: baseline + tenPercent + 1
        )
        let failingEvaluation = EmulationLifecycleStressEvaluator.evaluate(
            samples: atLimit,
            initialValidationErrorCount: 0,
            initialDeviceLostCount: 0
        )
        #expect(!failingEvaluation.passed)
        #expect(failingEvaluation.failures.contains { $0.contains("peak") })
    }

    @Test
    func lifecycleStressRejectsMissingSamples() {
        var samples = lifecycleStressSamples { _ in 256 * 1_024 * 1_024 }
        samples.removeLast()

        let evaluation = EmulationLifecycleStressEvaluator.evaluate(
            samples: samples,
            initialValidationErrorCount: 0,
            initialDeviceLostCount: 0
        )

        #expect(!evaluation.passed)
        #expect(evaluation.failures.contains { $0.contains("Expected rounds") })
    }

    @Test
    func lifecycleStressRejectsMilestoneOrderViolation() {
        var samples = lifecycleStressSamples { _ in 256 * 1_024 * 1_024 }
        samples[4] = lifecycleStressSample(
            round: 5,
            footprint: samples[4].physicalFootprint,
            orderViolationCount: 1
        )

        let evaluation = EmulationLifecycleStressEvaluator.evaluate(
            samples: samples,
            initialValidationErrorCount: 0,
            initialDeviceLostCount: 0
        )

        #expect(!evaluation.passed)
        #expect(evaluation.failures.contains { $0.contains("order violations") })
    }

    @Test
    func lifecycleStressRejectsResidualRendererThread() {
        var samples = lifecycleStressSamples { _ in 256 * 1_024 * 1_024 }
        samples[7] = lifecycleStressSample(
            round: 8,
            footprint: samples[7].physicalFootprint,
            activeRenderThreadCount: 1
        )

        let evaluation = EmulationLifecycleStressEvaluator.evaluate(
            samples: samples,
            initialValidationErrorCount: 0,
            initialDeviceLostCount: 0
        )

        #expect(!evaluation.passed)
        #expect(evaluation.failures.contains { $0.contains("GUI.RenderLoop") })
    }

    @Test
    func lifecycleStressRejectsUnbalancedVulkanHandles() {
        var samples = lifecycleStressSamples { _ in 256 * 1_024 * 1_024 }
        samples[9] = lifecycleStressSample(
            round: 10,
            footprint: samples[9].physicalFootprint,
            unbalancedInstanceHandle: true
        )

        let evaluation = EmulationLifecycleStressEvaluator.evaluate(
            samples: samples,
            initialValidationErrorCount: 0,
            initialDeviceLostCount: 0
        )

        #expect(!evaluation.passed)
        #expect(evaluation.failures.contains { $0.contains("instance handles") })
    }

}

private func lifecycleStressSamples(
    footprint: (Int) -> UInt64
) -> [EmulationLifecycleStressSample] {
    (1...20).map { round in
        lifecycleStressSample(
            round: round,
            footprint: round >= 3 ? footprint(round) : nil
        )
    }
}

private func lifecycleStressSample(
    round: Int,
    footprint: UInt64?,
    orderViolationCount: Int64 = 0,
    activeRenderThreadCount: Int64 = 0,
    unbalancedInstanceHandle: Bool = false
) -> EmulationLifecycleStressSample {
    let createdCount = Int64(round)

    return EmulationLifecycleStressSample(
        round: round,
        sampledAt: Date(timeIntervalSince1970: TimeInterval(round)),
        physicalFootprint: footprint,
        native: EmulationLifecycleNativeSnapshot(
            currentSessionID: 0,
            sessionID: Int64(round),
            state: EmulationLifecycleNativeSnapshot.completedState,
            milestoneMask: 0b11_1111,
            requiredTeardownMilestoneMask: 0b01_1111,
            orderViolationCount: orderViolationCount,
            teardownFailed: 0,
            resultCode: 0,
            nativePhysicalFootprint: Int64(footprint ?? 0),
            activeGPUThreadCount: 0,
            activeRenderThreadCount: activeRenderThreadCount,
            vulkanInstanceCreatedCount: createdCount,
            vulkanInstanceDestroyedCount:
                unbalancedInstanceHandle ? createdCount - 1 : createdCount,
            vulkanSurfaceCreatedCount: createdCount,
            vulkanSurfaceDestroyedCount: createdCount,
            vulkanDeviceCreatedCount: createdCount,
            vulkanDeviceDestroyedCount: createdCount,
            jitMappingCreatedCount: 1,
            vulkanValidationErrorCount: 0,
            vulkanDeviceLostCount: 0
        ),
        logViolations: []
    )
}
