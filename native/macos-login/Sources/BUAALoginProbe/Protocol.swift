import Foundation

struct Course: Identifiable {
    let id: String
    let name: String
    let taskID: String
    var semester: String = ""
    var code: String = ""
    var teachers: String = ""
}

struct ProbeLesson: Identifiable {
    let id: String
    let title: String
    let hasRecording: Bool
    var startAt: Int64 = 0
    var duration: UInt64 = 0
    var teachers: String = ""
    var recordingCourseID: String = ""
}

enum ProbeError: LocalizedError {
    case message(String)
    var errorDescription: String? {
        switch self { case .message(let value): return value }
    }
}

func schoolHost(_ url: URL) -> Bool {
    guard url.scheme?.lowercased() == "https", let host = url.host?.lowercased() else { return false }
    return host == "buaa.edu.cn" || host.hasSuffix(".buaa.edu.cn")
}

func upgradedRecordingURL(_ url: URL) -> URL? {
    guard url.scheme?.lowercased() == "http", url.host?.lowercased() == "yjapi.msa.buaa.edu.cn",
          url.port == nil || url.port == 80,
          var components = URLComponents(url: url, resolvingAgainstBaseURL: false) else { return nil }
    components.scheme = "https"
    components.port = nil
    return components.url
}

func recordingBearer(_ cookies: [HTTPCookie]) -> String? {
    let allowed = Set(["buaa.edu.cn", "msa.buaa.edu.cn", "classroom.msa.buaa.edu.cn", "yjapi.msa.buaa.edu.cn", "portal.msa.buaa.edu.cn", "www.msa.buaa.edu.cn", "resource.msa.buaa.edu.cn"])
    for cookie in cookies where cookie.name == "_token" {
        guard allowed.contains(cookie.domain.trimmingCharacters(in: CharacterSet(charactersIn: ".")).lowercased()) else { continue }
        let text = cookie.value.removingPercentEncoding ?? cookie.value
        guard let regex = try? NSRegularExpression(pattern: #"s:\d+:"_token";i:\d+;s:\d+:"(.+?)";"#),
              let match = regex.firstMatch(in: text, range: NSRange(text.startIndex..., in: text)),
              let range = Range(match.range(at: 1), in: text) else { continue }
        return String(text[range])
    }
    return nil
}

func courseID(from url: URL?) -> String? {
    guard let url, schoolHost(url), let components = URLComponents(url: url, resolvingAgainstBaseURL: false) else { return nil }
    return components.queryItems?.first(where: { $0.name == "course_id" && !($0.value ?? "").isEmpty })?.value
}

// Portal credentials never follow redirects to another host.
final class PortalRedirectPolicy: NSObject, URLSessionTaskDelegate {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        guard request.url?.scheme == "https", request.url?.host == "spoc.buaa.edu.cn" else { completionHandler(nil); return }
        completionHandler(request)
    }
}

final class PortalClient {
    private let token: String
    private let role: String
    private let delegate = PortalRedirectPolicy()
    private var providedSession: URLSession?
    private lazy var session: URLSession = {
        if let providedSession { return providedSession }
        let config = URLSessionConfiguration.ephemeral
        config.httpCookieStorage = nil
        config.timeoutIntervalForRequest = 30
        return URLSession(configuration: config, delegate: delegate, delegateQueue: nil)
    }()
    init(token: String, role: String, session: URLSession? = nil) {
        self.token = token; self.role = role; self.providedSession = session
    }

    func request(_ path: String, params: [String: String] = [:], post: Bool = false) async throws -> Any {
        var url = URLComponents(string: "https://spoc.buaa.edu.cn/spocnewht" + path)!
        url.queryItems = params.sorted { $0.key < $1.key }.map { URLQueryItem(name: $0.key, value: $0.value) }
        var request = URLRequest(url: url.url!)
        request.setValue("Inco-" + token, forHTTPHeaderField: "Token")
        request.setValue(role, forHTTPHeaderField: "RoleCode")
        request.setValue("XMLHttpRequest", forHTTPHeaderField: "X-Requested-With")
        request.setValue("https://spoc.buaa.edu.cn/spocnew/", forHTTPHeaderField: "Referer")
        if post { request.httpMethod = "POST"; request.httpBody = Data("{}".utf8); request.setValue("application/json", forHTTPHeaderField: "Content-Type") }
        let (data, response) = try await session.data(for: request)
        guard let response = response as? HTTPURLResponse, response.statusCode == 200,
              let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              (json["code"] as? Int) == 200 else { throw ProbeError.message("教学空间接口未成功响应，请检查网络或重新登录。") }
        return json["content"] ?? NSNull()
    }

    func semesters() async throws -> [String] {
        try CoreBridge.semesters(content: await request("/component/queryXnxq", post: true))
    }

    func courses(term: String) async throws -> [Course] {
        try CoreBridge.courses(content: await request("/jxkj/queryKclb", params: ["xnxq": term, "lx": "1"]), term: term)
    }

    func recordingEntry(_ course: Course) async throws -> URL {
        let role = String(describing: try await request("/kc/queryYhSflx", params: ["kcid": course.id]))
        let student = !["1", "3", "6"].contains(role)
        var params = ["kcid": course.id, "type": "", "isxs": student ? "1" : "0"]
        if !student {
            let rows = try await request("/lbsp/queryRwhList", params: ["kcid": course.id]) as? [[String: Any]] ?? []
            guard let selected = rows.first(where: { String(describing: $0["RWH"] ?? "") == course.taskID }) ?? rows.first,
                  let task = selected["RWH"] else { throw ProbeError.message("这门课程没有录播教学班，请选择其他课程。") }
            params["rwh"] = String(describing: task)
        }
        guard let signature = try await request("/yun/getHxyTimestampAndSignature2", params: params) as? [String: Any],
              signature["bind_id"] != nil, let raw = signature["ktsl_url"] as? String,
              var components = URLComponents(string: raw), let base = components.url, schoolHost(base) else {
            throw ProbeError.message("这门课程没有可用的学校录播入口，请选择其他课程。")
        }
        let keys = ["bind_id", "username", "realname", "tenant_code", "timestamp", "signature", "allow_live"]
        components.queryItems = (components.queryItems ?? []) + keys.compactMap { key in signature[key].map { URLQueryItem(name: key, value: String(describing: $0)) } }
        guard let url = components.url else { throw ProbeError.message("录播入口格式不正确。") }
        return url
    }
}
