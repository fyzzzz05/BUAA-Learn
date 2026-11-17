import Foundation
import Darwin

private typealias AnalysisSend = @convention(c) (UnsafeMutableRawPointer?, UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>?
private typealias AnalysisRelease = @convention(c) (UnsafeMutableRawPointer?, UnsafeMutablePointer<CChar>?) -> Void
private typealias AnalysisProgress = @convention(c) (UnsafeMutableRawPointer?, UnsafePointer<CChar>?) -> Void
@_silgen_name("buaa_core_extract_chunk")
private func extractChunkABI(_ context: UnsafeMutableRawPointer?, _ send: AnalysisSend?, _ release: AnalysisRelease?, _ prompt: UnsafePointer<CChar>?, _ evidence: UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>?
@_silgen_name("buaa_core_analyze_lesson")
private func analyzeLessonABI(_ context: UnsafeMutableRawPointer?, _ send: AnalysisSend?, _ release: AnalysisRelease?, _ progress: AnalysisProgress?, _ input: UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>?

private final class AnalysisResponse: @unchecked Sendable { var value: String? }
private final class AnalysisContext: @unchecked Sendable {
    let model: ModelService
    let progress: @Sendable (String) -> Void
    init(model: ModelService, progress: @escaping @Sendable (String) -> Void) { self.model = model; self.progress = progress }
}
private let analysisSendCallback: AnalysisSend = { context, request in
    guard let context, let request else { return nil }
    let model = Unmanaged<AnalysisContext>.fromOpaque(context).takeUnretainedValue().model
    let raw = String(cString: request)
    let box = AnalysisResponse(); let done = DispatchSemaphore(value: 0)
    let task = Task.detached {
        defer { done.signal() }
        guard let bytes = raw.data(using: .utf8), let data = try? JSONSerialization.jsonObject(with: bytes) as? [String:Any],
              let system = data["system"] as? String, let prompt = data["prompt"] as? String,
              let schema = data["schema"] as? [String:Any] else { return }
        do {
            let result = try await model.complete(system: system, prompt: prompt, schema: schema)
            let bytes = try JSONSerialization.data(withJSONObject: ["ok":true,"data":result])
            box.value = String(data: bytes, encoding: .utf8)
        } catch {
            let description = (error as? ProbeError)?.localizedDescription ?? ""
            let code: String
            if description.contains("不是有效 JSON") { code = "invalid_json" }
            else if description.contains("响应无效") { code = "invalid_response" }
            else if description.contains("未完成") { code = "incomplete" }
            else if description.contains("拒绝") { code = "refused" }
            else { code = "failed" }
            box.value = #"{"ok":false,"code":"\#(code)"}"#
        }
    }
    guard done.wait(timeout: .now() + 195) == .success else { task.cancel(); return nil }
    guard let value = box.value else { return nil }
    return value.withCString { strdup($0) }
}
private let analysisReleaseCallback: AnalysisRelease = { _, value in free(value) }
private let analysisProgressCallback: AnalysisProgress = { context, message in
    guard let context, let message else { return }
    let host = Unmanaged<AnalysisContext>.fromOpaque(context).takeUnretainedValue()
    let text = String(cString: message)
    DispatchQueue.main.async { host.progress(text) }
}

/// Runs validated chunk extraction off the UI thread; no model key is persisted.
final class NativeAnalysis: @unchecked Sendable {
    private let queue = DispatchQueue(label: "buaa.native.analysis")
    private let context: UnsafeMutableRawPointer
    init(model: ModelService, progress: @escaping @Sendable (String) -> Void = { _ in }) {
        context = Unmanaged.passRetained(AnalysisContext(model: model, progress: progress)).toOpaque()
    }
    deinit { Unmanaged<AnalysisContext>.fromOpaque(context).release() }
    func extract(prompt: String, evidence: [[String:Any]]) async throws -> [String:Any] {
        let bytes = try JSONSerialization.data(withJSONObject: evidence)
        guard let json = String(data: bytes, encoding: .utf8) else { throw ProbeError.message("课堂文本格式异常。") }
        return try await withCheckedThrowingContinuation { continuation in
            queue.async { [self] in
                do {
                    let result = try prompt.withCString { prompt in try json.withCString { evidence in
                        try CoreBridge.decode(extractChunkABI(context, analysisSendCallback, analysisReleaseCallback, prompt, evidence))
                    } }
                    guard let result = result as? [String:Any] else { throw ProbeError.message("课堂分析格式异常。") }
                    continuation.resume(returning: result)
                } catch { continuation.resume(throwing: error) }
            }
        }
    }
    func analyze(input: [String:Any]) async throws -> [String:Any] {
        let bytes = try JSONSerialization.data(withJSONObject: input)
        guard let json = String(data: bytes, encoding: .utf8) else { throw ProbeError.message("课堂材料格式异常。") }
        return try await withCheckedThrowingContinuation { continuation in
            queue.async { [self] in
                do {
                    let result = try json.withCString { input in
                        try CoreBridge.decode(analyzeLessonABI(context, analysisSendCallback, analysisReleaseCallback, analysisProgressCallback, input))
                    }
                    guard let result = result as? [String:Any] else { throw ProbeError.message("课堂分析格式异常。") }
                    continuation.resume(returning: result)
                } catch { continuation.resume(throwing: error) }
            }
        }
    }
}
