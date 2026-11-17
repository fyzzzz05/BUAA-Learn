import WebKit

/// Each concurrent course gets its own navigation state; only school cookies are shared.
@MainActor
final class RecordingAuthorizationResolver: NSObject, WKNavigationDelegate, WKUIDelegate {
    private let browser: WKWebView
    private var identifier: String?
    private var navigationError: Error?
    init(dataStore: WKWebsiteDataStore) {
        let config = WKWebViewConfiguration(); config.websiteDataStore = dataStore
        browser = WKWebView(frame:.zero,configuration:config)
        super.init(); browser.navigationDelegate = self; browser.uiDelegate = self
    }
    func resolve(entry: URL, timeout: TimeInterval = 8) async throws -> (id:String,token:String) {
        defer { browser.stopLoading(); browser.navigationDelegate = nil; browser.uiDelegate = nil }
        browser.load(URLRequest(url:entry))
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            try Task.checkCancellation()
            if let navigationError { throw navigationError }
            let cookies = await browser.configuration.websiteDataStore.httpCookieStore.allCookies()
            if let identifier, let token = recordingBearer(cookies) { return (identifier,token) }
            try await Task.sleep(nanoseconds:150_000_000)
        }
        throw ProbeError.message("未及时取得这门课的录播入口，已跳过；可稍后单独重试。")
    }
    func webView(_ webView: WKWebView, decidePolicyFor action: WKNavigationAction, decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        guard let url = action.request.url else { decisionHandler(.cancel); return }
        if let upgraded = upgradedRecordingURL(url) {
            decisionHandler(.cancel); var request = action.request; request.url = upgraded
            DispatchQueue.main.async { webView.load(request) }; return
        }
        if let id = courseID(from:url) { identifier = id }
        decisionHandler(schoolHost(url) ? .allow : .cancel)
    }
    func webView(_ webView: WKWebView, createWebViewWith configuration: WKWebViewConfiguration, for action: WKNavigationAction, windowFeatures: WKWindowFeatures) -> WKWebView? {
        if let url = action.request.url, schoolHost(url) { webView.load(action.request) }; return nil
    }
    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        let code = (error as NSError).code
        if code != NSURLErrorCancelled && code != 102 { navigationError = error }
    }
}

@MainActor
enum RecordingBatch {
    static func run<T>(items:[T], limit:Int = 3, operation:@escaping @MainActor (T) async -> Void) async {
        await withTaskGroup(of:Void.self) { group in
            var next = 0
            for _ in 0..<min(max(1,limit),items.count) {
                let item = items[next]; next += 1
                group.addTask { @MainActor in await operation(item) }
            }
            while await group.next() != nil {
                if next < items.count && !Task.isCancelled {
                    let item = items[next]; next += 1
                    group.addTask { @MainActor in await operation(item) }
                }
            }
        }
    }
}
