import Foundation
import XCTest
@testable import BUAALoginProbe

final class TimetableTests: XCTestCase {
    private let course = Course(id:"c1", name:"示例课程", taskID:"", semester:"term", code:"C1", teachers:"教师")

    func testWeekUsesShanghaiMondayAndExcludesOtherWeeks() throws {
        let now = try XCTUnwrap(ISO8601DateFormatter().date(from:"2026-11-08T16:00:00Z"))
        let rows: [[String:Any]] = ["2026-11-08", "2026-11-09", "2026-11-15", "2026-11-16"].map {
            ["date":$0, "course_id":"c1", "time":"08:00-09:35"]
        }
        let week = TimetableWeek(rows:rows, courses:[course], now:now)
        XCTAssertEqual(week.days.map(TimetableWeek.dateKey), ["2026-11-09","2026-11-10","2026-11-11","2026-11-12","2026-11-13","2026-11-14","2026-11-15"])
        XCTAssertEqual(week.entries.map(\.date), ["2026-11-09","2026-11-15"])
        XCTAssertEqual(week.entries.first?.course?.id, "c1")
    }

    func testUnknownTimesArePreservedAndCourseNamesMustResolveUniquely() {
        let invalid = TimetableEntry(row:["date":"2026-11-09", "name":"示例课程", "time":"待定"], index:0, courses:[course])
        XCTAssertNil(invalid.start)
        XCTAssertEqual(invalid.time,"待定")
        XCTAssertEqual(invalid.course?.id,"c1")
        let duplicate = Course(id:"c2", name:course.name, taskID:"", semester:"term", code:"C2", teachers:"")
        let ambiguous = TimetableEntry(row:["name":course.name], index:0, courses:[course,duplicate])
        XCTAssertNil(ambiguous.course)
        let range = TimetableEntry(row:["start_time":"14:00", "end_time":"15:35", "course_id":"c1"], index:0, courses:[course])
        XCTAssertEqual(range.start,840)
        XCTAssertEqual(range.end,935)
        XCTAssertEqual(range.name,course.name)
        for time in ["25:00-26:00", "09:35-08:00", "08:61-09:35"] {
            XCTAssertNil(TimetableEntry(row:["time":time], index:0, courses:[]).start)
        }
    }

    func testSemesterWeeksFollowOfficialCalendarAndCrossYearBoundary() throws {
        let semester = try XCTUnwrap(TimetableSemester(id:"2026-20271"))
        XCTAssertEqual(semester.weeks,19)
        XCTAssertEqual(TimetableWeek.dateKey(semester.date(week:1)),"2026-09-07")
        XCTAssertEqual(TimetableWeek.dateKey(semester.date(week:10)),"2026-11-09")
        XCTAssertEqual(TimetableWeek.dateKey(semester.date(week:19)),"2027-01-11")
        XCTAssertEqual(TimetableWeek.dateKey(semester.end),"2027-01-17")
        XCTAssertEqual(semester.currentWeek(now:try XCTUnwrap(TimetableSemester.parseDate("2026-11-09"))),10)
        XCTAssertNil(semester.currentWeek(now:try XCTUnwrap(TimetableSemester.parseDate("2026-09-06"))))
        XCTAssertNil(semester.currentWeek(now:try XCTUnwrap(TimetableSemester.parseDate("2027-01-18"))))
        XCTAssertNil(TimetableSemester(id:"unknown"))
    }

    @MainActor
    func testWholeSemesterSyncCachesAllRangesWithoutChangingAutomationSchedule() async throws {
        let config = URLSessionConfiguration.ephemeral; config.protocolClasses = [SemesterScheduleStub.self]
        let session = URLSession(configuration:config); defer { session.invalidateAndCancel() }
        let school = try NativeSchoolSession(token:"fixture", refresh:"", role:"01", session:session)
        let store = try NativeStore(inMemory:())
        try store.put(owner:"alice", kind:"meta", id:"automation_schedule", value:["rows":[], "synced_at":"sentinel"])
        let semester = try XCTUnwrap(TimetableSemester(id:"2026-20271"))
        var progress: [Int] = []
        let rows = try await NativeSemesterSchedule.sync(semester:semester, school:school, store:store, owner:"alice") { done, _ in progress.append(done) }
        XCTAssertEqual(progress,[0,1,2,3,4,5])
        XCTAssertEqual(rows.count,10)
        XCTAssertEqual(rows.first?["date"] as? String,"2026-09-07")
        XCTAssertEqual(rows.last?["date"] as? String,"2027-01-17")
        let saved = try XCTUnwrap(store.get(owner:"alice", kind:"meta", id:"semester_schedule") as? [String:Any])
        XCTAssertEqual(saved["semester"] as? String,semester.id)
        XCTAssertEqual((saved["rows"] as? [[String:Any]])?.count,10)
        XCTAssertNil(try store.get(owner:"bob", kind:"meta", id:"semester_schedule"))
        XCTAssertEqual((try store.get(owner:"alice", kind:"meta", id:"automation_schedule") as? [String:Any])?["synced_at"] as? String,"sentinel")
    }

    @MainActor
    func testIncompleteSemesterSyncKeepsPreviousCompleteCache() async throws {
        let config = URLSessionConfiguration.ephemeral; config.protocolClasses = [FailingSemesterScheduleStub.self]
        let session = URLSession(configuration:config); defer { session.invalidateAndCancel() }
        let school = try NativeSchoolSession(token:"fixture", refresh:"", role:"01", session:session)
        let store = try NativeStore(inMemory:())
        try store.put(owner:"alice", kind:"meta", id:"semester_schedule", value:["synced_at":"old-complete"])
        do {
            _ = try await NativeSemesterSchedule.sync(semester:try XCTUnwrap(TimetableSemester(id:"2026-20271")), school:school, store:store, owner:"alice") { _, _ in }
            XCTFail("A partial response must not replace the complete semester")
        } catch {
            XCTAssertEqual((try store.get(owner:"alice", kind:"meta", id:"semester_schedule") as? [String:Any])?["synced_at"] as? String,"old-complete")
        }
    }

    func testOverlappingClassesHaveSeparateLanesAndTouchingClassesDoNot() {
        let entries = ["08:00-09:35", "09:00-10:35", "10:00-11:00", "11:00-12:00"].enumerated().map {
            TimetableEntry(row:["time":$0.element, "course_id":"c1"], index:$0.offset, courses:[course])
        }
        let placements = TimetablePlacement.layout(entries)
        XCTAssertEqual(placements.count,4)
        XCTAssertEqual(placements.prefix(3).map(\.lanes),[2,2,2])
        XCTAssertEqual(placements.last?.lanes,1)
        for a in placements {
            for b in placements where a.id != b.id && a.lane == b.lane {
                XCTAssertTrue(a.entry.end! <= b.entry.start! || b.entry.end! <= a.entry.start!)
            }
        }
    }
}

private class SemesterScheduleStub: URLProtocol {
    class var fails: Bool { false }
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        let url = request.url!
        let query = URLComponents(url:url, resolvingAgainstBaseURL:false)?.queryItems ?? []
        let first = query.first { $0.name == "zksrq" }?.value ?? ""
        let last = query.first { $0.name == "zjsrq" }?.value ?? ""
        if Self.fails && first != "2026-09-07" {
            client?.urlProtocol(self, didFailWithError:URLError(.notConnectedToInternet)); return
        }
        let body: [String:Any] = ["code":200, "content":[first,last].map { ["kcid":"c1", "kcmc":"示例课程", "rq":$0, "strkcsj":"08:00-09:35", "skdd":"J1"] }]
        client?.urlProtocol(self, didReceive:HTTPURLResponse(url:url, statusCode:200, httpVersion:nil, headerFields:nil)!, cacheStoragePolicy:.notAllowed)
        client?.urlProtocol(self, didLoad:try! JSONSerialization.data(withJSONObject:body))
        client?.urlProtocolDidFinishLoading(self)
    }
    override func stopLoading() {}
}
private final class FailingSemesterScheduleStub: SemesterScheduleStub {
    override class var fails: Bool { true }
}
