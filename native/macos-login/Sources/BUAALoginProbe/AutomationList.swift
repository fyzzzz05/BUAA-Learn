import SwiftUI
import WebKit

/// Hosts the same Base UI controls used by the existing application.
struct AutomationList: NSViewRepresentable {
    let workspace: Int
    let courses: [Course]
    let savedCourses: [[String:Any]]
    let rules: [[String:Any]]
    let save: ([String:Any], Int) -> [String:Any]
    @Binding var draft: [String:Any]?

    func makeCoordinator() -> Coordinator { Coordinator(self) }
    func makeNSView(context: Context) -> WKWebView {
        let config = WKWebViewConfiguration()
        config.websiteDataStore = .nonPersistent()
        config.userContentController.add(context.coordinator, name: "automationDraft")
        config.userContentController.add(context.coordinator, name: "automationReady")
        config.userContentController.add(context.coordinator, name: "automationSave")
        let view = WKWebView(frame: .zero, configuration: config)
        view.setValue(false, forKey: "drawsBackground")
        if let url = Bundle.main.resourceURL?.appendingPathComponent("automation.html") {
            view.loadFileURL(url, allowingReadAccessTo: url.deletingLastPathComponent())
        }
        context.coordinator.webView = view
        return view
    }
    func updateNSView(_ view: WKWebView, context: Context) {
        context.coordinator.parent = self
        context.coordinator.renderIfNeeded()
    }
    static func dismantleNSView(_ view: WKWebView, coordinator: Coordinator) {
        view.configuration.userContentController.removeScriptMessageHandler(forName: "automationDraft")
        view.configuration.userContentController.removeScriptMessageHandler(forName: "automationReady")
        view.configuration.userContentController.removeScriptMessageHandler(forName: "automationSave")
        view.stopLoading()
    }
    final class Coordinator: NSObject, WKScriptMessageHandler {
        var parent: AutomationList
        weak var webView: WKWebView?
        var ready = false
        var rendered: Data?
        var renderedRules: Data?
        var saving = false
        var expectedRules: Data?
        var receivedSaveDraft = false
        init(_ parent: AutomationList) { self.parent = parent }
        func ruleSignature(_ rules: [[String:Any]]) -> Data? {
            let sorted = rules.sorted { ($0["course_id"] as? String ?? "") < ($1["course_id"] as? String ?? "") }
            return try? JSONSerialization.data(withJSONObject: sorted, options: [.sortedKeys])
        }
        func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
            guard message.frameInfo.isMainFrame else { return }
            if message.name == "automationReady" { ready = true; renderIfNeeded(); return }
            if message.name == "automationSave", let body = message.body as? [String:Any],
               body["owner"] as? String == "workspace-\(parent.workspace)",
               let id = body["id"] as? Int, let changes = body["changes"] as? [String:Any] {
                saving = true
                receivedSaveDraft = false
                let result = parent.save(changes, parent.workspace)
                let saved = result["saved"] as? [[String:Any]] ?? []
                let ids = Set(saved.compactMap { $0["course_id"] as? String })
                expectedRules = ruleSignature(parent.rules.filter { !ids.contains($0["course_id"] as? String ?? "") } + saved)
                if let data = try? JSONSerialization.data(withJSONObject: result), let json = String(data:data, encoding:.utf8) {
                    webView?.evaluateJavaScript("window.resolveAutomationSave(\(id),\(json))") { _, error in
                        if error != nil { self.saving = false; self.renderIfNeeded() }
                    }
                } else {
                    saving = false
                }
                return
            }
            guard message.name == "automationDraft", let value = message.body as? [String:Any],
                  value["owner"] as? String == "workspace-\(parent.workspace)" else { return }
            parent.draft = value
            if saving { receivedSaveDraft = true }
            renderIfNeeded()
        }
        func renderIfNeeded() {
            guard ready, let webView else { return }
            let courses: [[String:Any]] = parent.courses.map { course in
                let saved = parent.savedCourses.first { $0["id"] as? String == course.id }
                return ["id":course.id, "name":course.name, "teachers":course.teachers, "code":course.code,
                        "recording_count":saved?["recording_count"] as? Int ?? 0]
            }
            let identity: [String:Any] = ["owner":"workspace-\(parent.workspace)", "courses":courses]
            guard let signature = try? JSONSerialization.data(withJSONObject: identity, options: [.sortedKeys]),
                  let ruleSignature = ruleSignature(parent.rules) else { return }
            if saving {
                guard receivedSaveDraft, expectedRules == ruleSignature else { return }
                saving = false; expectedRules = nil; renderedRules = ruleSignature
            }
            let dirty = !(parent.draft?["changes"] as? [String:Any] ?? [:]).isEmpty
            guard signature != rendered || (ruleSignature != renderedRules && !dirty && !saving) else { return }
            var props = identity
            props["rules"] = parent.rules
            props["draft"] = parent.draft ?? NSNull()
            guard let data = try? JSONSerialization.data(withJSONObject: props), let json = String(data: data, encoding: .utf8) else { return }
            rendered = signature
            renderedRules = ruleSignature
            webView.evaluateJavaScript("window.renderAutomation(\(json))") { _, error in
                if error != nil { self.rendered = nil; self.renderedRules = nil }
            }
        }
    }
}
