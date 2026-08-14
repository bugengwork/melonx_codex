//
//  LogCapture.swift
//  MeloNX
//
//  Created by Stossy11 on 22/09/2025.
//


import Foundation

struct BoundedLogBuffer {
    struct Snapshot {
        let logs: [String]
        let nextCursor: Int
    }

    private struct Entry {
        let value: String
        let byteCount: Int
    }

    private let maxEntries: Int
    private let maxBytes: Int
    private var storage: [Entry?]
    private var startIndex = 0
    private(set) var count = 0
    private var storedBytes = 0
    private(set) var nextCursor = 0

    init(maxEntries: Int, maxBytes: Int) {
        precondition(maxEntries > 0)
        precondition(maxBytes > 0)
        self.maxEntries = maxEntries
        self.maxBytes = maxBytes
        storage = Array(repeating: nil, count: maxEntries)
    }

    mutating func append(_ value: String) {
        if count == maxEntries {
            removeOldest()
        }

        let entry = Entry(value: value, byteCount: value.utf8.count)
        let insertionIndex = (startIndex + count) % maxEntries
        storage[insertionIndex] = entry
        count += 1
        storedBytes += entry.byteCount
        nextCursor += 1

        while storedBytes > maxBytes, count > 0 {
            removeOldest()
        }
    }

    func allLogs() -> [String] {
        snapshot(since: nextCursor - count).logs
    }

    func snapshot(since cursor: Int) -> Snapshot {
        let oldestCursor = nextCursor - count
        let firstCursor = min(max(cursor, oldestCursor), nextCursor)
        let offset = firstCursor - oldestCursor
        var logs: [String] = []
        logs.reserveCapacity(count - offset)

        for index in offset..<count {
            let storageIndex = (startIndex + index) % maxEntries
            if let entry = storage[storageIndex] {
                logs.append(entry.value)
            }
        }

        return Snapshot(logs: logs, nextCursor: nextCursor)
    }

    private mutating func removeOldest() {
        guard count > 0, let entry = storage[startIndex] else {
            return
        }

        storedBytes -= entry.byteCount
        storage[startIndex] = nil
        startIndex = (startIndex + 1) % maxEntries
        count -= 1
    }
}

final class LogCapture: ObservableObject {
    private static let capturedLogEntryLimit = 4_096
    private static let capturedLogByteLimit = 4 * 1_024 * 1_024
    private static let logPrefixRegex = try! NSRegularExpression(
        pattern: "\\d{2}:\\d{2}:\\d{2}\\.\\d{3} \\|[A-Z]+\\|",
        options: .caseInsensitive
    )

    static let shared = LogCapture()

    private var stdoutPipe: Pipe?
    private var stderrPipe: Pipe?
    private let originalStdout: Int32
    private let originalStderr: Int32

    private var continuation: AsyncStream<String>.Continuation?
    private let capturedLogsLock = NSLock()
    private var capturedLogStorage = BoundedLogBuffer(
        maxEntries: capturedLogEntryLimit,
        maxBytes: capturedLogByteLimit
    )

    public var capturedLogs: [String] {
        capturedLogsLock.lock()
        defer { capturedLogsLock.unlock() }
        return capturedLogStorage.allLogs()
    }

    lazy var logs: AsyncStream<String> = {
        AsyncStream(bufferingPolicy: .bufferingNewest(4)) { continuation in
            self.continuation = continuation
            continuation.onTermination = { _ in
                self.continuation = nil
            }
        }
    }()

    private init() {
        originalStdout = dup(STDOUT_FILENO)
        originalStderr = dup(STDERR_FILENO)
        startCapturing()
    }

    func startCapturing() {
        stdoutPipe = Pipe()
        stderrPipe = Pipe()

        redirectOutput(to: stdoutPipe!, fileDescriptor: STDOUT_FILENO)
        redirectOutput(to: stderrPipe!, fileDescriptor: STDERR_FILENO)

        setupReadabilityHandler(for: stdoutPipe!, isStdout: true)
        setupReadabilityHandler(for: stderrPipe!, isStdout: false)
    }

    func stopCapturing() {
        dup2(originalStdout, STDOUT_FILENO)
        dup2(originalStderr, STDERR_FILENO)

        stdoutPipe?.fileHandleForReading.readabilityHandler = nil
        stderrPipe?.fileHandleForReading.readabilityHandler = nil
    }

    func capturedLogCount() -> Int {
        capturedLogsLock.lock()
        defer { capturedLogsLock.unlock() }
        return capturedLogStorage.nextCursor
    }

    func capturedLogsSnapshot(since index: Int) -> BoundedLogBuffer.Snapshot {
        capturedLogsLock.lock()
        defer { capturedLogsLock.unlock() }
        return capturedLogStorage.snapshot(since: index)
    }

    private func redirectOutput(to pipe: Pipe, fileDescriptor: Int32) {
        dup2(pipe.fileHandleForWriting.fileDescriptor, fileDescriptor)
    }

    private func setupReadabilityHandler(for pipe: Pipe, isStdout: Bool) {
        pipe.fileHandleForReading.readabilityHandler = { [weak self] fileHandle in
            guard let self else { return }

            let data = fileHandle.availableData
            let originalFD = isStdout ? self.originalStdout : self.originalStderr
            write(originalFD, (data as NSData).bytes, data.count)

            guard let logString = String(data: data, encoding: .utf8),
                  let cleanedLog = self.cleanLog(logString),
                  !cleanedLog.0.isEmpty else { return }

            self.capturedLogsLock.lock()
            self.capturedLogStorage.append(cleanedLog.1)
            self.capturedLogsLock.unlock()
            self.continuation?.yield(cleanedLog.0) 

        }
    }

    private func cleanLog(_ raw: String) -> (String, String)? {
        let lines = raw.split(separator: "\n")
        let showFullLogs = UserDefaults.standard.bool(forKey: "showFullLogs")
        var displayedLines: [String] = []
        var capturedLines: [String] = []
        displayedLines.reserveCapacity(lines.count)
        capturedLines.reserveCapacity(lines.count)

        for line in lines {
            let lineString = String(line)
            let cleanedLine: String

            if let tabRange = lineString.range(of: "\t") {
                cleanedLine = lineString[tabRange.upperBound...]
                    .trimmingCharacters(in: .whitespacesAndNewlines)
            } else {
                cleanedLine = lineString.trimmingCharacters(in: .whitespacesAndNewlines)
            }

            capturedLines.append(cleanedLine)

            if showFullLogs {
                displayedLines.append(cleanedLine)
                continue
            }

            let range = NSRange(lineString.startIndex..., in: lineString)
            if Self.logPrefixRegex.firstMatch(
                in: lineString,
                options: [],
                range: range
            ) != nil {
                displayedLines.append(cleanedLine)
            }
        }

        let displayedLog = displayedLines.joined(separator: "\n")
        guard !displayedLog.isEmpty else {
            return nil
        }

        return (
            displayedLog,
            capturedLines.joined(separator: "\n")
        )
    }

    deinit {
        stopCapturing()
        continuation?.finish()
    }
}
