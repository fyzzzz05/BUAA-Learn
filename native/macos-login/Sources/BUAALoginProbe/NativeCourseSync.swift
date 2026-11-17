import Foundation

/// Persists complete school responses after network reads have succeeded.
@MainActor
final class NativeCourseSync {
    private let owner: String
    private let store: NativeStore
    private let school: NativeSchoolSession
    init(owner: String, store: NativeStore, school: NativeSchoolSession) throws {
        guard !owner.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { throw ProbeError.message("请先登录学校账号。") }
        self.owner = owner; self.store = store; self.school = school
    }

    func syncSemester(_ term: String, syncedAt: String) async throws -> [Course] {
        let courses = try await school.courses(term: term)
        var records: [[String:Any]] = courses.map { course in
            ["kind":"course", "id":course.id, "body":["id":course.id, "name":course.name,
             "task_id":course.taskID, "semester":course.semester, "code":course.code,
             "teachers":course.teachers, "synced_at":syncedAt]]
        }
        records.append(["kind":"meta", "id":"semester", "body":["id":term, "synced_at":syncedAt]])
        try store.mergeSchool(owner: owner, records: records)
        return courses
    }

    /// `recordingID` comes from the authenticated cross-system entry, never from user text.
    func syncCatalogue(courseID: String, recordingID: String, syncedAt: String) async throws -> [ProbeLesson] {
        guard !recordingID.isEmpty,
              let course = try store.get(owner: owner, kind: "course", id: courseID) as? [String:Any],
              let courseName = course["name"] as? String else { throw ProbeError.message("未找到这门课程，请先同步课程。") }
        let lessons = try await school.catalogue(course: recordingID)
        var records: [[String:Any]] = []
        for lesson in lessons {
            let lessonRecordingID = lesson.recordingCourseID.isEmpty ? recordingID : lesson.recordingCourseID
            var url = URLComponents(string: "https://classroom.msa.buaa.edu.cn/livingroom")!
            url.queryItems = [URLQueryItem(name:"tenant_code",value:"21"), URLQueryItem(name:"course_id",value:recordingID),
                              URLQueryItem(name:"sub_id",value:lesson.id), URLQueryItem(name:"sub_public",value:"1")]
            let date = Date(timeIntervalSince1970: TimeInterval(lesson.startAt))
            let formatter = ISO8601DateFormatter(); formatter.timeZone = TimeZone(identifier:"Asia/Shanghai")!
            let body: [String:Any] = ["id":lesson.id, "course_id":courseID, "recording_course_id":lessonRecordingID,
                                      "title":lesson.title, "course_name":courseName, "date":formatter.string(from:date),
                                      "teachers":lesson.teachers, "duration":lesson.duration, "has_recording":lesson.hasRecording, "source_url":url.string ?? ""]
            records.append(["kind":"lesson", "id":lesson.id, "body":body])
        }
        records.append(["kind":"course", "id":courseID, "body":["recording_id":recordingID, "checked_at":syncedAt,
                            "sync_error":"", "lesson_count":lessons.count, "recording_count":lessons.filter(\.hasRecording).count]])
        try store.mergeSchool(owner: owner, records: records)
        return lessons
    }
}
