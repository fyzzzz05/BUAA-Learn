import SwiftUI

struct TimetableEntry: Identifiable {
    let id: String
    let date: String
    let name: String
    let room: String
    let time: String
    let start: Int?
    let end: Int?
    let course: Course?

    init(row: [String:Any], index: Int, courses: [Course]) {
        id = "schedule-\(index)"
        date = row["date"] as? String ?? ""
        room = row["room"] as? String ?? ""
        let courseID = row["course_id"] as? String ?? ""
        let rawName = row["name"] as? String ?? row["course_name"] as? String ?? "课程"
        let matches = courses.filter { $0.name == rawName }
        course = courses.first { $0.id == courseID } ?? (matches.count == 1 ? matches.first : nil)
        name = course?.name ?? rawName
        let rawTime = row["time"] as? String ?? ""
        let parts = rawTime.replacingOccurrences(of:"–", with:"-").replacingOccurrences(of:"—", with:"-").split(separator:"-").map(String.init)
        let first = row["start_time"] as? String ?? parts.first ?? ""
        let last = row["end_time"] as? String ?? (parts.count == 2 ? parts[1] : "")
        let from = Self.minutes(first), to = Self.minutes(last)
        if let from, let to, to > from {
            start = from; end = to
            time = "\(first.trimmingCharacters(in:.whitespaces))–\(last.trimmingCharacters(in:.whitespaces))"
        } else { start = nil; end = nil; time = rawTime.isEmpty ? "时间待确认" : rawTime }
    }

    private static func minutes(_ value: String) -> Int? {
        let parts = value.trimmingCharacters(in:.whitespaces).split(separator:":")
        guard parts.count == 2, let hour = Int(parts[0]), let minute = Int(parts[1]),
              (0...24).contains(hour), (0...59).contains(minute), hour < 24 || minute == 0 else { return nil }
        return hour * 60 + minute
    }
}

struct TimetableWeek {
    static var calendar: Calendar {
        var value = Calendar(identifier:.gregorian)
        value.timeZone = TimeZone(identifier:"Asia/Shanghai")!
        value.firstWeekday = 2
        return value
    }
    static func dateKey(_ date: Date) -> String {
        let formatter = DateFormatter()
        formatter.calendar = calendar; formatter.timeZone = calendar.timeZone
        formatter.locale = Locale(identifier:"en_US_POSIX"); formatter.dateFormat = "yyyy-MM-dd"
        return formatter.string(from:date)
    }
    let days: [Date]
    let entries: [TimetableEntry]
    init(rows: [[String:Any]], courses: [Course], now: Date = Date()) {
        let start = Self.calendar.dateInterval(of:.weekOfYear, for:now)!.start
        days = (0..<7).compactMap { Self.calendar.date(byAdding:.day, value:$0, to:start) }
        let keys = Set(days.map(Self.dateKey))
        entries = rows.enumerated().map { TimetableEntry(row:$0.element, index:$0.offset, courses:courses) }
            .filter { keys.contains($0.date) }
            .sorted { ($0.date, $0.start ?? Int.max, $0.name, $0.id) < ($1.date, $1.start ?? Int.max, $1.name, $1.id) }
    }
}

/// Split concurrent classes into lanes instead of drawing one over another.
struct TimetablePlacement: Identifiable {
    var id: String { entry.id }
    let entry: TimetableEntry
    let lane: Int
    var lanes: Int
    static func layout(_ entries: [TimetableEntry]) -> [TimetablePlacement] {
        let ordered = entries.filter { $0.start != nil && $0.end != nil }.sorted { $0.start! < $1.start! }
        var result: [TimetablePlacement] = [], group: [TimetablePlacement] = []
        var laneEnds: [Int] = []
        var groupEnd = -1
        func finishGroup() {
            for var item in group { item.lanes = laneEnds.count; result.append(item) }
            group = []; laneEnds = []
        }
        for entry in ordered {
            let start = entry.start!, end = entry.end!
            if start >= groupEnd { finishGroup(); groupEnd = -1 }
            let lane = laneEnds.firstIndex { $0 <= start } ?? laneEnds.count
            if lane == laneEnds.count { laneEnds.append(end) } else { laneEnds[lane] = end }
            group.append(TimetablePlacement(entry:entry, lane:lane, lanes:1))
            groupEnd = max(groupEnd, end)
        }
        finishGroup()
        return result
    }
}
