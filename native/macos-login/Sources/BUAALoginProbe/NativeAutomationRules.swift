import Foundation

/// Account-scoped configuration and timetable planning. Execution is handled separately.
@MainActor
final class NativeAutomationRules {
    private let owner: String
    private let store: NativeStore
    init(owner: String, store: NativeStore) throws {
        guard !owner.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { throw ProbeError.message("请先打开本地学习空间。") }
        self.owner = owner; self.store = store
    }

    func rows() throws -> [[String:Any]] {
        let schedule = try store.get(owner: owner, kind: "meta", id: "automation_schedule") as? [String:Any]
        return schedule?["rows"] as? [[String:Any]] ?? []
    }

    func plan(courseID: String, now: String) throws -> [String:Any]? {
        guard let rule = try store.get(owner: owner, kind: "automation", id: courseID) as? [String:Any] else { return nil }
        let planned = try CoreBridge.planAutomation(rule: rule, rows: rows(), now: now)
        try store.put(owner: owner, kind: "automation", id: courseID, value: planned)
        return planned
    }

    func configure(courseID: String, enabled: Bool, autoAnalyze: Bool, modelReady: Bool, now: String) throws -> [String:Any] {
        guard try store.get(owner: owner, kind: "course", id: courseID) != nil else { throw ProbeError.message("未找到这门课程，请先同步课程。") }
        guard !autoAnalyze || enabled else { throw ProbeError.message("请先开启自动获取。") }
        guard !autoAnalyze || modelReady else { throw ProbeError.message("请先配置模型，再开启自动笔记。") }
        var rule = try store.get(owner: owner, kind: "automation", id: courseID) as? [String:Any] ?? [:]
        rule["course_id"] = courseID; rule["enabled"] = enabled; rule["auto_analyze"] = autoAnalyze
        rule["interval_minutes"] = 120; rule["updated_at"] = now
        let planned = try CoreBridge.planAutomation(rule: rule, rows: rows(), now: now)
        try store.put(owner: owner, kind: "automation", id: courseID, value: planned)
        return planned
    }

    func retry(courseID: String, now: String) throws {
        guard var rule = try store.get(owner: owner, kind: "automation", id: courseID) as? [String:Any],
              rule["enabled"] as? Bool == true else { throw ProbeError.message("请先开启该课程的自动获取。") }
        var watches = rule["watches"] as? [String:[String:Any]] ?? [:]
        for key in watches.keys where watches[key]?["status"] as? String == "waiting" { watches[key]?.removeValue(forKey:"next_check") }
        rule["watches"] = watches
        let planned = try CoreBridge.planAutomation(rule:rule, rows:rows(), now:now)
        try store.put(owner:owner, kind:"automation", id:courseID, value:planned)
    }

    func syncSchedule(school: NativeSchoolSession, now: Date = Date()) async throws -> [[String:Any]] {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "Asia/Shanghai")!
        let formatter = DateFormatter(); formatter.calendar = calendar; formatter.timeZone = calendar.timeZone
        formatter.locale = Locale(identifier: "en_US_POSIX"); formatter.dateFormat = "yyyy-MM-dd"
        guard let start = calendar.date(byAdding: .day, value: -7, to: now),
              let end = calendar.date(byAdding: .day, value: 21, to: now) else { throw ProbeError.message("课表日期无效。") }
        let rows = try await school.schedule(start: formatter.string(from: start), end: formatter.string(from: end))
        let timestamp = ISO8601DateFormatter().string(from: now)
        try store.put(owner: owner, kind: "meta", id: "automation_schedule", value: ["rows":rows, "start":formatter.string(from:start), "end":formatter.string(from:end), "synced_at":timestamp])
        for rule in try store.list(owner: owner, kind: "automation").compactMap({ $0 as? [String:Any] }) {
            if let id = rule["course_id"] as? String { _ = try plan(courseID: id, now: timestamp) }
        }
        return rows
    }
}
