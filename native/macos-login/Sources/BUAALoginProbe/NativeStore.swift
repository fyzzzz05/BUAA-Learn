import Foundation
import SQLite3

@_silgen_name("buaa_store_open")
private func storeOpen(_ path: UnsafePointer<CChar>?) -> OpaquePointer?
@_silgen_name("buaa_store_close")
private func storeClose(_ handle: OpaquePointer?)
@_silgen_name("buaa_store_put")
private func storePut(_ handle: OpaquePointer?, _ owner: UnsafePointer<CChar>?, _ kind: UnsafePointer<CChar>?, _ id: UnsafePointer<CChar>?, _ body: UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>?
@_silgen_name("buaa_store_merge_school")
private func storeMergeSchool(_ handle: OpaquePointer?, _ owner: UnsafePointer<CChar>?, _ body: UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>?
@_silgen_name("buaa_store_get")
private func storeGet(_ handle: OpaquePointer?, _ owner: UnsafePointer<CChar>?, _ kind: UnsafePointer<CChar>?, _ id: UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>?
@_silgen_name("buaa_store_list")
private func storeList(_ handle: OpaquePointer?, _ owner: UnsafePointer<CChar>?, _ kind: UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>?
@_silgen_name("buaa_store_save_analysis")
private func storeSaveAnalysis(_ handle: OpaquePointer?, _ owner: UnsafePointer<CChar>?, _ analysis: UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>?
@_silgen_name("buaa_store_save_analysis_checked")
private func storeSaveAnalysisChecked(_ handle: OpaquePointer?, _ owner: UnsafePointer<CChar>?, _ analysis: UnsafePointer<CChar>?, _ digest: UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>?

/// Native hosts choose the path; this adapter never opens or migrates old data itself.
final class NativeStore {
    private let handle: OpaquePointer
    private var assets: OpaquePointer?
    let directory: URL?
    static func applicationDirectory(baseDirectory: URL? = nil) throws -> URL {
        let base = try baseDirectory ?? FileManager.default.url(for: .applicationSupportDirectory, in: .userDomainMask, appropriateFor: nil, create: true)
        let directory = base.appendingPathComponent("BUAA Learn Native", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        return directory
    }
    static func openApplicationStore(baseDirectory: URL? = nil) throws -> NativeStore {
        let directory = try applicationDirectory(baseDirectory: baseDirectory)
        return try NativeStore(url: directory.appendingPathComponent("learning.sqlite3"))
    }
    static func rememberedAccount(baseDirectory: URL? = nil) throws -> String? {
        let url = try applicationDirectory(baseDirectory: baseDirectory).appendingPathComponent("last-account.json")
        guard FileManager.default.fileExists(atPath: url.path) else { return nil }
        let value = try JSONDecoder().decode(String.self, from: Data(contentsOf: url))
        guard !value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { throw ProbeError.message("本地账号记录无效，请重新登录。") }
        return value
    }
    static func rememberAccount(_ account: String, baseDirectory: URL? = nil) throws {
        guard !account.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { throw ProbeError.message("学校账号无效。") }
        let url = try applicationDirectory(baseDirectory: baseDirectory).appendingPathComponent("last-account.json")
        try JSONEncoder().encode(account).write(to: url, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    }

    init(url: URL) throws {
        guard url.isFileURL, let handle = url.path.withCString({ storeOpen($0) }) else {
            throw ProbeError.message("无法打开本地数据库。")
        }
        self.handle = handle
        self.directory = url.deletingLastPathComponent()
        try openAssets(path:url.path)
    }
    /// Isolated session storage for the development probe; it never touches user files.
    init(inMemory: Void) throws {
        guard let handle = ":memory:".withCString({ storeOpen($0) }) else { throw ProbeError.message("无法打开临时数据库。") }
        self.handle = handle
        self.directory = nil
        try openAssets(path:":memory:")
    }
    deinit { if let assets { sqlite3_close(assets) }; storeClose(handle) }
    private func openAssets(path: String) throws {
        guard sqlite3_open_v2(path, &assets, SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_FULLMUTEX, nil) == SQLITE_OK,
              let assets else { throw ProbeError.message("无法打开本地图片缓存。") }
        sqlite3_busy_timeout(assets,5000)
        guard sqlite3_exec(assets,"CREATE TABLE IF NOT EXISTS slide_assets(owner TEXT NOT NULL,url TEXT NOT NULL,body BLOB NOT NULL,PRIMARY KEY(owner,url));",nil,nil,nil) == SQLITE_OK else {
            throw ProbeError.message("无法创建本地图片缓存。")
        }
    }
    func slideData(owner: String, url: URL) throws -> Data? {
        guard !owner.isEmpty, let assets else { throw ProbeError.message("本地图片缓存不可用。") }
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(assets,"SELECT body FROM slide_assets WHERE owner=? AND url=?",-1,&statement,nil) == SQLITE_OK else { throw ProbeError.message("无法读取本地图片。") }
        defer { sqlite3_finalize(statement) }
        let transient = unsafeBitCast(-1, to:sqlite3_destructor_type.self)
        sqlite3_bind_text(statement,1,owner,-1,transient); sqlite3_bind_text(statement,2,url.absoluteString,-1,transient)
        let step = sqlite3_step(statement)
        if step == SQLITE_DONE { return nil }
        guard step == SQLITE_ROW, let bytes = sqlite3_column_blob(statement,0) else { throw ProbeError.message("无法读取本地图片。") }
        let size = Int(sqlite3_column_bytes(statement,0))
        guard size > 0, size <= 12*1024*1024 else { throw ProbeError.message("本地图片大小异常。") }
        return Data(bytes:bytes,count:size)
    }
    func saveSlide(owner: String, url: URL, data: Data) throws {
        guard !owner.isEmpty, !data.isEmpty, data.count <= 12*1024*1024, let assets else { throw ProbeError.message("图片缓存大小或账号无效。") }
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(assets,"INSERT OR REPLACE INTO slide_assets(owner,url,body) VALUES(?,?,?)",-1,&statement,nil) == SQLITE_OK else { throw ProbeError.message("无法保存本地图片。") }
        defer { sqlite3_finalize(statement) }
        let transient = unsafeBitCast(-1,to:sqlite3_destructor_type.self)
        sqlite3_bind_text(statement,1,owner,-1,transient); sqlite3_bind_text(statement,2,url.absoluteString,-1,transient)
        _ = data.withUnsafeBytes { sqlite3_bind_blob(statement,3,$0.baseAddress,Int32(data.count),transient) }
        guard sqlite3_step(statement) == SQLITE_DONE else { throw ProbeError.message("无法保存本地图片，请检查可用磁盘空间。") }
    }
    func put(owner: String, kind: String, id: String, value: Any) throws {
        let data = try JSONSerialization.data(withJSONObject: value, options: [.fragmentsAllowed])
        guard let body = String(data: data, encoding: .utf8) else { throw ProbeError.message("本地数据格式异常。") }
        try owner.withCString { owner in try kind.withCString { kind in try id.withCString { id in try body.withCString { body in
            _ = try CoreBridge.decode(storePut(handle, owner, kind, id, body))
        } } } }
    }
    func mergeSchool(owner: String, records: [[String:Any]]) throws {
        let bytes = try JSONSerialization.data(withJSONObject: records)
        guard let json = String(data: bytes, encoding: .utf8) else { throw ProbeError.message("学校数据格式异常。") }
        try owner.withCString { owner in try json.withCString { json in
            _ = try CoreBridge.decode(storeMergeSchool(handle, owner, json))
        } }
    }
    func get(owner: String, kind: String, id: String) throws -> Any? {
        let result = try owner.withCString { owner in try kind.withCString { kind in try id.withCString { id in
            try CoreBridge.decode(storeGet(handle, owner, kind, id))
        } } }
        return result is NSNull ? nil : result
    }
    func list(owner: String, kind: String) throws -> [Any] {
        let result = try owner.withCString { owner in try kind.withCString { kind in
            try CoreBridge.decode(storeList(handle, owner, kind))
        } }
        guard let rows = result as? [Any] else { throw ProbeError.message("本地数据格式异常。") }
        return rows
    }
    func saveAnalysis(owner: String, analysis: [String:Any], expectedDigest: String? = nil) throws -> [[String:Any]] {
        let bytes = try JSONSerialization.data(withJSONObject: analysis)
        guard let json = String(data: bytes, encoding: .utf8) else { throw ProbeError.message("课堂分析格式异常。") }
        let result = try owner.withCString { owner in try json.withCString { analysis in
            if let expectedDigest {
                return try expectedDigest.withCString { try CoreBridge.decode(storeSaveAnalysisChecked(handle, owner, analysis, $0)) }
            }
            return try CoreBridge.decode(storeSaveAnalysis(handle, owner, analysis))
        } }
        guard let tasks = result as? [[String:Any]] else { throw ProbeError.message("课堂任务格式异常。") }
        return tasks
    }
}
