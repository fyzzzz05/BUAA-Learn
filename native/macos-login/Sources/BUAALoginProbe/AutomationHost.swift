import Foundation
import Darwin

/// Prevent more than one app process from scheduling the same local database.
final class AutomationLease {
    private let descriptor: Int32
    init?(directory: URL, filename: String = "automation.lock") {
        let path = directory.appendingPathComponent(filename).path
        let fd = open(path, O_CREAT | O_RDWR | O_CLOEXEC, mode_t(0o600))
        guard fd >= 0 else { return nil }
        guard flock(fd, LOCK_EX | LOCK_NB) == 0 else { close(fd); return nil }
        descriptor = fd
    }
    deinit { flock(descriptor, LOCK_UN); close(descriptor) }
}

@MainActor
final class AutomationHost: AutomationSchedulerTransport {
    let schedule: (Date) async throws -> Void
    let fetchCatalogue: (String) async throws -> [AutomationLesson]
    let fetchMaterials: (AutomationLesson) async throws -> (segments: Int, slides: Int)
    let analyzeLesson: (String) async throws -> Void
    init(schedule: @escaping (Date) async throws -> Void,
         catalogue: @escaping (String) async throws -> [AutomationLesson],
         materials: @escaping (AutomationLesson) async throws -> (segments: Int, slides: Int),
         analyze: @escaping (String) async throws -> Void) {
        self.schedule = schedule; self.fetchCatalogue = catalogue; self.fetchMaterials = materials; self.analyzeLesson = analyze
    }
    func synchronizeSchedule(now: Date) async throws { try await schedule(now) }
    func catalogue(courseID: String) async throws -> [AutomationLesson] { try await fetchCatalogue(courseID) }
    func materials(lesson: AutomationLesson) async throws -> (segments: Int, slides: Int) { try await fetchMaterials(lesson) }
    func analyze(lessonID: String) async throws { try await analyzeLesson(lessonID) }
}
