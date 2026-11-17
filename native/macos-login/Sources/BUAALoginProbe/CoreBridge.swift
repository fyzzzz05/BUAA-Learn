import Foundation

enum SchoolAuthorizationIssue: String, LocalizedError {
    case login = "login_required", recording = "recording_auth"
    var errorDescription: String? {
        self == .login ? "学校登录已过期，请重新登录。" : "录播授权未完成或已过期，请重新登录。"
    }
}
struct SchoolConnectionUnavailable: LocalizedError {
    var errorDescription: String? { "学校网络暂时无法连接，请稍后重试。" }
}

@_silgen_name("buaa_core_parse_semesters")
private func parseSemestersABI(_ body: UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>?
@_silgen_name("buaa_core_parse_courses")
private func parseCoursesABI(_ body: UnsafePointer<CChar>?, _ term: UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>?
@_silgen_name("buaa_core_parse_catalogue")
private func parseCatalogueABI(_ body: UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>?
@_silgen_name("buaa_core_parse_transcript")
private func parseTranscriptABI(_ body: UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>?
@_silgen_name("buaa_core_parse_slides")
private func parseSlidesABI(_ body: UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>?
@_silgen_name("buaa_core_ground_points")
private func groundPointsABI(_ points: UnsafePointer<CChar>?, _ evidence: UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>?
@_silgen_name("buaa_core_timeline")
private func timelineABI(_ plan: UnsafePointer<CChar>?, _ segments: UnsafePointer<CChar>?, _ findings: UnsafePointer<CChar>?, _ duration: Double) -> UnsafeMutablePointer<CChar>?
@_silgen_name("buaa_core_validate_answer")
private func validateAnswerABI(_ answer: UnsafePointer<CChar>?, _ cited: UnsafePointer<CChar>?, _ available: UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>?
@_silgen_name("buaa_core_plan_automation")
private func planAutomationABI(_ rule: UnsafePointer<CChar>?, _ rows: UnsafePointer<CChar>?, _ now: UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>?
@_silgen_name("buaa_core_string_free")
private func freeCoreString(_ value: UnsafeMutablePointer<CChar>?)

enum CoreBridge {
    static func planAutomation(rule: [String:Any], rows: [[String:Any]], now: String) throws -> [String:Any] {
        let ruleJSON = try encode(rule), rowsJSON = try encode(rows)
        let output = try ruleJSON.withCString { rule in try rowsJSON.withCString { rows in try now.withCString { now in
            try decode(planAutomationABI(rule, rows, now))
        } } }
        guard let result = output as? [String:Any] else { throw ProbeError.message("课程自动化数据格式异常。") }
        return result
    }
    static func decode(_ ptr: UnsafeMutablePointer<CChar>?) throws -> Any {
        guard let ptr else { throw ProbeError.message("共享内核未返回数据。") }
        defer { freeCoreString(ptr) }
        let json = Data(String(cString: ptr).utf8)
        guard let envelope = try JSONSerialization.jsonObject(with: json) as? [String: Any] else {
            throw ProbeError.message("共享内核返回的数据格式异常。")
        }
        guard envelope["ok"] as? Bool == true, let data = envelope["data"] else {
            if let code = envelope["error_code"] as? String, let issue = SchoolAuthorizationIssue(rawValue:code) { throw issue }
            if envelope["error_code"] as? String == "school_transport" { throw SchoolConnectionUnavailable() }
            throw ProbeError.message(envelope["error"] as? String ?? "共享内核未能完成操作。")
        }
        return data
    }
    static func semesters(content: Any) throws -> [String] {
        let body = try response(content: content)
        guard let rows = try body.withCString({ try decode(parseSemestersABI($0)) }) as? [String] else {
            throw ProbeError.message("学校返回的学期数据格式异常。")
        }
        return rows
    }
    static func courses(content: Any, term: String) throws -> [Course] {
        let body = try response(content: content)
        let result = try body.withCString { raw in try term.withCString { try decode(parseCoursesABI(raw, $0)) } }
        guard let rows = result as? [[String: Any]] else { throw ProbeError.message("学校返回的课程数据格式异常。") }
        return try rows.map { row in
            guard let id = row["id"] as? String, let name = row["name"] as? String,
                  let taskID = row["task_id"] as? String else { throw ProbeError.message("学校返回的课程数据格式异常。") }
            return Course(id: id, name: name, taskID: taskID, semester: row["semester"] as? String ?? term,
                          code: row["code"] as? String ?? "", teachers: row["teachers"] as? String ?? "")
        }
    }
    static func catalogue(data: Data) throws -> [ProbeLesson] {
        guard let body = String(data: data, encoding: .utf8) else { throw ProbeError.message("录播目录格式异常。") }
        guard let rows = try body.withCString({ try decode(parseCatalogueABI($0)) }) as? [[String: Any]] else {
            throw ProbeError.message("录播目录格式异常。")
        }
        return try rows.map { row in
            guard let id = row["id"] as? String, let title = row["title"] as? String,
                  let available = row["has_recording"] as? Bool else { throw ProbeError.message("录播目录格式异常。") }
            return ProbeLesson(id: id, title: title, hasRecording: available,
                               startAt: (row["start_at"] as? NSNumber)?.int64Value ?? 0,
                               duration: (row["duration_seconds"] as? NSNumber)?.uint64Value ?? 0,
                               teachers: row["teachers"] as? String ?? "", recordingCourseID: row["recording_course_id"] as? String ?? "")
        }
    }
    static func transcriptCount(data: Data) throws -> Int {
        guard let body = String(data: data, encoding: .utf8),
              let rows = try body.withCString({ try decode(parseTranscriptABI($0)) }) as? [Any] else {
            throw ProbeError.message("课堂文本格式异常。")
        }
        return rows.count
    }
    static func slidesCount(rows: [[String: Any]]) throws -> Int {
        let data = try JSONSerialization.data(withJSONObject: rows)
        guard let body = String(data: data, encoding: .utf8),
              let slides = try body.withCString({ try decode(parseSlidesABI($0)) }) as? [Any] else {
            throw ProbeError.message("PPT 截图索引格式异常。")
        }
        return slides.count
    }
    static func ground(points: [[String: Any]], evidence: [[String: Any]]) throws -> [[String: Any]] {
        let pointsJSON = try encode(points); let evidenceJSON = try encode(evidence)
        let output = try pointsJSON.withCString { points in try evidenceJSON.withCString { evidence in
            try decode(groundPointsABI(points, evidence))
        } }
        guard let rows = output as? [[String: Any]] else { throw ProbeError.message("课堂来源格式异常。") }
        return rows
    }
    static func timeline(plan: [String: Any], segments: [[String: Any]], findings: [String: Any], duration: Double) throws -> [[String: Any]] {
        let planJSON = try encode(plan); let segmentsJSON = try encode(segments); let findingsJSON = try encode(findings)
        let output = try planJSON.withCString { plan in try segmentsJSON.withCString { segments in try findingsJSON.withCString { findings in
            try decode(timelineABI(plan, segments, findings, duration))
        } } }
        guard let rows = output as? [[String: Any]] else { throw ProbeError.message("课堂时间轴格式异常。") }
        return rows
    }
    static func validateAnswer(_ answer: String, cited: [String], available: [String]) throws {
        let citedJSON = try encode(cited); let availableJSON = try encode(available)
        try answer.withCString { answer in try citedJSON.withCString { cited in try availableJSON.withCString { available in
            _ = try decode(validateAnswerABI(answer, cited, available))
        } } }
    }
    private static func encode(_ value: Any) throws -> String {
        let data = try JSONSerialization.data(withJSONObject: value, options: [.fragmentsAllowed])
        guard let string = String(data: data, encoding: .utf8) else { throw ProbeError.message("数据编码失败。") }
        return string
    }
    private static func response(content: Any) throws -> String {
        let data = try JSONSerialization.data(withJSONObject: ["code": 200, "content": content])
        guard let string = String(data: data, encoding: .utf8) else { throw ProbeError.message("学校返回的数据格式异常。") }
        return string
    }
}
