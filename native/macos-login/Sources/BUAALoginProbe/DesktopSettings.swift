import AppKit
import CryptoKit
import Foundation
import SwiftUI

private let updateFeed = URL(string: "https://raw.githubusercontent.com/fyzzzz05/BUAA-Learn/desktop-update-feed/updates.json")!

struct NativeDesktopRelease: Decodable {
    let version: String
    let size: Int64
    let sha256: String
    let notes: String?
}

struct NativeDesktopFeed: Decodable {
    let schema: Int
    let releases: [String: NativeDesktopRelease]
}

private struct DesktopSettingsError: LocalizedError {
    let errorDescription: String?
    init(_ message: String) { errorDescription = message }
}

private final class DesktopReleaseRedirects: NSObject, URLSessionTaskDelegate {
    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest,
                    completionHandler: @escaping (URLRequest?) -> Void) {
        let hosts = ["release-assets.githubusercontent.com", "objects.githubusercontent.com"]
        guard task.originalRequest?.url?.host == "github.com", let url = request.url, url.scheme == "https", let host = url.host, hosts.contains(host),
              url.port == nil, url.user == nil, url.password == nil else {
            completionHandler(nil)
            return
        }
        completionHandler(request)
    }
}

enum DesktopDiagnostics {
    static func file(in directory: URL? = nil) throws -> URL {
        let root = try directory ?? NativeStore.applicationDirectory()
        let url = root.appendingPathComponent("desktop.log")
        if !FileManager.default.fileExists(atPath: url.path) {
            try Data().write(to: url, options: .atomic)
        }
        return url
    }

    @MainActor
    static func record(_ event: String, in directory: URL? = nil) {
        guard let url = try? file(in: directory), let handle = try? FileHandle(forWritingTo: url) else { return }
        defer { try? handle.close() }
        do {
            if try handle.seekToEnd() > 256 * 1024 { try handle.truncate(atOffset: 0) }
            let stamp = ISO8601DateFormatter().string(from: Date())
            try handle.write(contentsOf: Data("\(stamp) \(event)\n".utf8))
        } catch {
            // Diagnostics must never interrupt course or update operations.
        }
    }
}

@MainActor
final class NativeDesktopSettings: ObservableObject {
    enum Phase { case idle, checking, current, available, downloading, ready }
    let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.0.0"
    @Published private(set) var phase: Phase = .idle
    @Published private(set) var release: NativeDesktopRelease?
    @Published private(set) var message = ""
    private var operation: Task<Void, Never>?
    private var didCheck = false

    private let network = URLSession(configuration: .ephemeral)
    private let redirects = DesktopReleaseRedirects()

    private var target: String {
        #if arch(arm64)
        return "mac-native-arm64.dmg"
        #else
        return "mac-native-x64.dmg"
        #endif
    }

    func checkOnce() {
        guard !didCheck else { return }
        check()
    }

    func check() {
        guard operation == nil else { return }
        didCheck = true
        message = ""
        phase = .checking
        operation = Task {
            defer { operation = nil }
            do {
                var request = URLRequest(url: updateFeed, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 15)
                request.httpShouldHandleCookies = false
                let (data, response) = try await network.data(for: request, delegate: redirects)
                guard (response as? HTTPURLResponse)?.statusCode == 200, response.url == updateFeed else {
                    throw DesktopSettingsError("暂时无法获取更新，请稍后重试。")
                }
                let feed = try JSONDecoder().decode(NativeDesktopFeed.self, from: data)
                guard feed.schema == 1 else { throw DesktopSettingsError("更新信息格式异常。") }
                guard let next = feed.releases[target] else {
                    release = nil; phase = .current; return
                }
                let available = try Self.versionParts(next.version, requirePatch: true)
                let current = try Self.versionParts(version)
                guard current.lexicographicallyPrecedes(available) else {
                    release = nil; phase = .current; return
                }
                guard next.size > 0, next.size <= 1_073_741_824,
                      next.sha256.range(of: "^[a-f0-9]{64}$", options: .regularExpression) != nil else {
                    throw DesktopSettingsError("安装包的校验信息不完整。")
                }
                release = next
                phase = try await Self.verified(cacheFile(next), release: next) ? .ready : .available
            } catch {
                phase = .idle
                message = (error as? DesktopSettingsError)?.errorDescription ?? "无法检查更新，请稍后重试。"
                DesktopDiagnostics.record("update-check-failed")
            }
        }
    }

    func download() {
        guard phase == .available, operation == nil, let release else { return }
        phase = .downloading
        message = ""
        operation = Task {
            defer { operation = nil }
            do {
                let installer = try cacheFile(release)
                let url = URL(string: "https://github.com/fyzzzz05/BUAA-Learn/releases/download/desktop-v\(release.version)/\(installer.lastPathComponent)")!
                let (temporary, response) = try await network.download(from: url, delegate: redirects)
                defer { try? FileManager.default.removeItem(at: temporary) }
                try Task.checkCancellation()
                guard (response as? HTTPURLResponse)?.statusCode == 200,
                      try await Self.verified(temporary, release: release) else {
                    throw DesktopSettingsError("安装包校验失败，请重新下载。")
                }
                try Task.checkCancellation()
                let directory = installer.deletingLastPathComponent()
                try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
                if FileManager.default.fileExists(atPath: installer.path) { try FileManager.default.removeItem(at: installer) }
                try FileManager.default.moveItem(at: temporary, to: installer)
                phase = .ready
            } catch {
                phase = .available
                if !Task.isCancelled {
                    message = (error as? DesktopSettingsError)?.errorDescription ?? "下载失败，请稍后重试。"
                    DesktopDiagnostics.record("update-download-failed")
                }
            }
        }
    }

    func cancelDownload() { if phase == .downloading { operation?.cancel() } }

    func openInstaller() {
        guard phase == .ready, operation == nil, let release else { return }
        message = ""
        operation = Task {
            defer { operation = nil }
            do {
                let installer = try cacheFile(release)
                guard try await Self.verified(installer, release: release) else {
                    phase = .available
                    throw DesktopSettingsError("安装包校验失败，请重新下载。")
                }
                guard NSWorkspace.shared.open(installer) else {
                    throw DesktopSettingsError("无法打开安装包，请稍后重试。")
                }
            } catch { message = (error as? DesktopSettingsError)?.errorDescription ?? "无法打开安装包。" }
        }
    }

    private func cacheFile(_ release: NativeDesktopRelease) throws -> URL {
        try NativeStore.applicationDirectory().appendingPathComponent("desktop-update-cache", isDirectory: true)
            .appendingPathComponent("BUAA-Learn-\(release.version)-\(target)")
    }

    nonisolated static func versionParts(_ version: String, requirePatch: Bool = false) throws -> [Int] {
        let parts = version.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 3 || (!requirePatch && parts.count == 2),
              parts.allSatisfy({ $0.range(of: "^(0|[1-9][0-9]{0,8})$", options: .regularExpression) != nil }) else {
            throw DesktopSettingsError("更新版本格式异常。")
        }
        let numbers = parts.compactMap { Int($0) }
        return numbers + Array(repeating: 0, count: 3 - numbers.count)
    }

    nonisolated static func verified(_ file: URL, release: NativeDesktopRelease) async throws -> Bool {
        try await Task.detached(priority: .utility) {
            let attributes: [FileAttributeKey: Any]
            do { attributes = try FileManager.default.attributesOfItem(atPath: file.path) }
            catch let error as NSError where error.domain == NSCocoaErrorDomain
                && [NSFileNoSuchFileError, NSFileReadNoSuchFileError].contains(error.code) { return false }
            guard attributes[.type] as? FileAttributeType == .typeRegular,
                  (attributes[.size] as? NSNumber)?.int64Value == release.size else { return false }
            let handle = try FileHandle(forReadingFrom: file)
            defer { try? handle.close() }
            var hash = SHA256()
            while let bytes = try handle.read(upToCount: 1024 * 1024), !bytes.isEmpty { hash.update(data: bytes) }
            return hash.finalize().map { String(format: "%02x", $0) }.joined() == release.sha256
        }.value
    }
}
