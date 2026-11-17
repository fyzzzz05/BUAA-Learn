import Foundation

struct TimetableSemester {
    let id: String
    let start: Date
    let weeks: Int

    // Official BUAA 2026–27 calendar: https://jiaowu.buaa.edu.cn/index/xl.htm
    // Keep dates explicit; never guess a new academic year's teaching weeks.
    init?(id: String) {
        let dates: [String:(String,Int)] = [
            "2026-20271":("2026-09-07",19),
            "2026-20272":("2027-03-01",18),
            "2026-20273":("2027-07-05",8),
        ]
        guard let (first, weeks) = dates[id], let start = Self.parseDate(first) else { return nil }
        self.id = id; self.start = start; self.weeks = weeks
    }

    static func parseDate(_ value: String) -> Date? {
        let formatter = DateFormatter()
        formatter.calendar = TimetableWeek.calendar; formatter.timeZone = TimetableWeek.calendar.timeZone
        formatter.locale = Locale(identifier:"en_US_POSIX"); formatter.dateFormat = "yyyy-MM-dd"
        return formatter.date(from:value)
    }
    func date(week: Int) -> Date {
        TimetableWeek.calendar.date(byAdding:.day, value:(min(weeks,max(1,week)) - 1) * 7, to:start)!
    }
    func currentWeek(now: Date = Date()) -> Int? {
        let days = TimetableWeek.calendar.dateComponents([.day], from:start, to:TimetableWeek.calendar.startOfDay(for:now)).day ?? -1
        guard days >= 0, days < weeks * 7 else { return nil }
        return days / 7 + 1
    }
    var end: Date { TimetableWeek.calendar.date(byAdding:.day, value:weeks * 7 - 1, to:start)! }
}

@MainActor
enum NativeSemesterSchedule {
    /// Fetch bounded date ranges and commit once, preserving the previous complete cache on failure.
    static func sync(semester: TimetableSemester, school: NativeSchoolSession, store: NativeStore,
                     owner: String, progress: (Int,Int) throws -> Void) async throws -> [[String:Any]] {
        let total = (semester.weeks + 3) / 4
        var rows: [[String:Any]] = []
        for batch in 0..<total {
            try progress(batch,total)
            let from = semester.date(week:batch * 4 + 1)
            let last = min(semester.weeks * 7 - 1, batch * 28 + 27)
            let to = TimetableWeek.calendar.date(byAdding:.day, value:last, to:semester.start)!
            rows += try await school.schedule(start:TimetableWeek.dateKey(from), end:TimetableWeek.dateKey(to))
        }
        let first = TimetableWeek.dateKey(semester.start), last = TimetableWeek.dateKey(semester.end)
        rows = rows.filter { let date = $0["date"] as? String ?? ""; return date >= first && date <= last }
        try store.put(owner:owner, kind:"meta", id:"semester_schedule", value:[
            "semester":semester.id, "start":first, "end":last, "weeks":semester.weeks,
            "rows":rows, "synced_at":ISO8601DateFormatter().string(from:Date()),
        ])
        try progress(total,total)
        return rows
    }
}
