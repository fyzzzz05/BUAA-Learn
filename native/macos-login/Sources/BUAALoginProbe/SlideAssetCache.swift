import Foundation

@MainActor
enum SlideAssetCache {
    static func load(url: URL, owner: String, store: NativeStore,
                     fetch: (URL) async throws -> Data = { try await SlideDownload().get($0) }) async throws -> Data {
        _ = try SlideInput(["url":url.absoluteString,"start":0])
        if let saved = try store.slideData(owner:owner,url:url) { return saved }
        let data = try await fetch(url)
        try Task.checkCancellation()
        _ = try SlideExporter.decode(data)
        try store.saveSlide(owner:owner,url:url,data:data)
        return data
    }
    static func acquire(rows: [[String:Any]], owner: String, store: NativeStore) async throws -> Int {
        guard rows.count <= 1000 else { throw ProbeError.message("PPT 截图超过 1000 张，请检查课堂材料。") }
        var missing = 0
        var consecutiveFailures = 0
        for row in rows {
            try Task.checkCancellation()
            do {
                let input = try SlideInput(row)
                _ = try await load(url:input.url,owner:owner,store:store, fetch:{ url in
                    guard consecutiveFailures < 3 else { throw ProbeError.message("图片连接暂时不可用，请稍后刷新材料。") }
                    return try await SlideDownload().get(url)
                })
                consecutiveFailures = 0
            } catch is CancellationError { throw CancellationError() }
              catch { missing += 1; consecutiveFailures += 1 }
        }
        return missing
    }
}
