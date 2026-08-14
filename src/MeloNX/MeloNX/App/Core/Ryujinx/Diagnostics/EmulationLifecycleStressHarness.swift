//
//  EmulationLifecycleStressHarness.swift
//  MeloNX
//
//  Opt-in physical-device teardown and memory acceptance harness.
//

import Foundation
import SwiftUI

struct EmulationLifecycleStressConfiguration: Equatable {
    static let optInEnvironmentKey = "MELONX_LIFECYCLE_STRESS"
    static let romEnvironmentKey = "MELONX_LIFECYCLE_STRESS_ROM"

    let rounds: Int
    let firstFrameTimeout: TimeInterval
    let teardownTimeout: TimeInterval
    let runAfterFirstFrame: TimeInterval
    let postCompletionSampleDelay: TimeInterval
    let requestedROMPath: String?

    static func fromEnvironment(
        _ environment: [String: String] = ProcessInfo.processInfo.environment
    ) -> EmulationLifecycleStressConfiguration? {
        guard environment[optInEnvironmentKey] == "1" else {
            return nil
        }

        return EmulationLifecycleStressConfiguration(
            rounds: 20,
            firstFrameTimeout: boundedSeconds(
                environment["MELONX_LIFECYCLE_STRESS_FIRST_FRAME_TIMEOUT"],
                defaultValue: 120,
                range: 15...600
            ),
            teardownTimeout: boundedSeconds(
                environment["MELONX_LIFECYCLE_STRESS_TEARDOWN_TIMEOUT"],
                defaultValue: 60,
                range: 30...180
            ),
            runAfterFirstFrame: boundedSeconds(
                environment["MELONX_LIFECYCLE_STRESS_RUN_SECONDS"],
                defaultValue: 3,
                range: 1...60
            ),
            postCompletionSampleDelay: 5,
            requestedROMPath: environment[romEnvironmentKey]
        )
    }

    func resolveROM(fileManager: FileManager = .default) throws -> URL {
        guard let documentsURL = fileManager.urls(
            for: .documentDirectory,
            in: .userDomainMask
        ).first else {
            throw EmulationLifecycleStressError.missingDocumentsDirectory
        }

        if let requestedROMPath, !requestedROMPath.isEmpty {
            let requestedURL: URL

            if requestedROMPath.hasPrefix("/") {
                requestedURL = URL(fileURLWithPath: requestedROMPath)
            } else {
                requestedURL = documentsURL
                    .appendingPathComponent("roms", isDirectory: true)
                    .appendingPathComponent(requestedROMPath)
            }

            guard fileManager.fileExists(atPath: requestedURL.path),
                  GameFileType.isSupported(fileExtension: requestedURL.pathExtension) else {
                throw EmulationLifecycleStressError.invalidROM(requestedURL.path)
            }

            return requestedURL
        }

        let romsURL = documentsURL.appendingPathComponent("roms", isDirectory: true)
        let candidates = (try? fileManager.contentsOfDirectory(
            at: romsURL,
            includingPropertiesForKeys: [.isRegularFileKey],
            options: [.skipsHiddenFiles]
        )) ?? []

        let supportedROMs = candidates
            .filter {
                GameFileType.isSupported(fileExtension: $0.pathExtension)
            }
            .sorted(by: { $0.lastPathComponent < $1.lastPathComponent })

        guard let firstROM = supportedROMs.first else {
            throw EmulationLifecycleStressError.noROM(romsURL.path)
        }

        return firstROM
    }

    private static func boundedSeconds(
        _ value: String?,
        defaultValue: TimeInterval,
        range: ClosedRange<TimeInterval>
    ) -> TimeInterval {
        guard let value,
              let parsed = TimeInterval(value),
              range.contains(parsed) else {
            return defaultValue
        }

        return parsed
    }
}

enum EmulationLifecycleStressError: LocalizedError {
    case missingDocumentsDirectory
    case noROM(String)
    case invalidROM(String)
    case nativeDiagnosticUnavailable(String)
    case firstFrameTimedOut(Int)
    case runEndedBeforeStop(Int)
    case teardownTimedOut(Int)
    case nativeFailure(Int, String)
    case unableToCloneConfiguration

    var errorDescription: String? {
        switch self {
        case .missingDocumentsDirectory:
            return "Documents directory is unavailable."
        case let .noROM(path):
            return "No supported ROM was found in \(path)."
        case let .invalidROM(path):
            return "The requested stress-test ROM is missing or unsupported: \(path)."
        case let .nativeDiagnosticUnavailable(reason):
            return "Native lifecycle diagnostics are unavailable: \(reason)."
        case let .firstFrameTimedOut(round):
            return "Round \(round) did not reach FirstFrame with a positive FPS before timeout."
        case let .runEndedBeforeStop(round):
            return "Round \(round) ended before the harness could issue Stop."
        case let .teardownTimedOut(round):
            return "Round \(round) did not return from native teardown before timeout."
        case let .nativeFailure(round, reason):
            return "Round \(round) failed lifecycle validation: \(reason)."
        case .unableToCloneConfiguration:
            return "The emulator configuration could not be cloned."
        }
    }
}

struct EmulationLifecycleNativeSnapshot: Codable, Equatable {
    static let completedState: Int64 = 6
    static let firstFrameMask: Int64 = 1 << 5

    let currentSessionID: Int64
    let sessionID: Int64
    let state: Int64
    let milestoneMask: Int64
    let requiredTeardownMilestoneMask: Int64
    let orderViolationCount: Int64
    let teardownFailed: Int64
    let resultCode: Int64
    let nativePhysicalFootprint: Int64
    let activeGPUThreadCount: Int64
    let activeRenderThreadCount: Int64
    let vulkanInstanceCreatedCount: Int64
    let vulkanInstanceDestroyedCount: Int64
    let vulkanSurfaceCreatedCount: Int64
    let vulkanSurfaceDestroyedCount: Int64
    let vulkanDeviceCreatedCount: Int64
    let vulkanDeviceDestroyedCount: Int64
    let jitMappingCreatedCount: Int64
    let vulkanValidationErrorCount: Int64
    let vulkanDeviceLostCount: Int64

    static func readCompleted() throws -> EmulationLifecycleNativeSnapshot {
        let schema = try diagnostic(.schemaVersion)

        guard schema == 1 else {
            throw EmulationLifecycleStressError.nativeDiagnosticUnavailable(
                "unsupported schema \(schema)"
            )
        }

        return EmulationLifecycleNativeSnapshot(
            currentSessionID: try diagnostic(.currentSessionID),
            sessionID: try diagnostic(.lastSessionID),
            state: try diagnostic(.lastSessionState),
            milestoneMask: try diagnostic(.lastMilestoneMask),
            requiredTeardownMilestoneMask: try diagnostic(.requiredTeardownMilestoneMask),
            orderViolationCount: try diagnostic(.lastOrderViolationCount),
            teardownFailed: try diagnostic(.lastTeardownFailed),
            resultCode: try diagnostic(.lastResultCode),
            nativePhysicalFootprint: try diagnostic(.lastPhysicalFootprint),
            activeGPUThreadCount: try diagnostic(.activeGPUThreadCount),
            activeRenderThreadCount: try diagnostic(.activeRenderThreadCount),
            vulkanInstanceCreatedCount: try diagnostic(.vulkanInstanceCreatedCount),
            vulkanInstanceDestroyedCount: try diagnostic(.vulkanInstanceDestroyedCount),
            vulkanSurfaceCreatedCount: try diagnostic(.vulkanSurfaceCreatedCount),
            vulkanSurfaceDestroyedCount: try diagnostic(.vulkanSurfaceDestroyedCount),
            vulkanDeviceCreatedCount: try diagnostic(.vulkanDeviceCreatedCount),
            vulkanDeviceDestroyedCount: try diagnostic(.vulkanDeviceDestroyedCount),
            jitMappingCreatedCount: try diagnostic(.jitMappingCreatedCount),
            vulkanValidationErrorCount: try diagnostic(.vulkanValidationErrorCount),
            vulkanDeviceLostCount: try diagnostic(.vulkanDeviceLostCount)
        )
    }

    static func currentSessionIDAndMilestones() throws -> (Int64, Int64) {
        (
            try diagnostic(.currentSessionID),
            try diagnostic(.currentMilestoneMask)
        )
    }

    static func readCounter(
        _ field: RyujinxBridge.EmulationLifecycleDiagnosticField
    ) throws -> Int64 {
        try diagnostic(field)
    }

    private static func diagnostic(
        _ field: RyujinxBridge.EmulationLifecycleDiagnosticField
    ) throws -> Int64 {
        let value = RyujinxBridge.emulationLifecycleDiagnostic(field)

        guard value != Int64.min else {
            throw EmulationLifecycleStressError.nativeDiagnosticUnavailable(
                "field \(field.rawValue)"
            )
        }

        return value
    }
}

struct EmulationLifecycleStressSample: Codable, Equatable {
    let round: Int
    let sampledAt: Date
    let physicalFootprint: UInt64?
    let native: EmulationLifecycleNativeSnapshot
    let logViolations: [String]
}

struct EmulationLifecycleStressEvaluation: Codable, Equatable {
    let passed: Bool
    let failures: [String]
    let slopeBytesPerRound: Double?
    let baselineBytes: UInt64?
    let peakBytes: UInt64?
    let peakLimitBytes: UInt64?
}

enum EmulationLifecycleStressEvaluator {
    static let expectedRounds = 20
    static let baselineRound = 3
    static let firstMeasuredRound = 4
    static let maximumSlopeBytesPerRound = Double(2 * 1_024 * 1_024)
    static let minimumPeakAllowanceBytes = UInt64(64 * 1_024 * 1_024)

    static func evaluate(
        samples: [EmulationLifecycleStressSample],
        initialValidationErrorCount: Int64,
        initialDeviceLostCount: Int64
    ) -> EmulationLifecycleStressEvaluation {
        var failures: [String] = []
        let sortedSamples = samples.sorted(by: { $0.round < $1.round })
        let actualRounds = sortedSamples.map(\.round)
        let expectedRoundNumbers = Array(1...expectedRounds)

        if actualRounds != expectedRoundNumbers {
            failures.append(
                "Expected rounds \(expectedRoundNumbers), received \(actualRounds)."
            )
        }

        var previousSessionID: Int64?

        for sample in sortedSamples {
            failures.append(contentsOf: lifecycleFailures(
                sample: sample,
                previousSessionID: previousSessionID,
                initialValidationErrorCount: initialValidationErrorCount,
                initialDeviceLostCount: initialDeviceLostCount
            ))
            previousSessionID = sample.native.sessionID
        }

        let measuredSamples = sortedSamples.filter {
            $0.round >= baselineRound && $0.physicalFootprint != nil
        }
        let baseline = measuredSamples.first(where: { $0.round == baselineRound })?
            .physicalFootprint
        let postBaseline = measuredSamples.filter { $0.round >= firstMeasuredRound }
        let slope = regressionSlope(measuredSamples)
        let peak = postBaseline.compactMap(\.physicalFootprint).max()
        var peakLimit: UInt64?

        if measuredSamples.map(\.round) != Array(baselineRound...expectedRounds) {
            failures.append(
                "Memory samples must contain every round from "
                    + "\(baselineRound) through \(expectedRounds)."
            )
        }

        if let slope, slope > maximumSlopeBytesPerRound + 0.5 {
            failures.append(
                String(
                    format: "Memory slope %.2f MiB/round exceeds 2.00 MiB/round.",
                    slope / Double(1_024 * 1_024)
                )
            )
        } else if slope == nil {
            failures.append("Memory slope could not be calculated.")
        }

        if let baseline {
            let tenPercent = UInt64((Double(baseline) * 0.10).rounded(.up))
            let allowance = max(minimumPeakAllowanceBytes, tenPercent)
            peakLimit = baseline.addingReportingOverflow(allowance).overflow
                ? UInt64.max
                : baseline + allowance

            if let peak, let peakLimit, peak > peakLimit {
                failures.append(
                    "Memory peak \(peak) exceeds allowed \(peakLimit) bytes."
                )
            } else if peak == nil {
                failures.append("Post-baseline memory peak is unavailable.")
            }
        } else {
            failures.append("Round 3 baseline memory sample is unavailable.")
        }

        return EmulationLifecycleStressEvaluation(
            passed: failures.isEmpty,
            failures: failures,
            slopeBytesPerRound: slope,
            baselineBytes: baseline,
            peakBytes: peak,
            peakLimitBytes: peakLimit
        )
    }

    static func lifecycleFailures(
        sample: EmulationLifecycleStressSample,
        previousSessionID: Int64?,
        initialValidationErrorCount: Int64,
        initialDeviceLostCount: Int64
    ) -> [String] {
        let round = sample.round
        let native = sample.native
        var failures: [String] = []

        if native.currentSessionID != 0 {
            failures.append("Round \(round): native session remains installed.")
        }
        if native.sessionID <= 0 ||
            (previousSessionID != nil && native.sessionID <= previousSessionID!) {
            failures.append("Round \(round): sessionId did not increase.")
        }
        if native.state != EmulationLifecycleNativeSnapshot.completedState {
            failures.append("Round \(round): native state is \(native.state), not Completed.")
        }
        if native.resultCode == Int64(RyujinxBridge.teardownIncompleteResult) {
            failures.append("Round \(round): native teardown returned -2.")
        } else if native.resultCode != 0 {
            failures.append("Round \(round): native returned \(native.resultCode).")
        }
        if native.teardownFailed != 0 {
            failures.append("Round \(round): native reported teardown failure.")
        }
        if native.orderViolationCount != 0 {
            failures.append(
                "Round \(round): lifecycle order violations="
                    + "\(native.orderViolationCount)."
            )
        }
        if native.milestoneMask & native.requiredTeardownMilestoneMask !=
            native.requiredTeardownMilestoneMask {
            failures.append(
                "Round \(round): required teardown milestones are incomplete."
            )
        }
        if native.milestoneMask & EmulationLifecycleNativeSnapshot.firstFrameMask == 0 {
            failures.append("Round \(round): FirstFrame was not observed.")
        }
        if native.activeGPUThreadCount != 0 {
            failures.append(
                "Round \(round): GPU.MainThread count="
                    + "\(native.activeGPUThreadCount)."
            )
        }
        if native.activeRenderThreadCount != 0 {
            failures.append(
                "Round \(round): GUI.RenderLoop count="
                    + "\(native.activeRenderThreadCount)."
            )
        }
        if native.vulkanInstanceCreatedCount != native.vulkanInstanceDestroyedCount {
            failures.append("Round \(round): Vulkan instance handles are unbalanced.")
        }
        if native.vulkanSurfaceCreatedCount != native.vulkanSurfaceDestroyedCount {
            failures.append("Round \(round): Vulkan surface handles are unbalanced.")
        }
        if native.vulkanDeviceCreatedCount != native.vulkanDeviceDestroyedCount {
            failures.append("Round \(round): Vulkan device handles are unbalanced.")
        }
        if native.vulkanValidationErrorCount != initialValidationErrorCount {
            failures.append("Round \(round): Vulkan validation error count increased.")
        }
        if native.vulkanDeviceLostCount != initialDeviceLostCount {
            failures.append("Round \(round): Vulkan device-lost count increased.")
        }
        if !sample.logViolations.isEmpty {
            failures.append(
                "Round \(round): validation/device-lost log markers: "
                    + sample.logViolations.joined(separator: " | ")
            )
        }

        return failures
    }

    private static func regressionSlope(
        _ samples: [EmulationLifecycleStressSample]
    ) -> Double? {
        let points: [(Double, Double)] = samples.compactMap { sample in
            guard let footprint = sample.physicalFootprint else {
                return nil
            }
            return (Double(sample.round), Double(footprint))
        }

        guard points.count >= 2 else {
            return nil
        }

        let meanX = points.map(\.0).reduce(0, +) / Double(points.count)
        let meanY = points.map(\.1).reduce(0, +) / Double(points.count)
        let numerator = points.reduce(0) {
            $0 + ($1.0 - meanX) * ($1.1 - meanY)
        }
        let denominator = points.reduce(0) {
            $0 + ($1.0 - meanX) * ($1.0 - meanX)
        }

        guard denominator > 0 else {
            return nil
        }

        return numerator / denominator
    }
}

struct EmulationLifecycleStressReport: Codable {
    let schemaVersion: Int
    let startedAt: Date
    let completedAt: Date
    let romName: String
    let initialValidationErrorCount: Int64
    let initialDeviceLostCount: Int64
    let samples: [EmulationLifecycleStressSample]
    let evaluation: EmulationLifecycleStressEvaluation
    let harnessFailure: String?
}

@MainActor
final class EmulationLifecycleStressHarness: ObservableObject {
    enum Status: String {
        case ready
        case running
        case pass
        case fail
    }

    @Published private(set) var status: Status = .ready
    @Published private(set) var currentRound = 0
    @Published private(set) var summary = "Waiting to start."
    @Published private(set) var reportPath = ""

    private let configuration: EmulationLifecycleStressConfiguration
    private let ryujinx: Ryujinx
    private var hasRun = false

    init(
        configuration: EmulationLifecycleStressConfiguration,
        ryujinx: Ryujinx
    ) {
        self.configuration = configuration
        self.ryujinx = ryujinx
    }

    func run() async {
        guard !hasRun else {
            return
        }

        hasRun = true
        status = .running
        let startedAt = Date()
        var samples: [EmulationLifecycleStressSample] = []
        var romName = "unknown"
        var initialValidationErrorCount: Int64 = 0
        var initialDeviceLostCount: Int64 = 0
        var harnessFailure: String?

        do {
            let romURL = try configuration.resolveROM()
            romName = romURL.lastPathComponent
            initialValidationErrorCount = try EmulationLifecycleNativeSnapshot.readCounter(
                .vulkanValidationErrorCount
            )
            initialDeviceLostCount = try EmulationLifecycleNativeSnapshot.readCounter(
                .vulkanDeviceLostCount
            )

            let logCapture = LogCapture.shared
            var logCursor = logCapture.capturedLogCount()
            var previousSessionID: Int64?

            for round in 1...configuration.rounds {
                try Task.checkCancellation()
                currentRound = round
                summary = "Round \(round)/\(configuration.rounds): starting"

                let arguments = try makeArguments(romURL: romURL)
                try ryujinx.start(with: arguments)
                let liveSessionID = try await waitForFirstFrameAndFPS(round: round)

                try await sleep(seconds: configuration.runAfterFirstFrame)

                guard ryujinx.state == .running else {
                    throw EmulationLifecycleStressError.runEndedBeforeStop(round)
                }

                summary = "Round \(round)/\(configuration.rounds): stopping"
                try ryujinx.stop()
                try await waitForCompletion(round: round)

                // Round 3 establishes the warmed-up baseline. Rounds 4-20
                // remain idle for the full five-second sampling window.
                if round >= EmulationLifecycleStressEvaluator.baselineRound {
                    summary = "Round \(round)/\(configuration.rounds): idle sampling"
                    try await sleep(seconds: configuration.postCompletionSampleDelay)
                }

                let footprint = round >= EmulationLifecycleStressEvaluator.baselineRound
                    ? MemoryUsageMonitor.currentPhysicalFootprint()
                    : nil
                let native = try EmulationLifecycleNativeSnapshot.readCompleted()

                guard native.sessionID == liveSessionID else {
                    throw EmulationLifecycleStressError.nativeFailure(
                        round,
                        "completed sessionId \(native.sessionID) does not match "
                            + "live sessionId \(liveSessionID)"
                    )
                }

                let logSnapshot = logCapture.capturedLogsSnapshot(since: logCursor)
                let newLogs = logSnapshot.logs
                logCursor = logSnapshot.nextCursor
                let sample = EmulationLifecycleStressSample(
                    round: round,
                    sampledAt: Date(),
                    physicalFootprint: footprint,
                    native: native,
                    logViolations: Self.findLogViolations(newLogs)
                )
                samples.append(sample)

                let lifecycleFailures =
                    EmulationLifecycleStressEvaluator.lifecycleFailures(
                        sample: sample,
                        previousSessionID: previousSessionID,
                        initialValidationErrorCount: initialValidationErrorCount,
                        initialDeviceLostCount: initialDeviceLostCount
                    )

                if let firstFailure = lifecycleFailures.first {
                    throw EmulationLifecycleStressError.nativeFailure(
                        round,
                        firstFailure
                    )
                }

                previousSessionID = native.sessionID
            }
        } catch {
            harnessFailure = error.localizedDescription

            if ryujinx.isRunning {
                try? ryujinx.stop()
            }
        }

        var evaluation = EmulationLifecycleStressEvaluator.evaluate(
            samples: samples,
            initialValidationErrorCount: initialValidationErrorCount,
            initialDeviceLostCount: initialDeviceLostCount
        )

        if let harnessFailure {
            evaluation = EmulationLifecycleStressEvaluation(
                passed: false,
                failures: [harnessFailure] + evaluation.failures,
                slopeBytesPerRound: evaluation.slopeBytesPerRound,
                baselineBytes: evaluation.baselineBytes,
                peakBytes: evaluation.peakBytes,
                peakLimitBytes: evaluation.peakLimitBytes
            )
        }

        let report = EmulationLifecycleStressReport(
            schemaVersion: 1,
            startedAt: startedAt,
            completedAt: Date(),
            romName: romName,
            initialValidationErrorCount: initialValidationErrorCount,
            initialDeviceLostCount: initialDeviceLostCount,
            samples: samples,
            evaluation: evaluation,
            harnessFailure: harnessFailure
        )

        do {
            reportPath = try writeReport(report).path
        } catch {
            evaluation = EmulationLifecycleStressEvaluation(
                passed: false,
                failures: ["Unable to write report: \(error.localizedDescription)"]
                    + evaluation.failures,
                slopeBytesPerRound: evaluation.slopeBytesPerRound,
                baselineBytes: evaluation.baselineBytes,
                peakBytes: evaluation.peakBytes,
                peakLimitBytes: evaluation.peakLimitBytes
            )
        }

        if evaluation.passed {
            status = .pass
            summary = String(
                format: "PASS — slope %.2f MiB/round; peak %.2f MiB",
                (evaluation.slopeBytesPerRound ?? 0) / Double(1_024 * 1_024),
                Double(evaluation.peakBytes ?? 0) / Double(1_024 * 1_024)
            )
        } else {
            status = .fail
            summary = "FAIL — " + evaluation.failures.joined(separator: " ")
        }
    }

    private func waitForFirstFrameAndFPS(round: Int) async throws -> Int64 {
        let deadline = Date().addingTimeInterval(configuration.firstFrameTimeout)
        var observedSessionID: Int64 = 0

        while Date() < deadline {
            try Task.checkCancellation()

            if ryujinx.state == .failed || ryujinx.state == .idle {
                throw EmulationLifecycleStressError.runEndedBeforeStop(round)
            }

            let (sessionID, milestones) =
                try EmulationLifecycleNativeSnapshot.currentSessionIDAndMilestones()
            observedSessionID = max(observedSessionID, sessionID)

            if sessionID > 0,
               milestones & EmulationLifecycleNativeSnapshot.firstFrameMask != 0,
               RyujinxBridge.currentFPS > 0 {
                summary = "Round \(round)/\(configuration.rounds): first frame"
                return sessionID
            }

            try await sleep(seconds: 0.1)
        }

        if ryujinx.isRunning {
            try? ryujinx.stop()
        }

        _ = observedSessionID
        throw EmulationLifecycleStressError.firstFrameTimedOut(round)
    }

    private func waitForCompletion(round: Int) async throws {
        let deadline = Date().addingTimeInterval(configuration.teardownTimeout)

        while Date() < deadline {
            try Task.checkCancellation()

            switch ryujinx.state {
            case .idle:
                return
            case .failed:
                throw EmulationLifecycleStressError.nativeFailure(
                    round,
                    "Swift entered failed state"
                )
            case .starting, .running, .stopping:
                break
            }

            try await sleep(seconds: 0.1)
        }

        try? ryujinx.stop()
        throw EmulationLifecycleStressError.teardownTimedOut(round)
    }

    private func makeArguments(romURL: URL) throws -> Ryujinx.Arguments {
        let encoder = JSONEncoder()
        let decoder = JSONDecoder()
        let encoded = try encoder.encode(SettingsManager.shared.config)

        guard let arguments = try? decoder.decode(
            Ryujinx.Arguments.self,
            from: encoded
        ) else {
            throw EmulationLifecycleStressError.unableToCloneConfiguration
        }

        arguments.gamepath = romURL.path
        arguments.inputids = ["0"]
        arguments.debuglogs = true
        arguments.additionalArgs = Self.replacingOption(
            "--graphics-debug-level",
            value: "All",
            in: arguments.additionalArgs
        )
        return arguments
    }

    private static func replacingOption(
        _ option: String,
        value: String,
        in arguments: [String]
    ) -> [String] {
        var filtered: [String] = []
        var index = 0

        while index < arguments.count {
            if arguments[index] == option {
                index += min(2, arguments.count - index)
            } else {
                filtered.append(arguments[index])
                index += 1
            }
        }

        filtered.append(contentsOf: [option, value])
        return filtered
    }

    private static func findLogViolations(_ logs: [String]) -> [String] {
        var violations: [String] = []

        for line in logs {
            let lowercased = line.lowercased()
            let isValidation =
                lowercased.contains("validation error") ||
                lowercased.contains("vuid-")
            let isDeviceLost =
                lowercased.contains("device lost") ||
                lowercased.contains("error_device_lost") ||
                lowercased.contains("errordevicelost")

            if isValidation || isDeviceLost {
                violations.append(
                    String(line.prefix(500))
                )
            }
        }

        return Array(violations.prefix(20))
    }

    private func writeReport(
        _ report: EmulationLifecycleStressReport
    ) throws -> URL {
        let fileManager = FileManager.default
        let documentsURL = fileManager.urls(
            for: .documentDirectory,
            in: .userDomainMask
        )[0]
        let reportDirectory = documentsURL.appendingPathComponent(
            "LifecycleStressReports",
            isDirectory: true
        )
        try fileManager.createDirectory(
            at: reportDirectory,
            withIntermediateDirectories: true
        )

        let formatter = DateFormatter()
        formatter.dateFormat = "yyyy-MM-dd_HH-mm-ss"
        let reportURL = reportDirectory.appendingPathComponent(
            "lifecycle-stress-\(formatter.string(from: report.startedAt)).json"
        )
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        encoder.dateEncodingStrategy = .iso8601
        try encoder.encode(report).write(to: reportURL, options: .atomic)
        return reportURL
    }

    private func sleep(seconds: TimeInterval) async throws {
        let nanoseconds = UInt64(seconds * 1_000_000_000)
        try await Task.sleep(nanoseconds: nanoseconds)
    }
}

struct EmulationLifecycleStressView: View {
    @ObservedObject private var ryujinx = Ryujinx.shared
    @StateObject private var harness: EmulationLifecycleStressHarness

    init(configuration: EmulationLifecycleStressConfiguration) {
        _harness = StateObject(
            wrappedValue: EmulationLifecycleStressHarness(
                configuration: configuration,
                ryujinx: .shared
            )
        )
    }

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()

            if ryujinx.isRunning {
                MetalView()
                    .ignoresSafeArea()
            }

            VStack(spacing: 12) {
                Text(harness.status.rawValue.uppercased())
                    .font(.title.bold())
                    .accessibilityIdentifier("lifecycle-stress-status")

                Text("Round \(harness.currentRound)/20")
                    .accessibilityIdentifier("lifecycle-stress-round")

                ScrollView {
                    Text(harness.summary)
                        .font(.footnote.monospaced())
                        .textSelection(.enabled)
                        .accessibilityIdentifier("lifecycle-stress-summary")

                    if !harness.reportPath.isEmpty {
                        Text(harness.reportPath)
                            .font(.caption2.monospaced())
                            .textSelection(.enabled)
                            .accessibilityIdentifier("lifecycle-stress-report-path")
                    }
                }
                .frame(maxHeight: 220)
            }
            .foregroundStyle(.white)
            .padding()
            .background(.black.opacity(0.8))
        }
        .task {
            await harness.run()
        }
    }
}
