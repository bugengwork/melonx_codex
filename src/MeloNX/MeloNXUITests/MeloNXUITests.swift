//
//  MeloNXUITests.swift
//  MeloNXUITests
//
//  Created by Stossy11 on 3/11/2024.
//

import XCTest

final class MeloNXUITests: XCTestCase {

    override func setUpWithError() throws {
        // Put setup code here. This method is called before the invocation of each test method in the class.

        // In UI tests it is usually best to stop immediately when a failure occurs.
        continueAfterFailure = false

        // In UI tests it’s important to set the initial state - such as interface orientation - required for your tests before they run. The setUp method is a good place to do this.
    }

    override func tearDownWithError() throws {
        // Put teardown code here. This method is called after the invocation of each test method in the class.
    }

    @MainActor
    func testExample() throws {
        // UI tests must launch the application that they test.
        let app = XCUIApplication()
        app.launch()

        // Use XCTAssert and related functions to verify your tests produce the correct results.
    }

    @MainActor
    func testLaunchPerformance() throws {
        if #available(macOS 10.15, iOS 13.0, tvOS 13.0, watchOS 7.0, *) {
            // This measures how long it takes to launch your application.
            measure(metrics: [XCTApplicationLaunchMetric()]) {
                XCUIApplication().launch()
            }
        }
    }

    @MainActor
    func testLifecycleStressTwentyCyclesOnPhysicalDevice() throws {
#if targetEnvironment(simulator)
        throw XCTSkip("Lifecycle stress acceptance requires a physical iOS device.")
#else
        let app = XCUIApplication()
        app.launchEnvironment[
            "MELONX_LIFECYCLE_STRESS"
        ] = "1"

        if let requestedROM = ProcessInfo.processInfo.environment[
            "MELONX_LIFECYCLE_STRESS_ROM"
        ] {
            app.launchEnvironment[
                "MELONX_LIFECYCLE_STRESS_ROM"
            ] = requestedROM
        }

        app.launch()

        let status = app.staticTexts["lifecycle-stress-status"]
        XCTAssertTrue(
            status.waitForExistence(timeout: 60),
            "The opt-in lifecycle stress view did not appear."
        )

        let terminalState = NSPredicate(
            format: "label == %@ OR label == %@",
            "PASS",
            "FAIL"
        )
        let terminalExpectation = XCTNSPredicateExpectation(
            predicate: terminalState,
            object: status
        )
        let waitResult = XCTWaiter.wait(
            for: [terminalExpectation],
            timeout: 1_800
        )

        let summary = app.staticTexts[
            "lifecycle-stress-summary"
        ].label
        let reportPath = app.staticTexts[
            "lifecycle-stress-report-path"
        ].label

        XCTAssertEqual(
            waitResult,
            .completed,
            "Lifecycle stress did not finish within 30 minutes. \(summary)"
        )
        XCTAssertEqual(
            status.label,
            "PASS",
            "Lifecycle stress failed. \(summary) Report: \(reportPath)"
        )
#endif
    }
}
