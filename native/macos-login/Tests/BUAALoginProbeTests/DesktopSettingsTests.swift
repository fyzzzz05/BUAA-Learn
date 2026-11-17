import CryptoKit
import Foundation
import XCTest
@testable import BUAALoginProbe

final class DesktopSettingsTests: XCTestCase {
    func testLegacyVersionComparesToThreePartRelease() throws {
        XCTAssertEqual(try NativeDesktopSettings.versionParts("0.1"), [0, 1, 0])
        XCTAssertTrue(try NativeDesktopSettings.versionParts("0.2.9")
            .lexicographicallyPrecedes(NativeDesktopSettings.versionParts("0.2.10")))
        XCTAssertThrowsError(try NativeDesktopSettings.versionParts("0.2", requirePatch: true))
        XCTAssertThrowsError(try NativeDesktopSettings.versionParts("../0.2"))
    }

    func testMissingAndModifiedInstallerAreRejected() async throws {
        let path = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: path) }
        let data = Data("test installer".utf8)
        let digest = SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
        let release = NativeDesktopRelease(version: "0.2.4", size: Int64(data.count), sha256: digest, notes: nil)
        let missing = try await NativeDesktopSettings.verified(path, release: release)
        XCTAssertFalse(missing)
        try data.write(to: path)
        let verified = try await NativeDesktopSettings.verified(path, release: release)
        XCTAssertTrue(verified)
        try Data(repeating: 0, count: data.count).write(to: path)
        let modified = try await NativeDesktopSettings.verified(path, release: release)
        XCTAssertFalse(modified)
    }
}
