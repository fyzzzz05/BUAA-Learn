import Foundation
import SwiftUI
import XCTest
@testable import BUAALoginProbe

final class TimetableBridgeTests: XCTestCase {
    @MainActor
    func testBridgePayloadTracksBindingsAndKeepsCourseRoutingData() throws {
        var number: Int? = 9
        var weekdays = true
        let course = Course(id:"c1",name:"课程",taskID:"",semester:"2026-20271",code:"C1",teachers:"教师")
        let view = ScheduleTimetable(workspace:7,rows:[[
            "date":"2026-11-05","name":"课程","course_id":"c1","room":"J1","time":"15:50-17:25",
        ]],fullSemester:false,semester:TimetableSemester(id:course.semester),
          selectedWeek:Binding(get:{ number },set:{ number = $0 }),
          weekdaysOnly:Binding(get:{ weekdays },set:{ weekdays = $0 }),
          courses:[course],syncing:false,canSync:false,sync:{},open:{ _,_ in })
        let coordinator = view.makeCoordinator()
        let before = coordinator.parent.renderPayload()
        XCTAssertEqual(before["workspace"] as? Int,7)
        XCTAssertEqual(before["week"] as? Int,9)
        let entries = try XCTUnwrap(before["entries"] as? [[String:Any]])
        XCTAssertEqual(entries.first?["courseID"] as? String,"c1")
        XCTAssertEqual(entries.first?["date"] as? String,"2026-11-05")
        XCTAssertEqual(entries.first?["teachers"] as? String,"教师")
        XCTAssertTrue(JSONSerialization.isValidJSONObject(before))
        coordinator.parent.selectedWeek = 10
        coordinator.parent.weekdaysOnly = false
        let after = coordinator.parent.renderPayload()
        XCTAssertEqual(after["week"] as? Int,10)
        XCTAssertEqual(after["weekdaysOnly"] as? Bool,false)
        XCTAssertTrue((after["entries"] as? [Any] ?? []).isEmpty)
        XCTAssertEqual(before["courseKeys"] as? [String],after["courseKeys"] as? [String])
    }
}
