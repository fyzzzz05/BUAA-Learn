// swift-tools-version: 5.9
import PackageDescription
import Foundation

let packageRoot = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
let rustLib = packageRoot.deletingLastPathComponent().appendingPathComponent("core/target/release").path

let package = Package(
    name: "BUAALoginProbe",
    platforms: [.macOS(.v13)],
    targets: [
        .executableTarget(name: "BUAALoginProbe", linkerSettings: [
            // Select the archive explicitly: the Windows bridge also builds a cdylib.
            // -lbuaa_core would prefer the dylib and require shipping it in the app.
            .unsafeFlags(["\(rustLib)/libbuaa_core.a"]), .linkedLibrary("sqlite3"),
            .linkedFramework("Security"), .linkedFramework("CoreFoundation")
        ]),
        .testTarget(name: "BUAALoginProbeTests", dependencies: ["BUAALoginProbe"]),
    ]
)
