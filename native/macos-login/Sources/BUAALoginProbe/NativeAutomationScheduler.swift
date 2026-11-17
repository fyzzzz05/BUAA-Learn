import Foundation

@MainActor
protocol AutomationSchedulerTransport: AutomationTransport {
    func synchronizeSchedule(now: Date) async throws
}

/// One tick is serialized by the host; saved request times prevent rapid retries after failures.
@MainActor
final class NativeAutomationScheduler {
    private let owner: String
    private let store: NativeStore
    private let planner: NativeAutomationRules
    private let onChange: (String, String) -> Void
    private var running = false
    init(owner: String, store: NativeStore, onChange: @escaping (String, String) -> Void = { _, _ in }) throws {
        self.owner = owner; self.store = store
        self.onChange = onChange
        planner = try NativeAutomationRules(owner: owner, store: store)
    }
    private func parse(_ text: String?) -> Date? {
        guard let text else { return nil }
        let formatter = ISO8601DateFormatter()
        if let value = formatter.date(from: text) { return value }
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter.date(from: text)
    }
    private func recordJob(id: String, kind: String, target: String, status: String, message: String, createdAt: String, finishedAt: String? = nil) throws {
        var value: [String:Any] = ["id":id, "kind":kind, "target":target, "status":status, "message":message, "created_at":createdAt]
        if let finishedAt { value["finished_at"] = finishedAt }
        try store.put(owner: owner, kind: "job", id: id, value: value)
        onChange(id, status)
    }
    func tick(now: Date, transport: any AutomationSchedulerTransport) async throws {
        guard !running else { return }
        running = true; defer { running = false }
        let active = try store.list(owner: owner, kind: "automation").compactMap { $0 as? [String:Any] }
            .filter { $0["enabled"] as? Bool == true }
        guard !active.isEmpty else { return }
        let timestamp = ISO8601DateFormatter().string(from: now)
        var calendar = Calendar(identifier: .gregorian); calendar.timeZone = TimeZone(identifier: "Asia/Shanghai")!
        var schedule = try store.get(owner: owner, kind: "meta", id: "automation_schedule") as? [String:Any] ?? [:]
        let synced = parse(schedule["synced_at"] as? String)
        let requested = parse(schedule["requested_at"] as? String)
        if (synced == nil || !calendar.isDate(synced!, inSameDayAs: now)) && (requested == nil || now.timeIntervalSince(requested!) >= 7200) {
            schedule["requested_at"] = timestamp
            try store.put(owner: owner, kind: "meta", id: "automation_schedule", value: schedule)
            let id = UUID().uuidString
            try recordJob(id:id, kind:"schedule", target:"", status:"running", message:"正在同步课表。", createdAt:timestamp)
            do {
                try await transport.synchronizeSchedule(now: now)
                try recordJob(id:id, kind:"schedule", target:"", status:"completed", message:"课表已同步。", createdAt:timestamp, finishedAt:ISO8601DateFormatter().string(from:Date()))
            } catch {
                try recordJob(id:id, kind:"schedule", target:"", status:"failed", message:(error as? LocalizedError)?.errorDescription ?? "课表同步失败。", createdAt:timestamp, finishedAt:ISO8601DateFormatter().string(from:Date()))
                if error is SchoolAuthorizationIssue { throw error }
            }
        }
        for initial in active {
            guard let courseID = initial["course_id"] as? String,
                  let rule = try planner.plan(courseID:courseID, now:timestamp), rule["enabled"] as? Bool == true,
                  let next = parse(rule["next_run"] as? String), next <= now else { continue }
            let id = UUID().uuidString
            try recordJob(id:id, kind:"automation", target:courseID, status:"running", message:"正在检查课程回放。", createdAt:timestamp)
            do {
                let runner = try NativeAutomationRunner(owner: owner, store: store)
                let count = try await runner.run(courseID: courseID, now: now, transport: transport)
                let updated = try store.get(owner:owner, kind:"automation", id:courseID) as? [String:Any]
                let pending = updated?["pending_count"] as? Int ?? 0
                try recordJob(id:id, kind:"automation", target:courseID, status:"completed", message:"本次处理 \(count) 节课，\(pending) 个课次等待回放。", createdAt:timestamp, finishedAt:ISO8601DateFormatter().string(from:Date()))
            } catch {
                try recordJob(id:id, kind:"automation", target:courseID, status:"failed", message:(error as? LocalizedError)?.errorDescription ?? "回放检查失败。", createdAt:timestamp, finishedAt:ISO8601DateFormatter().string(from:Date()))
                if error is SchoolAuthorizationIssue { throw error }
            }
        }
    }
}
