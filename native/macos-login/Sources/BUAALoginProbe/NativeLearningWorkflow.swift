import Foundation

/// Coordinates account-scoped saved material and analysis; the UI supplies selections.
@MainActor
final class NativeLearningWorkflow {
    private let owner: String
    private let store: NativeStore
    private let analysis: NativeAnalysis

    init(owner: String, store: NativeStore, analysis: NativeAnalysis) throws {
        guard !owner.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw ProbeError.message("请先登录学校账号。")
        }
        self.owner = owner
        self.store = store
        self.analysis = analysis
    }

    static func fetchMaterials(owner: String, store: NativeStore, school: NativeSchoolSession, recordingCourseID: String, lessonID: String) async throws -> [String:Any] {
        guard let lesson = try store.get(owner: owner, kind: "lesson", id: lessonID) as? [String:Any] else {
            throw ProbeError.message("未找到这节课，请先同步课程。")
        }
        guard lesson["recording_course_id"] as? String == recordingCourseID,
              lesson["has_recording"] as? Bool == true else { throw ProbeError.message("课次录播信息无效，请重新同步课程。") }
        let duration = (lesson["duration"] as? NSNumber)?.doubleValue ?? 0
        var material = try await school.materials(course: recordingCourseID, lesson: lessonID, duration: duration)
        guard material["lesson_id"] as? String == lessonID else { throw ProbeError.message("课堂材料归属异常。") }
        try store.put(owner: owner, kind: "materials", id: lessonID, value: material)
        let missing = try await SlideAssetCache.acquire(rows:material["slides"] as? [[String:Any]] ?? [], owner:owner,store:store)
        material["uncached_slides"] = missing
        try store.put(owner:owner,kind:"materials",id:lessonID,value:material)
        return material
    }

    func analyze(lessonID: String, modelName: String, createdAt: String) async throws -> [String:Any] {
        guard let lesson = try store.get(owner: owner, kind: "lesson", id: lessonID) as? [String:Any],
              let courseID = lesson["course_id"] as? String,
              let course = try store.get(owner: owner, kind: "course", id: courseID) as? [String:Any],
              let courseName = course["name"] as? String, let title = lesson["title"] as? String,
              let material = try store.get(owner: owner, kind: "materials", id: lessonID) as? [String:Any],
              let digest = material["digest"] as? String else {
            throw ProbeError.message("请先同步课程并获取课堂材料。")
        }
        let info: [String:Any] = ["id":lessonID, "course_id":courseID, "course_name":courseName,
                                 "title":title, "duration":lesson["duration"] as? Double ?? 0]
        let result = try await analysis.analyze(input: ["lesson":info, "materials":material,
                                                         "model":modelName, "created_at":createdAt])
        _ = try store.saveAnalysis(owner: owner, analysis: result, expectedDigest: digest)
        return result
    }

}
