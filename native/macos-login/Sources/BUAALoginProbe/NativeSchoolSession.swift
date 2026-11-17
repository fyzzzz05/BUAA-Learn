import Foundation
import Darwin

private typealias SchoolSend = @convention(c) (UnsafeMutableRawPointer?, UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>?
private typealias SchoolRelease = @convention(c) (UnsafeMutableRawPointer?, UnsafeMutablePointer<CChar>?) -> Void
@_silgen_name("buaa_school_open")
private func schoolOpen(_ context: UnsafeMutableRawPointer?, _ send: SchoolSend?, _ release: SchoolRelease?, _ token: UnsafePointer<CChar>?, _ refresh: UnsafePointer<CChar>?, _ role: UnsafePointer<CChar>?) -> OpaquePointer?
@_silgen_name("buaa_school_close")
private func schoolClose(_ handle: OpaquePointer?)
@_silgen_name("buaa_school_credentials")
private func schoolCredentials(_ handle: OpaquePointer?) -> UnsafeMutablePointer<CChar>?
@_silgen_name("buaa_school_set_recording")
private func schoolSetRecording(_ handle: OpaquePointer?, _ token: UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>?
@_silgen_name("buaa_school_semesters")
private func schoolSemesters(_ handle: OpaquePointer?) -> UnsafeMutablePointer<CChar>?
@_silgen_name("buaa_school_courses")
private func schoolCourses(_ handle: OpaquePointer?, _ term: UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>?
@_silgen_name("buaa_school_recording_signature")
private func schoolRecordingSignature(_ handle: OpaquePointer?, _ course: UnsafePointer<CChar>?, _ task: UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>?
@_silgen_name("buaa_school_schedule")
private func schoolSchedule(_ handle: OpaquePointer?, _ start: UnsafePointer<CChar>?, _ end: UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>?
@_silgen_name("buaa_school_catalogue")
private func schoolCatalogue(_ handle: OpaquePointer?, _ course: UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>?
@_silgen_name("buaa_school_materials_with_duration")
private func schoolMaterials(_ handle: OpaquePointer?, _ course: UnsafePointer<CChar>?, _ lesson: UnsafePointer<CChar>?, _ duration: Double) -> UnsafeMutablePointer<CChar>?

private final class RejectSchoolRedirects: NSObject, URLSessionTaskDelegate {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) { completionHandler(nil) }
}

private final class SchoolTransport {
    private let delegate = RejectSchoolRedirects()
    private let provided: URLSession?
    private let timeout: TimeInterval
    private lazy var session: URLSession = {
        if let provided { return provided }
        let config = URLSessionConfiguration.ephemeral
        config.httpCookieStorage = nil
        config.timeoutIntervalForRequest = timeout
        config.timeoutIntervalForResource = timeout + 2
        return URLSession(configuration: config, delegate: delegate, delegateQueue: nil)
    }()
    init(session: URLSession? = nil, timeout: TimeInterval = 30) { provided = session; self.timeout = timeout }

    func send(_ payload: String) -> String? {
        guard let data = payload.data(using: .utf8),
              let body = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let rawURL = body["url"] as? String, let components = URLComponents(string: rawURL),
              components.scheme == "https", components.user == nil, components.password == nil,
              let host = components.host, ["spoc.buaa.edu.cn", "classroom.msa.buaa.edu.cn", "yjapi.msa.buaa.edu.cn"].contains(host),
              components.port == nil || components.port == 443,
              let url = components.url, let method = body["method"] as? String, ["GET", "POST"].contains(method),
              let headers = body["headers"] as? [String: String] else { return nil }
        if host == "spoc.buaa.edu.cn" {
            if headers["Authorization"] != nil { return nil }
        } else if headers["Token"] != nil || headers["RoleCode"] != nil { return nil }
        if headers.values.contains(where: { $0.contains("\r") || $0.contains("\n") }) { return nil }
        var request = URLRequest(url: url)
        request.timeoutInterval = timeout
        request.httpMethod = method
        for (key, value) in headers { request.setValue(value, forHTTPHeaderField: key) }
        if let value = body["body"], !(value is NSNull) {
            guard method == "POST", let bytes = try? JSONSerialization.data(withJSONObject: value) else { return nil }
            request.httpBody = bytes
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        }
        final class ResultBox { var data: Data?; var response: HTTPURLResponse? }
        let box = ResultBox(); let done = DispatchSemaphore(value: 0)
        let task = session.dataTask(with: request) { data, response, _ in
            box.data = data; box.response = response as? HTTPURLResponse; done.signal()
        }
        task.resume()
        guard done.wait(timeout: .now() + 35) == .success else { task.cancel(); return nil }
        guard let response = box.response, let bytes = box.data, bytes.count <= 32 * 1024 * 1024,
              let text = String(data: bytes, encoding: .utf8),
              let json = try? JSONSerialization.data(withJSONObject: ["status":response.statusCode,"body":text]),
              let result = String(data: json, encoding: .utf8) else { return nil }
        return result
    }
}

private let schoolSendCallback: SchoolSend = { context, request in
    guard let context, let request else { return nil }
    let transport = Unmanaged<SchoolTransport>.fromOpaque(context).takeUnretainedValue()
    guard let response = transport.send(String(cString: request)) else { return nil }
    return response.withCString { strdup($0) }
}
private let schoolReleaseCallback: SchoolRelease = { _, response in free(response) }

/// Owns a transient Rust session. All blocking transport calls run on a private queue.
// Calls and handle access are serialized on `queue`; queued work retains `self`.
final class NativeSchoolSession: @unchecked Sendable {
    @MainActor var onAuthorizationExpired: ((SchoolAuthorizationIssue) -> Void)?
    @MainActor var onCredentialsChanged: ((String) -> Void)?
    private let queue = DispatchQueue(label: "buaa.native.school-session")
    private let context: UnsafeMutableRawPointer
    private let handle: OpaquePointer
    private let providedSession: URLSession?
    init(token: String, refresh: String, role: String, session: URLSession? = nil, requestTimeout: TimeInterval = 30) throws {
        self.providedSession = session
        let transport = SchoolTransport(session: session, timeout:requestTimeout)
        let context = Unmanaged.passRetained(transport).toOpaque()
        let handle = token.withCString { token in refresh.withCString { refresh in role.withCString { role in
            schoolOpen(context, schoolSendCallback, schoolReleaseCallback, token, refresh, role)
        } } }
        guard let handle else {
            Unmanaged<SchoolTransport>.fromOpaque(context).release()
            throw ProbeError.message("学校登录信息无效。")
        }
        self.context = context; self.handle = handle
    }
    deinit { schoolClose(handle); Unmanaged<SchoolTransport>.fromOpaque(context).release() }
    private func call(_ operation: @escaping @Sendable (OpaquePointer) -> UnsafeMutablePointer<CChar>?) async throws -> Any {
        do {
        let result = try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Any, Error>) in
            queue.async { [self] in
                do { continuation.resume(returning: try CoreBridge.decode(operation(handle))) }
                catch { continuation.resume(throwing: error) }
            }
        }
        let saved: String = try await withCheckedThrowingContinuation { continuation in
            queue.async { [self] in
                do {
                    let value = try CoreBridge.decode(schoolCredentials(handle))
                    let bytes = try JSONSerialization.data(withJSONObject:value,options:.sortedKeys)
                    continuation.resume(returning:String(decoding:bytes,as:UTF8.self))
                } catch { continuation.resume(throwing:error) }
            }
        }
        await MainActor.run { self.onCredentialsChanged?(saved) }
        return result
        } catch let issue as SchoolAuthorizationIssue {
            await MainActor.run { self.onAuthorizationExpired?(issue) }
            throw issue
        }
    }
    /// Independent queues/handles make catalogue requests concurrent without sharing mutable tokens.
    func recordingWorker() async throws -> (NativeSchoolSession, Bool) {
        guard let saved = try await call({ schoolCredentials($0) }) as? [String:Any],
              let token = saved["token"] as? String, let role = saved["role"] as? String else { throw ProbeError.message("学校会话不可用。") }
        // Workers do not race to rotate the same refresh token. The main session owns refresh.
        let worker = try NativeSchoolSession(token:token, refresh:"", role:role, session:providedSession, requestTimeout:10)
        let recording = saved["recording"] as? String ?? ""
        if !recording.isEmpty { try await worker.setRecording(token:recording) }
        return (worker,!recording.isEmpty)
    }
    func semesters() async throws -> [String] {
        guard let rows = try await call({ schoolSemesters($0) }) as? [String] else { throw ProbeError.message("学期数据格式异常。") }
        return rows
    }
    func courses(term: String) async throws -> [Course] {
        let output = try await call { handle in term.withCString { schoolCourses(handle, $0) } }
        guard let rows = output as? [[String: Any]] else { throw ProbeError.message("课程数据格式异常。") }
        return try rows.map { row in
            guard let id = row["id"] as? String, let name = row["name"] as? String, let task = row["task_id"] as? String else {
                throw ProbeError.message("课程数据格式异常。")
            }
            return Course(id: id, name: name, taskID: task, semester: row["semester"] as? String ?? term,
                          code: row["code"] as? String ?? "", teachers: row["teachers"] as? String ?? "")
        }
    }
    func setRecording(token: String) async throws {
        _ = try await call { handle in token.withCString { schoolSetRecording(handle, $0) } }
    }
    func recordingEntry(course: Course) async throws -> URL {
        let output = try await call { handle in course.id.withCString { id in course.taskID.withCString { task in
            schoolRecordingSignature(handle, id, task)
        } } }
        guard let signature = output as? [String:Any], let raw = signature["ktsl_url"] as? String,
              var components = URLComponents(string: raw), components.user == nil, components.password == nil,
              components.port == nil || components.port == 443,
              let base = components.url, schoolHost(base) else {
            throw ProbeError.message("这门课程没有可用的学校录播入口，请选择其他课程。")
        }
        let keys = ["bind_id", "username", "realname", "tenant_code", "timestamp", "signature", "allow_live"]
        components.queryItems = (components.queryItems ?? []) + keys.compactMap { key in
            guard let value = signature[key], value is String || value is NSNumber else { return nil }
            return URLQueryItem(name:key, value:String(describing:value))
        }
        guard let url = components.url else { throw ProbeError.message("录播入口格式不正确。") }
        return url
    }
    func schedule(start: String, end: String) async throws -> [[String:Any]] {
        let output = try await call { handle in start.withCString { start in end.withCString { end in
            schoolSchedule(handle, start, end)
        } } }
        guard let rows = output as? [[String:Any]] else { throw ProbeError.message("课表数据格式异常。") }
        return rows
    }
    func catalogue(course: String) async throws -> [ProbeLesson] {
        let output = try await call { handle in course.withCString { schoolCatalogue(handle, $0) } }
        guard let rows = output as? [[String: Any]] else { throw ProbeError.message("录播目录格式异常。") }
        return try rows.map { row in
            guard let id = row["id"] as? String, let title = row["title"] as? String,
                  let available = row["has_recording"] as? Bool else { throw ProbeError.message("录播目录格式异常。") }
            return ProbeLesson(id:id,title:title,hasRecording:available,
                               startAt: (row["start_at"] as? NSNumber)?.int64Value ?? 0,
                               duration: (row["duration_seconds"] as? NSNumber)?.uint64Value ?? 0,
                               teachers: row["teachers"] as? String ?? "", recordingCourseID: row["recording_course_id"] as? String ?? "")
        }
    }
    func materials(course: String, lesson: String, duration: Double = 0) async throws -> [String: Any] {
        let output = try await call { handle in course.withCString { course in lesson.withCString { lesson in
            schoolMaterials(handle, course, lesson, duration)
        } } }
        guard let data = output as? [String: Any], data["segments"] is [Any], data["slides"] is [Any],
              data["digest"] is String, data["coverage"] is [String:Any] else {
            throw ProbeError.message("课堂材料格式异常。")
        }
        return data
    }
}
