import Foundation

struct AutomationLesson {
    let id: String
    let start: Date
    let hasRecording: Bool
    let recordingCourseID: String
}

@MainActor
protocol AutomationTransport {
    func catalogue(courseID: String) async throws -> [AutomationLesson]
    func materials(lesson: AutomationLesson) async throws -> (segments: Int, slides: Int)
    func analyze(lessonID: String) async throws
}

/// Executes one due course. Retry deadlines are persisted before external work.
@MainActor
final class NativeAutomationRunner {
    private let owner: String
    private let store: NativeStore
    private let rules: NativeAutomationRules
    init(owner: String, store: NativeStore) throws {
        self.owner = owner; self.store = store
        rules = try NativeAutomationRules(owner: owner, store: store)
    }
    private func date(_ text: String?) -> Date? {
        guard let text else { return nil }
        let formatter = ISO8601DateFormatter()
        if let value = formatter.date(from: text) { return value }
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter.date(from: text)
    }
    private func load(_ courseID: String) throws -> [String:Any]? {
        try store.get(owner: owner, kind: "automation", id: courseID) as? [String:Any]
    }
    private func save(_ value: [String:Any], courseID: String, now: String) throws -> [String:Any] {
        let planned = try CoreBridge.planAutomation(rule: value, rows: rules.rows(), now: now)
        try store.put(owner: owner, kind: "automation", id: courseID, value: planned)
        return planned
    }
    func run(courseID: String, now: Date, transport: any AutomationTransport) async throws -> Int {
        let timestamp = ISO8601DateFormatter().string(from: now)
        guard let planned = try rules.plan(courseID: courseID, now: timestamp), planned["enabled"] as? Bool == true else { return 0 }
        let watches = planned["watches"] as? [String:[String:Any]] ?? [:]
        let due = watches.keys.sorted().filter { key in
            guard let watch = watches[key], watch["status"] as? String == "waiting",
                  let dueAt = date(watch["due_at"] as? String), dueAt <= now else { return false }
            return date(watch["next_check"] as? String).map { $0 <= now } ?? true
        }
        guard !due.isEmpty else { return 0 }
        guard var fresh = try load(courseID), fresh["enabled"] as? Bool == true else { return 0 }
        var freshWatches = fresh["watches"] as? [String:[String:Any]] ?? [:]
        let next = ISO8601DateFormatter().string(from: now.addingTimeInterval(7200))
        for key in due { freshWatches[key]?["next_check"] = next }
        fresh["watches"] = freshWatches; fresh["last_run"] = timestamp
        _ = try save(fresh, courseID: courseID, now: timestamp)
        let lessons = try await transport.catalogue(courseID: courseID)
        var processed = 0
        for key in due {
            guard let rule = try load(courseID), rule["enabled"] as? Bool == true,
                  let watch = (rule["watches"] as? [String:[String:Any]])?[key],
                  let start = date(watch["start"] as? String), let end = date(watch["end"] as? String) else { continue }
            let candidates = lessons.filter { $0.start >= start.addingTimeInterval(-900) && $0.start < end }
            for lesson in candidates where lesson.hasRecording {
                guard let current = try load(courseID), current["enabled"] as? Bool == true else { return processed }
                if Set(current["known"] as? [String] ?? []).contains(lesson.id) { continue }
                let material = try await transport.materials(lesson: lesson)
                guard material.segments > 0 || material.slides > 0 else { continue }
                guard let nextRule = try load(courseID), nextRule["enabled"] as? Bool == true else { return processed }
                if nextRule["auto_analyze"] as? Bool == true { try await transport.analyze(lessonID: lesson.id) }
                guard var latest = try load(courseID), latest["enabled"] as? Bool == true else { return processed }
                var known = Set(latest["known"] as? [String] ?? []); known.insert(lesson.id)
                latest["known"] = known.sorted()
                _ = try save(latest, courseID: courseID, now: timestamp)
                processed += 1
            }
            guard var current = try load(courseID), current["enabled"] as? Bool == true else { return processed }
            let known = Set(current["known"] as? [String] ?? [])
            if !candidates.isEmpty && candidates.allSatisfy({ $0.hasRecording && known.contains($0.id) }) {
                var all = current["watches"] as? [String:[String:Any]] ?? [:]
                all[key]?["status"] = "completed"; all[key]?["completed_at"] = timestamp
                current["watches"] = all
                _ = try save(current, courseID: courseID, now: timestamp)
            }
        }
        return processed
    }
}
