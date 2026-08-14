//
//  Ryujinx.swift
//  MeloNX
//
//  Created by Stossy11 on 3/11/2024.
//

import Foundation
import SwiftUI
import GameController
import MetalKit
import Metal
import Darwin
import NavigationStackBackport


struct iOSNav<Content: View>: View {
    @ViewBuilder var content: () -> Content

    var body: some View {
        if #available(iOS 16, *) {
            SwiftUI.NavigationStack(root: content)
        } else {
            NavigationStackBackport.NavigationStack(root: content)
        }
    }
}

@MainActor
class Ryujinx : ObservableObject {
    enum EmulationState: String, CaseIterable, Hashable {
        case idle
        case starting
        case running
        case stopping
        case failed

        var allowsStart: Bool {
            self == .idle
        }

        func canTransition(to newState: EmulationState) -> Bool {
            switch (self, newState) {
            case (.idle, .starting),
                 (.starting, .running),
                 (.starting, .stopping),
                 (.starting, .failed),
                 (.starting, .idle),
                 (.running, .stopping),
                 (.running, .failed),
                 (.running, .idle),
                 (.stopping, .failed),
                 (.stopping, .idle),
                 (.failed, .idle):
                return true
            default:
                return false
            }
        }
    }

    // Models
    @Published private(set) var state: EmulationState = .idle
    @Published var showLoading = true
    @Published var jitenabled = false
    @Published var isPortrait = false

    var isRunning: Bool {
        switch state {
        case .starting, .running, .stopping:
            return true
        case .failed:
            return runner.isActive
        case .idle:
            return false
        }
    }
    
    // LDN and Firmware
    @AppStorage("LDN_MITM") var ldn = printAllIPv4Addresses().first ?? "Unknown"
    @Published var firmwareversion = "0"
    
    // UI
    @Published var metalLayer: CAMetalLayer? = nil
    @Published var emulationUIView: MeloMTKView? = nil
    @Published var defMLContentSize: CGFloat?
    var shouldMetal: Bool {
        metalLayer == nil
    }
    
    // Ryujinx Models
    @Published var config: Ryujinx.Arguments? = nil
    @Published var games: [Game] = []
    @Published var aspectRatio: AspectRatio = .fixed16x9
    
    // Classes
    let controllerManager = ControllerManager.shared
    
    // Ryujinx Thread
    private let runner = Runner()
    private var activeRunID: UUID?
    private var activeSecurityScopedURL: URL?
    private var stopWatchdog: Task<Void, Never>?
    private var cleanupFailureAlert: UIAlertController?
    private var executionFailureTask: Task<Void, Never>?
    private var lastFailedRunID: UUID?
    
    static let shared = Ryujinx()
    
    init() {
        checkForJIT()
    }
    
    func addGames() {
        self.games = loadGames()
    }
    
    public class Arguments : Observable, Codable, Equatable {
        @IgnoreCoding var gamepath: String = ""
        @IgnoreCoding var inputids: [String] = []
        var resscale: Double = 1.0
        var debuglogs: Bool = false
        var tracelogs: Bool = false
        var nintendoinput: Bool = true
        var enableInternet: Bool = false
        var ldn_mitm: Bool = false
        var listinputids: Bool = false
        var aspectRatio: AspectRatio = .fixed16x9
        var memoryManagerMode: String = "HostMappedUnsafe"
        var enableShaderCache: Bool = false
        var hypervisor: Bool = false
        var enableDockedMode: Bool = false
        var enableTextureRecompression: Bool = true
        var additionalArgs: [String] = []
        var maxAnisotropy: Double = 1.0
        var macroHLE: Bool = false
        var ignoreMissingServices: Bool = false
        var expandRam: Bool = false
        var dfsIntegrityChecks: Bool = false
        var disablePTC: Bool = false
        var disablevsync: Bool = false
        var language: SystemLanguage = .americanEnglish
        var regioncode: SystemRegionCode = .usa
        
        
        static func == (lhs: Arguments, rhs: Arguments) -> Bool {
            return lhs.resscale == rhs.resscale &&
            lhs.debuglogs == rhs.debuglogs &&
            lhs.tracelogs == rhs.tracelogs &&
            lhs.nintendoinput == rhs.nintendoinput &&
            lhs.enableInternet == rhs.enableInternet &&
            lhs.listinputids == rhs.listinputids &&
            lhs.aspectRatio == rhs.aspectRatio &&
            lhs.memoryManagerMode == rhs.memoryManagerMode &&
            lhs.enableShaderCache == rhs.enableShaderCache &&
            lhs.hypervisor == rhs.hypervisor &&
            lhs.enableDockedMode == rhs.enableDockedMode &&
            lhs.enableTextureRecompression == rhs.enableTextureRecompression &&
            lhs.additionalArgs == rhs.additionalArgs &&
            lhs.maxAnisotropy == rhs.maxAnisotropy &&
            lhs.macroHLE == rhs.macroHLE &&
            lhs.ignoreMissingServices == rhs.ignoreMissingServices &&
            lhs.expandRam == rhs.expandRam &&
            lhs.dfsIntegrityChecks == rhs.dfsIntegrityChecks &&
            lhs.disablePTC == rhs.disablePTC &&
            lhs.disablevsync == rhs.disablevsync &&
            lhs.language == rhs.language &&
            lhs.regioncode == rhs.regioncode
        }
    }
    
    
    func start(with config: Arguments) throws {
        guard state.allowsStart, !runner.isActive else {
            throw RyujinxError.alreadyRunning
        }

        let runID = UUID()
        let args = buildCommandLineArgs(from: config)
        let gameURL = URL(fileURLWithPath: config.gamepath)
        let isAccessingSecurityScopedResource = gameURL.startAccessingSecurityScopedResource()

        self.config = config
        activeRunID = runID
        activeSecurityScopedURL = isAccessingSecurityScopedResource ? gameURL : nil
        executionFailureTask?.cancel()
        executionFailureTask = nil
        lastFailedRunID = nil
        transition(to: .starting, reason: "start requested")
        MetalView.createView()
        RyujinxBridge.prepareEmulationStart()

        let didStart = runner.start(
            operation: {
                RyujinxBridge.mainRyu(argv: args)
            },
            completion: { [weak self] result in
                self?.completeRun(runID: runID, result: result)
            }
        )

        guard didStart else {
            RyujinxBridge.cancelEmulationStart()
            releaseSecurityScopedResource()
            emulationUIView = nil
            metalLayer = nil
            activeRunID = nil
            transition(to: .idle, reason: "runner rejected start", runID: runID)
            throw RyujinxError.alreadyRunning
        }

        transition(to: .running, reason: "mainRyu dispatched")
    }

    private func completeRun(runID: UUID, result: Int) {
        guard activeRunID == runID else {
            return
        }

        stopWatchdog?.cancel()
        stopWatchdog = nil

        if result == RyujinxBridge.teardownIncompleteResult {
            if state != .failed {
                transition(
                    to: .failed,
                    reason: "native teardown did not complete",
                    runID: runID
                )
            }

            presentCleanupFailureAlert(
                title: "Emulation Cleanup Did Not Complete",
                message: "Native cleanup could not finish safely. Restart MeloNX before launching another game."
            )
            return
        }

        dismissCleanupFailureAlert()

        releaseSecurityScopedResource()
        emulationUIView = nil
        metalLayer = nil
        UIApplication.shared.isIdleTimerDisabled = false
        UserDefaults.standard.set(false, forKey: "lockInApp")
        activeRunID = nil

        transition(
            to: .idle,
            reason: "mainRyu completed with code \(result)",
            runID: runID
        )

        if result != 0 {
            lastFailedRunID = runID
            presentExecutionFailure(runID: runID)
        }
    }

    private func releaseSecurityScopedResource() {
        activeSecurityScopedURL?.stopAccessingSecurityScopedResource()
        activeSecurityScopedURL = nil
    }

    private func transition(
        to newState: EmulationState,
        reason: String,
        runID: UUID? = nil
    ) {
        let oldState = state

        guard oldState.canTransition(to: newState) else {
            print(
                "[EmulationLifecycle] Rejected invalid transition "
                    + "\(oldState.rawValue) -> \(newState.rawValue): \(reason)"
            )
            return
        }

        state = newState
        print("[EmulationLifecycle] \(oldState.rawValue) -> \(newState.rawValue): \(reason)")
        MemoryUsageMonitor.logPhysicalFootprint(
            sessionID: runID ?? activeRunID,
            stage: "\(oldState.rawValue)->\(newState.rawValue)"
        )
    }

    private func startStopWatchdog(runID: UUID) {
        stopWatchdog?.cancel()
        stopWatchdog = Task { [weak self] in
            do {
                try await Task.sleep(nanoseconds: 30_000_000_000)
            } catch {
                return
            }

            guard let self,
                  self.activeRunID == runID,
                  self.runner.isActive,
                  self.state == .stopping else {
                return
            }

            self.transition(to: .failed, reason: "stop timed out after 30 seconds")
            self.presentCleanupFailureAlert(
                title: "Emulation Cleanup Timed Out",
                message: "Cleanup has not completed. Restart MeloNX before launching another game."
            )
        }
    }

    private func presentCleanupFailureAlert(title: String, message: String) {
        guard cleanupFailureAlert == nil,
              let rootViewController = AppDelegate.window?.rootViewController else {
            return
        }

        let alert = UIAlertController(title: title, message: message, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "OK", style: .default))
        cleanupFailureAlert = alert
        rootViewController.present(alert, animated: true)
    }

    private func dismissCleanupFailureAlert() {
        guard let alert = cleanupFailureAlert else {
            return
        }

        cleanupFailureAlert = nil
        alert.dismiss(animated: true)
    }

    private func presentExecutionFailure(runID: UUID) {
        executionFailureTask?.cancel()
        executionFailureTask = Task { [weak self] in
            do {
                try await Task.sleep(nanoseconds: 300_000_000)
            } catch {
                return
            }

            guard let self,
                  self.activeRunID == nil,
                  self.lastFailedRunID == runID,
                  self.state == .idle else {
                return
            }

            self.executionFailureTask = nil
            self.lastFailedRunID = nil
            let logs = LogCapture.shared.capturedLogs
            let parsedLogs = self.extractExceptionInfo(logs)

            if let parsedLogs {
                let result = Array(logs.suffix(from: parsedLogs.lineIndex))
                let dateFormatter = DateFormatter()
                dateFormatter.dateFormat = "yyyy-MM-dd_HH-mm-ss"
                let dateString = dateFormatter.string(from: Date())
                let path = URL.documentsDirectory
                    .appendingPathComponent("StackTrace")
                    .appendingPathComponent("StackTrace-\(dateString).txt")
                    .path

                self.saveArrayAsTextFile(strings: result, filePath: path)
                presentCrashAlert(message: parsedLogs.exceptionType + ": " + parsedLogs.message)
            } else {
                presentCrashAlert(message: "Unknown Error")
            }
        }
    }

    private func presentCrashAlert(message: String) {
        presentAlert(title: "MeloNX Execution Failed", message: message, imageName: "sad_mac")
    }
    
    func saveArrayAsTextFile(strings: [String], filePath: String) {
        let text = strings.joined(separator: "\n")
        
        let path = URL.documentsDirectory.appendingPathComponent("StackTrace").path
        
        do {
            try FileManager.default.createDirectory(atPath: path, withIntermediateDirectories: false)
        } catch {
            
        }
        
        do {
            try text.write(to: URL(fileURLWithPath: filePath), atomically: true, encoding: .utf8)
            print("File saved successfully.")
        } catch {
            print("Error saving file: \(error)")
        }
    }
    
    
    static func clearShaderCache(_ titleId: String = "") {
        showAlert(title: "Clear Shader Cache", message: titleId.isEmpty ? "Are you sure you want to clear ALL shader cache?" : "Are you sure you want to clear your shader cache?",
                  actions: [
                    (title: "Cancel", style: .cancel, handler: nil),
                    (title: "Clear", style: .destructive, handler: {
                        if titleId.isEmpty {
                            let fileManager = FileManager.default
                            let gamesURL = URL.documentsDirectory.appendingPathComponent("games")
                            
                            do {
                                let contents = try fileManager.contentsOfDirectory(at: gamesURL, includingPropertiesForKeys: [.isDirectoryKey], options: [.skipsHiddenFiles])
                                
                                let folderURLs = contents.filter { url in
                                    (try? url.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true
                                }
                                
                                for folderURL in folderURLs {
                                    try? fileManager.removeItem(at: folderURL.appendingPathComponent("cache"))
                                }
                                
                            } catch {
                                print("Error reading games folder: \(error)")
                            }
                        } else {
                            let fileManager = FileManager.default
                            let cacheURL = URL.documentsDirectory.appendingPathComponent("games").appendingPathComponent(titleId).appendingPathComponent("cache")
                            
                            try? fileManager.removeItem(at: cacheURL)
                        }
                    }),
                  ]
        )
        
    }
    
    struct ExceptionInfo {
        let exceptionType: String
        let message: String
        let lineIndex: Int
    }
    
    func extractExceptionInfo(_ logs: [String]) -> ExceptionInfo? {
        for i in (0..<logs.count).reversed() {
            let line = logs[i]
            let pattern = "([\\w\\.]+Exception): ([^\\s]+(?:\\s+[^\\s]+)*)"
            
            guard let regex = try? NSRegularExpression(pattern: pattern, options: []),
                  let match = regex.firstMatch(in: line, options: [], range: NSRange(location: 0, length: line.count)) else {
                continue
            }
            
            // Extract exception type and message if pattern matches
            if let exceptionTypeRange = Range(match.range(at: 1), in: line),
               let messageRange = Range(match.range(at: 2), in: line) {
                
                let exceptionType = String(line[exceptionTypeRange])
                
                var message = String(line[messageRange])
                if let atIndex = message.range(of: "\\s+at\\s+", options: .regularExpression) {
                    message = String(message[..<atIndex.lowerBound])
                }
                
                message = message.trimmingCharacters(in: .whitespacesAndNewlines)
                
                return ExceptionInfo(exceptionType: exceptionType, message: message, lineIndex: i)
            }
        }
        
        return nil
    }
    
    
    func stop() throws {
        guard let runID = activeRunID, runner.isActive else {
            throw RyujinxError.notRunning
        }

        switch state {
        case .starting, .running:
            transition(to: .stopping, reason: "stop requested")
            UserDefaults.standard.set(false, forKey: "lockInApp")
            RyujinxBridge.stopEmulation()
            startStopWatchdog(runID: runID)
        case .stopping:
            RyujinxBridge.stopEmulation()
        case .failed:
            // A watchdog failure still owns a live native run. A repeated stop
            // request is safe, but resources remain retained until completion.
            RyujinxBridge.stopEmulation()
        case .idle:
            throw RyujinxError.notRunning
        }
    }
    
    
    func loadGames() -> [Game] {
        let fileManager = FileManager.default
        guard let documentsDirectory = fileManager.urls(for: .documentDirectory, in: .userDomainMask).first else { return [] }
        
        var romdirs: [URL] = [documentsDirectory.appendingPathComponent("roms")]
        let romfoldermanager = ROMFolderManager.shared
        romfoldermanager.loadBookmarks()
        
        for bookmarkData in romfoldermanager.bookmarks {
            var isStale = false
            do {
                let url = try URL(resolvingBookmarkData: bookmarkData,
                                  options: [withSecurityScope],
                                  relativeTo: nil,
                                  bookmarkDataIsStale: &isStale)
                
                if isStale {
                    if fileManager.fileExists(atPath: url.path) {
                        _ = romfoldermanager.addFolder(url: url)
                    }
                }
                
                print(url.path)
                
                if url.startAccessingSecurityScopedResource() {
                    romdirs.append(url)
                }
            } catch {
                print("Failed to resolve bookmark: \(error)")
            }
        }
        
        let originalRom = documentsDirectory.appendingPathComponent("roms")
        if !fileManager.fileExists(atPath: originalRom.path) {
            do {
                try fileManager.createDirectory(at: originalRom, withIntermediateDirectories: true)
            } catch {
                print("Failed to create roms directory: \(error)")
            }
        }
        
        var games: [Game] = []
        
        for romsDirectory in romdirs {
            if let enumerator = fileManager.enumerator(at: romsDirectory, includingPropertiesForKeys: nil) {
                for case let fileURL as URL in enumerator {
                    if !GameFileType.isSupported(fileExtension: fileURL.pathExtension) {
                        continue
                    }
                    
                    do {
                        let handle = try FileHandle(forReadingFrom: fileURL)
                        let fileExtension = (fileURL.pathExtension as NSString)
                        let gameInfo = RyujinxBridge.getGameInfo(arg0: handle.fileDescriptor, arg1: fileExtension)
                        
                        let game = Game.convertGameInfoToGame(gameInfo: gameInfo, url: fileURL)
                        
                        games.append(game)
                    } catch {
                        print("Failed to read file at \(fileURL): \(error)")
                    }
                }
            }
            
            
            // romsDirectory.stopAccessingSecurityScopedResource()
        }
        
        return games
    }
    
    
    func buildCommandLineArgs(from config: Arguments) -> [String] {
        var args: [String] = []
        
        // Add the game path
        args.append(config.gamepath)
        
        // Starts with vulkan
        args.append("--graphics-backend")
        args.append("Vulkan")
        
        args.append(contentsOf: ["--memory-manager-mode", config.memoryManagerMode])
        
        args.append(contentsOf: ["--exclusive-fullscreen", String(true)])
        if self.aspectRatio == .stretched {
            args.append(contentsOf: ["--exclusive-fullscreen-width", "\(Int(UIScreen.main.bounds.width))"])
            args.append(contentsOf: ["--exclusive-fullscreen-height", "\(Int(UIScreen.main.bounds.height))"])
        } else {
            let windowSize = UIApplication.shared.windows.first?.bounds.size ?? UIScreen.main.bounds.size
            let target = targetSize(for: windowSize, ryujinx: self)
            args.append(contentsOf: ["--exclusive-fullscreen-width", "\(Int(target.width))"])
            args.append(contentsOf: ["--exclusive-fullscreen-height", "\(Int(target.height))"])
        }
        
        var model = ""
        
        var systemInfo = utsname()
        uname(&systemInfo)
        let machineMirror = Mirror(reflecting: systemInfo.machine)
        model = machineMirror.children.reduce("") { identifier, element in
            guard let value = element.value as? Int8, value != 0 else { return identifier }
            return identifier + String(UnicodeScalar(UInt8(value)))
        }
        
        args.append(contentsOf: ["--device-model", model])
        
        args.append(contentsOf: ["--device-display-name", UIDevice.modelName])
        
        if checkAppEntitlement("com.apple.developer.kernel.increased-memory-limit") {
            args.append("--has-memory-entitlement")
        }
        
        args.append(contentsOf: ["--system-language", config.language.rawValue])
        
        args.append(contentsOf: ["--system-region", config.regioncode.rawValue])
        
        Task { @MainActor in
            self.aspectRatio = config.aspectRatio
        }
        
        args.append(contentsOf: ["--aspect-ratio", "Stretched"])
        
        args.append(contentsOf: ["--system-timezone", TimeZone.current.identifier])
        
        // args.append(contentsOf: ["--system-time-offset", String(TimeZone.current.secondsFromGMT())])
        
        
        if config.nintendoinput {
            args.append("--correct-controller")
        }
        
        if config.disablePTC {
            args.append("--disable-ptc")
        }
        
        if config.disablevsync {
            args.append("--disable-vsync")
        }
        
        
        if config.hypervisor {
            args.append("--use-hypervisor")
        }
        
        if config.dfsIntegrityChecks {
            args.append("--disable-fs-integrity-checks")
        }
        
        if config.enableInternet {
            args.append("--enable-internet-connection")
        }
        if let index = ldn.firstIndex(of: ":") {
            let result = String(ldn[..<index])
            
            args.append(contentsOf: ["--lan-interface-id", result])
        }
        
        if config.ldn_mitm {
            args.append("--enable-ldn-mitm")
        }
        
        // ldn
        
        if config.resscale != 1.0 {
            args.append(contentsOf: ["--resolution-scale", String(config.resscale)])
        }
        
        if config.expandRam {
            args.append(contentsOf: ["--expand-ram", String(config.expandRam)])
        }
        
        if config.ignoreMissingServices {
            // args.append(contentsOf: ["--ignore-missing-services"])
            args.append("--ignore-missing-services")
        }
        
        if config.maxAnisotropy != 0 {
            args.append(contentsOf: ["--max-anisotropy", String(config.maxAnisotropy)])
        }
        
        if !config.macroHLE {
            args.append("--disable-macro-hle")
        }
        
        // Finally fixed these by replacing disable with enable.
        if !config.enableShaderCache {
            args.append("--disable-shader-cache")
        }
        
        if !config.enableDockedMode {
            args.append("--disable-docked-mode")
        }
        if config.enableTextureRecompression {
            // args.append("--enable-texture-recompression")
        }
        
        if config.debuglogs {
            args.append("--enable-debug-logs")
        }
        if config.tracelogs {
            args.append("--enable-trace-logs")
        }
        
        // List the input ids
        if config.listinputids {
            args.append("--list-inputs-ids")
        }
        
        // Append the input ids (limit to 8 (used to be 4) just in case)
        if !config.inputids.isEmpty {
            for (index, inputId) in config.inputids.prefix(8).enumerated() {
                if args.contains(inputId) { continue }
                
                // controllerType
                if let controller = controllerManager.controllerForString(inputId) {
                    controller.setupController()
                    if controller.type == .handheld {
                        args.append(contentsOf: ["--input-id-handheld", inputId])
                    } else {
                        args.append(contentsOf: ["--input-id-\(index + 1)", inputId])
                    }
                    
                    args.append(contentsOf: ["--controller-type-\(index + 1)", controller.type.rawValue])
                } else if inputId == "0" {
                    args.append(contentsOf: ["--input-id-\(index + 1)", inputId])
                    args.append(contentsOf: ["--controller-type-\(index + 1)", ControllerType.proController.rawValue])
                }
            }
        }
        
        args.append(contentsOf: config.additionalArgs)
        
        return args
    }
    
    func reloadControllersWithInfo() {
        var args: [String] = []
        
        if !controllerManager.selectedControllers.isEmpty {
            for (index, inputId) in controllerManager.selectedControllers.prefix(8).enumerated() {
                if args.contains(inputId) { continue }
                
                // controllerType
                if let controller = controllerManager.controllerForString(inputId) {
                    controller.setupController()
                    if controller.type == .handheld {
                        args.append(contentsOf: ["--input-id-handheld", inputId])
                    } else {
                        args.append(contentsOf: ["--input-id-\(index + 1)", inputId])
                    }
                    
                    args.append(contentsOf: ["--controller-type-\(index + 1)", controller.type.rawValue])
                } else if inputId == "0" {
                    args.append(contentsOf: ["--input-id-\(index + 1)", inputId])
                    args.append(contentsOf: ["--controller-type-\(index + 1)", ControllerType.proController.rawValue])
                }
            }
        } else {
            args.append(contentsOf: ["--input-id-1", "0"])
            args.append(contentsOf: ["--controller-type-1", ControllerType.proController.rawValue])
        }
        
        RyujinxBridge.changeControllerInfo(argv: args)
    }
    
    
    func checkIfKeysImported() -> Bool {
        let keysDirectory = URL.documentsDirectory.appendingPathComponent("system")
        let keysFile = keysDirectory.appendingPathComponent("prod.keys")

        return FileManager.default.fileExists(atPath: keysFile.path)
    }
    
    func fetchFirmwareVersion() -> String {
        if isRunning {
            return "1"
        }
        
        let firmwareVersionPointer = RyujinxBridge.installedFirmwareVersion

        return firmwareVersionPointer.isEmpty ? "0" : firmwareVersionPointer
    }
    

    func getDlcNcaList(titleId: String, path: String) -> [DownloadableContentNca] {

        let listPointer = RyujinxBridge.getDlcList(titleId: titleId, path: path)//get_dlc_nca_list(titleIdCString, pathCString)
        // print("DLC parcing success: \(listPointer.success)")
        guard listPointer.success else { return [] }

        let list = Array(UnsafeBufferPointer(start: listPointer.items, count: Int(listPointer.size)))

        return list.map { item in
                .init(fullPath: withUnsafePointer(to: item.Path) {
                    $0.withMemoryRebound(to: UInt8.self, capacity: MemoryLayout.size(ofValue: $0)) {
                        String(cString: $0)
                    }
                }, titleId: item.TitleId, enabled: true)
        }
    }
    
    func removeFirmware() {
        let fileManager = FileManager.default
        
        let documentsfolder = fileManager.urls(for: .documentDirectory, in: .userDomainMask).first!
        
        
        let bisFolder = documentsfolder.appendingPathComponent("bis")
        let systemFolder = bisFolder.appendingPathComponent("system")
        let contentsFolder = systemFolder.appendingPathComponent("Contents")
        let registeredFolder = contentsFolder.appendingPathComponent("registered").path
        
    
        do {
            if fileManager.fileExists(atPath: registeredFolder) {
                try fileManager.removeItem(atPath: registeredFolder)
                // print("Folder removed successfully.")
                let version = fetchFirmwareVersion()
                
                if version.isEmpty {
                    self.firmwareversion = "0"
                } else {
                    // print("Firmware eeeeee \(version)")
                }
                
            } else {
                // print("Folder does not exist.")
            }
        } catch {
            // print("Error removing folder: \(error)")
        }
    }
    


    static func log(_ message: String) {
        // print("[Ryujinx] \(message)")
    }
    
    public func updateOrientation() -> Bool {
        if let window = AppDelegate.window {
            return (window.bounds.size.height > window.bounds.size.width)
        }
        return false
    }
    
    nonisolated func checkForJIT() {
        let isEnabled = isJITEnabled()
        Task { @MainActor [weak self] in
            self?.jitenabled = isEnabled
        }
    }
}


public extension UIDevice {
    static let modelName: String = {
        var systemInfo = utsname()
        uname(&systemInfo)
        let machineMirror = Mirror(reflecting: systemInfo.machine)
        let identifier = machineMirror.children.reduce("") { identifier, element in
            guard let value = element.value as? Int8, value != 0 else { return identifier }
            return identifier + String(UnicodeScalar(UInt8(value)))
        }

        return CallMGCopyAnswer(kMGPhysicalHardwareNameString)?.takeUnretainedValue() as? String ?? identifier
    }()

}
