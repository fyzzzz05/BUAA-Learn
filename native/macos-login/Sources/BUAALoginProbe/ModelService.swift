import Foundation

@_silgen_name("buaa_core_model_request")
private func modelRequestABI(_ provider: UnsafePointer<CChar>?, _ model: UnsafePointer<CChar>?, _ system: UnsafePointer<CChar>?, _ prompt: UnsafePointer<CChar>?, _ schema: UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>?
@_silgen_name("buaa_core_model_parse")
private func modelParseABI(_ provider: UnsafePointer<CChar>?, _ body: UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>?

private final class ModelRedirectPolicy: NSObject, URLSessionTaskDelegate {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
    }
}

/// API keys remain in memory and provider responses are validated by the shared core.
final class ModelService: @unchecked Sendable {
    private let endpoint: URL
    private let provider: String
    private let model: String
    private let key: String
    private let session: URLSession

    init(baseURL: String, model: String, key: String = "", session: URLSession? = nil) throws {
        guard var components = URLComponents(string: baseURL), let host = components.host?.lowercased(), !host.isEmpty,
              components.user == nil, components.password == nil, components.query == nil, components.fragment == nil,
              !model.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw ProbeError.message("请填写有效的模型地址和名称。")
        }
        let scheme = components.scheme?.lowercased()
        guard scheme == "https" || (scheme == "http" && ["localhost", "127.0.0.1", "::1", "[::1]"].contains(host)) else {
            throw ProbeError.message("模型地址请使用 HTTPS；本地模型可使用 localhost HTTP。")
        }
        let basePath = components.path.replacingOccurrences(of: #"/+$"#, with: "", options: .regularExpression)
        self.provider = host == "api.deepseek.com" && ["", "/v1"].contains(basePath) ? "responses" : "chat"
        components.path = basePath + (self.provider == "responses" ? "/responses" : "/chat/completions")
        guard let endpoint = components.url else { throw ProbeError.message("模型地址无效。") }
        self.endpoint = endpoint
        self.model = model.trimmingCharacters(in: .whitespacesAndNewlines)
        self.key = key
        if let session { self.session = session }
        else {
            let config = URLSessionConfiguration.ephemeral
            config.httpCookieStorage = nil
            config.timeoutIntervalForRequest = 180
            self.session = URLSession(configuration: config, delegate: ModelRedirectPolicy(), delegateQueue: nil)
        }
    }

    func complete(system: String, prompt: String, schema: [String: Any]? = nil) async throws -> Any {
        let contract: Any = schema ?? NSNull()
        let contractData = try JSONSerialization.data(withJSONObject: contract, options: [.fragmentsAllowed])
        guard let schemaJSON = String(data: contractData, encoding: .utf8) else { throw ProbeError.message("模型请求格式无效。") }
        let body = try provider.withCString { provider in try model.withCString { model in try system.withCString { system in
            try prompt.withCString { prompt in try schemaJSON.withCString { schema in
                try CoreBridge.decode(modelRequestABI(provider, model, system, prompt, schema))
            } }
        } } }
        var request = URLRequest(url: endpoint)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        if !key.isEmpty { request.setValue("Bearer " + key, forHTTPHeaderField: "Authorization") }
        request.httpBody = try JSONSerialization.data(withJSONObject: body)
        let (data, response) = try await session.data(for: request)
        guard let response = response as? HTTPURLResponse, response.statusCode == 200,
              let content = String(data: data, encoding: .utf8) else {
            throw ProbeError.message("模型请求失败，请检查地址、密钥和模型名称。")
        }
        return try provider.withCString { provider in try content.withCString { body in
            try CoreBridge.decode(modelParseABI(provider, body))
        } }
    }
}
