import XCTest
@testable import BUAALoginProbe

final class LessonExportTests: XCTestCase {
    func testMarkdownIncludesTimelineAndPreservesAssignmentDetails() {
        let lesson: [String:Any] = ["title":"第一讲", "course_name":"示例课程", "source_url":"https://classroom.msa.buaa.edu.cn/example"]
        let analysis: [String:Any] = ["timeline_status":"partial", "sections":[[
            "start":60, "end":125, "title":"主题", "summary":"课堂内容",
            "knowledge_points":[["text":"知识"]],
            "assignments":[["text":"交作业", "deadline":"周五", "audience":"全班", "submission":"线上"]]
        ]]]
        let text = LessonNoteExport.markdown(lesson:lesson, analysis:analysis)
        XCTAssertTrue(text.contains("# 示例课程 · 第一讲"))
        XCTAssertTrue(text.contains("00:01:00–00:02:05 · 主题"))
        XCTAssertTrue(text.contains("部分时间段尚未整理完成"))
        XCTAssertTrue(text.contains("截止：周五；对象：全班；提交：线上"))
        XCTAssertFalse(text.contains("## 作业"))
    }

    func testLegacyNoteWithoutTimelineAndUnsafeSource() {
        let text = LessonNoteExport.markdown(lesson:["source_url":"https://example.com/private"], analysis:[:])
        XCTAssertTrue(text.contains("尚无课堂时间轴"))
        XCTAssertFalse(text.contains("example.com"))
    }
}
