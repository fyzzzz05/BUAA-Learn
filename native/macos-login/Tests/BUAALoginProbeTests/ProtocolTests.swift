import Foundation
import XCTest
import AppKit
import PDFKit
import ImageIO
@testable import BUAALoginProbe

final class ProtocolTests: XCTestCase {
    func testAutomationLeaseAllowsOnlyOneScheduler() throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: false)
        defer { try? FileManager.default.removeItem(at: folder) }
        var first: AutomationLease? = AutomationLease(directory:folder)
        XCTAssertNotNil(first)
        XCTAssertNil(AutomationLease(directory:folder))
        first = nil
        XCTAssertNotNil(AutomationLease(directory:folder))
    }

    @MainActor
    func testAutomationSaveReturnsPerCourseResultsAndRejectsStaleWorkspace() throws {
        let store = try NativeStore(inMemory: ())
        try store.put(owner:"alice", kind:"course", id:"1", value:["id":"1", "name":"示例"])
        let school = try NativeSchoolSession(token:"example", refresh:"", role:"01")
        let probe = Probe(credentials:MemoryModelCredentials())
        try probe.attachSchool(session:school, store:store, account:"alice")
        let changes: [String:Any] = ["1":["enabled":true, "auto_analyze":false], "missing":["enabled":true, "auto_analyze":false]]
        let result = probe.saveAutomation(changes:changes, workspace:probe.workspaceRevision)
        XCTAssertEqual((result["saved"] as? [[String:Any]])?.count, 1)
        XCTAssertEqual((result["errors"] as? [[String:String]])?.first?["id"], "missing")
        XCTAssertEqual((try store.get(owner:"alice", kind:"automation", id:"1") as? [String:Any])?["enabled"] as? Bool, true)
        let stale = probe.saveAutomation(changes:changes, workspace:probe.workspaceRevision + 1)
        XCTAssertEqual((stale["saved"] as? [[String:Any]])?.count, 0)
    }
    @MainActor
    func testAutomationConfigurationAndTimetablePersistPerAccount() async throws {
        let config = URLSessionConfiguration.ephemeral; config.protocolClasses = [PortalStub.self]
        let transport = URLSession(configuration: config); defer { transport.invalidateAndCancel() }
        let school = try NativeSchoolSession(token: "example", refresh: "", role: "01", session: transport)
        let store = try NativeStore(inMemory: ())
        try store.put(owner: "alice", kind: "course", id: "1", value: ["id":"1", "name":"示例"])
        let rules = try NativeAutomationRules(owner: "alice", store: store)
        let now = "2026-10-22T08:00:00+08:00"
        XCTAssertThrowsError(try rules.configure(courseID:"1", enabled:false, autoAnalyze:true, modelReady:true, now:now))
        XCTAssertThrowsError(try rules.configure(courseID:"1", enabled:true, autoAnalyze:true, modelReady:false, now:now))
        XCTAssertThrowsError(try rules.configure(courseID:"other", enabled:true, autoAnalyze:false, modelReady:false, now:now))
        let initial = try rules.configure(courseID:"1", enabled:true, autoAnalyze:false, modelReady:false, now:now)
        XCTAssertEqual(initial["tracking_from"] as? String, "2026-10-21")
        let date = try XCTUnwrap(ISO8601DateFormatter().date(from: now))
        _ = try await rules.syncSchedule(school:school, now:date)
        let planned = try XCTUnwrap(try store.get(owner:"alice", kind:"automation", id:"1") as? [String:Any])
        XCTAssertEqual(planned["pending_count"] as? Int, 1)
        XCTAssertNotNil(planned["next_run"] as? String)
        XCTAssertNil(try store.get(owner:"bob", kind:"automation", id:"1"))
        let paused = try rules.configure(courseID:"1", enabled:false, autoAnalyze:false, modelReady:false, now:now)
        XCTAssertTrue(paused["next_run"] is NSNull)
        XCTAssertEqual((paused["watches"] as? [String:Any])?.count, 1)
    }

    @MainActor
    func testAutomationRetriesBeforeNetworkAndMarksOnlyCompletedLessonsKnown() async throws {
        let store = try NativeStore(inMemory: ())
        try store.put(owner:"alice", kind:"course", id:"1", value:["id":"1"])
        try store.put(owner:"alice", kind:"meta", id:"automation_schedule", value:["rows":[["course_id":"1", "date":"2026-10-21", "time":"08:00-09:35"]]])
        let nowText = "2026-10-22T08:00:00+08:00"
        let now = try XCTUnwrap(ISO8601DateFormatter().date(from:nowText))
        let rules = try NativeAutomationRules(owner:"alice", store:store)
        _ = try rules.configure(courseID:"1", enabled:true, autoAnalyze:true, modelReady:true, now:nowText)
        let service = FakeAutomationTransport()
        service.lessons = [AutomationLesson(id:"l1", start:try XCTUnwrap(ISO8601DateFormatter().date(from:"2026-10-21T08:10:00+08:00")), hasRecording:true, recordingCourseID:"42")]
        service.onCatalogue = {
            let rule = try store.get(owner:"alice", kind:"automation", id:"1") as? [String:Any]
            let watch = (rule?["watches"] as? [String:[String:Any]])?.values.first
            XCTAssertNotNil(watch?["next_check"] as? String)
        }
        service.failAnalysis = true
        let runner = try NativeAutomationRunner(owner:"alice", store:store)
        do { _ = try await runner.run(courseID:"1", now:now, transport:service); XCTFail("analysis failure accepted") } catch {}
        let failed = try XCTUnwrap(try store.get(owner:"alice", kind:"automation", id:"1") as? [String:Any])
        XCTAssertTrue((failed["known"] as? [String] ?? []).isEmpty)
        let early = try await runner.run(courseID:"1", now:now.addingTimeInterval(60), transport:service)
        XCTAssertEqual(early, 0)
        try rules.retry(courseID:"1", now:ISO8601DateFormatter().string(from:now.addingTimeInterval(60)))
        service.failAnalysis = false
        let count = try await runner.run(courseID:"1", now:now.addingTimeInterval(60), transport:service)
        XCTAssertEqual(count, 1)
        let completed = try XCTUnwrap(try store.get(owner:"alice", kind:"automation", id:"1") as? [String:Any])
        XCTAssertEqual(completed["known"] as? [String], ["l1"])
        XCTAssertEqual(completed["pending_count"] as? Int, 0)
    }

    @MainActor
    func testAutomationPauseDuringMaterialFetchDoesNotAnalyzeOrMarkKnown() async throws {
        let store = try NativeStore(inMemory: ())
        try store.put(owner:"alice", kind:"course", id:"1", value:["id":"1"])
        try store.put(owner:"alice", kind:"meta", id:"automation_schedule", value:["rows":[["course_id":"1", "date":"2026-10-21", "time":"08:00-09:35"]]])
        let nowText = "2026-10-22T08:00:00+08:00"
        let now = try XCTUnwrap(ISO8601DateFormatter().date(from:nowText))
        let rules = try NativeAutomationRules(owner:"alice", store:store)
        _ = try rules.configure(courseID:"1", enabled:true, autoAnalyze:true, modelReady:true, now:nowText)
        let service = FakeAutomationTransport()
        service.lessons = [AutomationLesson(id:"l1", start:try XCTUnwrap(ISO8601DateFormatter().date(from:"2026-10-21T08:10:00+08:00")), hasRecording:true, recordingCourseID:"42")]
        service.onMaterials = { _ = try rules.configure(courseID:"1", enabled:false, autoAnalyze:false, modelReady:true, now:nowText) }
        let runner = try NativeAutomationRunner(owner:"alice", store:store)
        let count = try await runner.run(courseID:"1", now:now, transport:service)
        XCTAssertEqual(count, 0)
        XCTAssertEqual(service.analysisCount, 0)
        let result = try XCTUnwrap(try store.get(owner:"alice", kind:"automation", id:"1") as? [String:Any])
        XCTAssertEqual(result["enabled"] as? Bool, false)
        XCTAssertTrue((result["known"] as? [String] ?? []).isEmpty)
    }

    @MainActor
    func testSchedulerThrottlesFailedTimetableSyncAndRecordsFailure() async throws {
        let store = try NativeStore(inMemory: ())
        try store.put(owner:"alice", kind:"course", id:"1", value:["id":"1"])
        let nowText = "2026-10-22T08:00:00+08:00"
        let now = try XCTUnwrap(ISO8601DateFormatter().date(from:nowText))
        let rules = try NativeAutomationRules(owner:"alice", store:store)
        _ = try rules.configure(courseID:"1", enabled:true, autoAnalyze:false, modelReady:false, now:nowText)
        let service = FakeAutomationTransport(); service.failSchedule = true
        let scheduler = try NativeAutomationScheduler(owner:"alice", store:store)
        try await scheduler.tick(now:now, transport:service)
        try await scheduler.tick(now:now.addingTimeInterval(60), transport:service)
        XCTAssertEqual(service.scheduleCount, 1)
        let jobs = try store.list(owner:"alice", kind:"job").compactMap { $0 as? [String:Any] }
        XCTAssertEqual(jobs.count, 1)
        XCTAssertEqual(jobs.first?["status"] as? String, "failed")
        try await scheduler.tick(now:now.addingTimeInterval(7200), transport:service)
        XCTAssertEqual(service.scheduleCount, 2)
    }

    @MainActor
    func testSchedulerDoesNotOverlapTicks() async throws {
        let store = try NativeStore(inMemory:())
        try store.put(owner:"alice", kind:"course", id:"1", value:["id":"1"])
        let nowText = "2026-10-22T08:00:00+08:00"
        let now = try XCTUnwrap(ISO8601DateFormatter().date(from:nowText))
        try store.put(owner:"alice", kind:"meta", id:"automation_schedule", value:["rows":[["course_id":"1", "date":"2026-10-21", "time":"08:00-09:35"]], "synced_at":nowText])
        _ = try NativeAutomationRules(owner:"alice", store:store).configure(courseID:"1", enabled:true, autoAnalyze:false, modelReady:false, now:nowText)
        let service = FakeAutomationTransport()
        service.onCatalogueAsync = { try? await Task.sleep(nanoseconds:200_000_000) }
        let scheduler = try NativeAutomationScheduler(owner:"alice", store:store)
        let first = Task { try await scheduler.tick(now:now, transport:service) }
        for _ in 0..<100 where service.catalogueCount == 0 { try await Task.sleep(nanoseconds:10_000_000) }
        guard service.catalogueCount > 0 else { first.cancel(); XCTFail("scheduler did not start"); return }
        try await scheduler.tick(now:now, transport:service)
        try await first.value
        XCTAssertEqual(service.catalogueCount, 1)
    }

    @MainActor
    func testSchedulerProcessesSchoolMaterialsAndModelWithMockServices() async throws {
        let schoolConfig = URLSessionConfiguration.ephemeral; schoolConfig.protocolClasses = [SyncStub.self]
        let schoolSession = URLSession(configuration:schoolConfig); defer { schoolSession.invalidateAndCancel() }
        let modelConfig = URLSessionConfiguration.ephemeral; modelConfig.protocolClasses = [ExtractionStub.self]
        let modelSession = URLSession(configuration:modelConfig); defer { modelSession.invalidateAndCancel() }
        let school = try NativeSchoolSession(token:"example", refresh:"", role:"01", session:schoolSession)
        let store = try NativeStore(inMemory:())
        let sync = try NativeCourseSync(owner:"alice", store:store, school:school)
        _ = try await sync.syncSemester("2026-20271", syncedAt:"2025-10-10T01:00:00Z")
        try await school.setRecording(token:"recording")
        let nowText = "2025-10-10T09:00:00+08:00"
        let now = try XCTUnwrap(ISO8601DateFormatter().date(from:nowText))
        try store.put(owner:"alice", kind:"meta", id:"automation_schedule", value:["rows":[["course_id":"1", "date":"2025-10-09", "time":"16:00-18:00"]], "synced_at":nowText])
        let rules = try NativeAutomationRules(owner:"alice", store:store)
        _ = try rules.configure(courseID:"1", enabled:true, autoAnalyze:true, modelReady:true, now:nowText)
        let model = try ModelService(baseURL:"http://localhost:8080/v1", model:"fixture", session:modelSession)
        let host = AutomationHost(schedule: { _ in XCTFail("fresh schedule was unexpectedly synchronized") }, catalogue: { courseID in
            let lessons = try await sync.syncCatalogue(courseID:courseID, recordingID:"42", syncedAt:nowText)
            return lessons.map { AutomationLesson(id:$0.id, start:Date(timeIntervalSince1970:TimeInterval($0.startAt)), hasRecording:$0.hasRecording, recordingCourseID:$0.recordingCourseID.isEmpty ? "42" : $0.recordingCourseID) }
        }, materials: { lesson in
            let value = try await NativeLearningWorkflow.fetchMaterials(owner:"alice", store:store, school:school, recordingCourseID:lesson.recordingCourseID, lessonID:lesson.id)
            return ((value["segments"] as? [Any])?.count ?? 0, (value["slides"] as? [Any])?.count ?? 0)
        }, analyze: { id in
            let workflow = try NativeLearningWorkflow(owner:"alice", store:store, analysis:NativeAnalysis(model:model))
            _ = try await workflow.analyze(lessonID:id, modelName:"fixture", createdAt:nowText)
        })
        let scheduler = try NativeAutomationScheduler(owner:"alice", store:store)
        try await scheduler.tick(now:now, transport:host)
        XCTAssertNotNil(try store.get(owner:"alice", kind:"materials", id:"7"))
        XCTAssertNotNil(try store.get(owner:"alice", kind:"analysis", id:"7"))
        let rule = try XCTUnwrap(try store.get(owner:"alice", kind:"automation", id:"1") as? [String:Any])
        XCTAssertEqual(rule["pending_count"] as? Int, 0)
        let jobs = try store.list(owner:"alice", kind:"job").compactMap { $0 as? [String:Any] }
        XCTAssertEqual(jobs.first?["status"] as? String, "completed")
        XCTAssertNil(try store.get(owner:"bob", kind:"analysis", id:"7"))
    }

    @MainActor
    func testProbeRunsAutomaticWorkflowWithMockAuthorizationAndServices() async throws {
        let schoolConfig = URLSessionConfiguration.ephemeral; schoolConfig.protocolClasses = [SyncStub.self]
        let schoolSession = URLSession(configuration:schoolConfig); defer { schoolSession.invalidateAndCancel() }
        let modelConfig = URLSessionConfiguration.ephemeral; modelConfig.protocolClasses = [ExtractionStub.self]
        let modelSession = URLSession(configuration:modelConfig); defer { modelSession.invalidateAndCancel() }
        let school = try NativeSchoolSession(token:"example", refresh:"", role:"01", session:schoolSession)
        let store = try NativeStore(inMemory:())
        let sync = try NativeCourseSync(owner:"alice", store:store, school:school)
        _ = try await sync.syncSemester("2026-20271", syncedAt:"2025-10-10T01:00:00Z")
        let nowText = "2025-10-10T09:00:00+08:00"
        let now = try XCTUnwrap(ISO8601DateFormatter().date(from:nowText))
        try store.put(owner:"alice", kind:"meta", id:"automation_schedule", value:["rows":[["course_id":"1", "date":"2025-10-09", "time":"16:00-18:00"]], "synced_at":nowText])
        let rules = try NativeAutomationRules(owner:"alice", store:store)
        _ = try rules.configure(courseID:"1", enabled:true, autoAnalyze:true, modelReady:true, now:nowText)
        let probe = Probe(credentials:MemoryModelCredentials(), makeModel: { try ModelService(baseURL:$0, model:$1, key:$2, session:modelSession) }, recordingAuthorization: { _, _ in (id:"42", token:"recording") })
        try probe.attachSchool(session:school, store:store, account:"alice")
        try probe.configureModel(baseURL:"http://localhost:8080/v1", model:"fixture", key:"")
        probe.signedIn = true
        await probe.automationTick(now:now)
        XCTAssertNotNil(try store.get(owner:"alice", kind:"materials", id:"7"))
        XCTAssertNotNil(try store.get(owner:"alice", kind:"analysis", id:"7"))
        XCTAssertEqual((try store.get(owner:"alice", kind:"automation", id:"1") as? [String:Any])?["pending_count"] as? Int, 0)
        let jobs = try probe.records(kind:"job")
        XCTAssertEqual(jobs.first?["status"] as? String, "completed")
        XCTAssertNil(probe.activeAutomationJobID)
    }
    func testSlideExportDeduplicatesGloballyAndPreservesDestinationOnFailure() async throws {
        func png(_ mark: Bool) throws -> Data {
            let context = try XCTUnwrap(CGContext(data:nil,width:120,height:80,bitsPerComponent:8,bytesPerRow:480,space:CGColorSpaceCreateDeviceRGB(),bitmapInfo:CGImageAlphaInfo.premultipliedLast.rawValue))
            context.setFillColor(NSColor.white.cgColor); context.fill(CGRect(x:0,y:0,width:120,height:80))
            context.setFillColor(NSColor.black.cgColor); context.fill(CGRect(x:10,y:10,width:30,height:20))
            if mark { context.fill(CGRect(x:70,y:40,width:10,height:10)) }
            let image = try XCTUnwrap(context.makeImage()); let data = NSMutableData()
            let dest = try XCTUnwrap(CGImageDestinationCreateWithData(data, "public.png" as CFString, 1, nil))
            CGImageDestinationAddImage(dest,image,nil); XCTAssertTrue(CGImageDestinationFinalize(dest)); return data as Data
        }
        let one = try png(false), two = try png(true)
        let original = try SlideExporter.decode(one), changed = try SlideExporter.decode(two)
        let fingerprint = try SlideFingerprint(image:original,original:Data([1]))
        XCTAssertTrue(fingerprint.matches(try SlideFingerprint(image:original,original:Data([2]))))
        XCTAssertFalse(fingerprint.matches(try SlideFingerprint(image:changed,original:Data([3]))))
        let rows = try ["a","b","c"].enumerated().map { try SlideInput(["url":"https://resource.msa.buaa.edu.cn/\($0.element).png", "start":Double($0.offset)]) }
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory:true)
        try FileManager.default.createDirectory(at:dir,withIntermediateDirectories:false)
        defer { try? FileManager.default.removeItem(at:dir) }
        let output = dir.appendingPathComponent("out.pdf")
        let counts = try await SlideExporter.export(rows:rows,destination:output,progress:{ _ in },fetch:{ url in url.lastPathComponent == "b.png" ? two : one })
        XCTAssertEqual(counts.0, 3); XCTAssertEqual(counts.1, 2)
        XCTAssertEqual(PDFDocument(url:output)?.pageCount, 2)
        let before = try Data(contentsOf:output)
        do { _ = try await SlideExporter.export(rows:rows,destination:output,progress:{ _ in },fetch:{ _ in throw ProbeError.message("failure") }); XCTFail("expected failure") } catch {}
        XCTAssertEqual(try Data(contentsOf:output),before)
        XCTAssertThrowsError(try SlideInput(["url":"https://resource.msa.buaa.edu.cn.evil.test/a.png", "start":1]))
        XCTAssertThrowsError(try SlideInput(["url":"https://resource.msa.buaa.edu.cn/a.png?token=x", "start":1]))
    }
    @MainActor
    func testAnalysisJobShowsProgressThenPersistsSuccessAndFailure() async throws {
        let config = URLSessionConfiguration.ephemeral; config.protocolClasses = [SlowExtractionStub.self]
        let session = URLSession(configuration: config); defer { session.invalidateAndCancel() }
        let store = try NativeStore(inMemory: ())
        try store.put(owner:"alice", kind:"course", id:"course", value:["id":"course", "name":"示例"])
        try store.put(owner:"alice", kind:"lesson", id:"lesson", value:["id":"lesson", "course_id":"course", "title":"第一课", "duration":2])
        try store.put(owner:"alice", kind:"materials", id:"lesson", value:["lesson_id":"lesson", "segments":[["id":"s0", "start":1, "end":2, "text":"下周五提交作业"]], "digest":"fixture", "coverage":[:], "limitations":[], "duration":2])
        let school = try NativeSchoolSession(token:"example", refresh:"", role:"01")
        let probe = Probe(credentials: MemoryModelCredentials(), makeModel: { try ModelService(baseURL:$0, model:$1, key:$2, session:session) })
        try probe.attachSchool(session:school, store:store, account:"alice")
        try probe.configureModel(baseURL:"http://localhost:8080/v1", model:"fixture", key:"")
        let task = Task { try await probe.analyzeLesson("lesson") }
        for _ in 0..<100 {
            if probe.analysisJob?.message.contains("正在分析课堂文本") == true { break }
            try await Task.sleep(nanoseconds:10_000_000)
        }
        XCTAssertEqual(probe.analysisJob?.phase, .running)
        XCTAssertTrue(probe.analysisJob?.message.contains("正在分析课堂文本") == true)
        XCTAssertEqual((try probe.records(kind:"job")).first?["status"] as? String, "running")
        try await task.value
        let success = try XCTUnwrap(try probe.records(kind:"job").first)
        XCTAssertEqual(success["status"] as? String, "completed")
        XCTAssertEqual(success["target"] as? String, "lesson")
        XCTAssertNotNil(try store.get(owner:"alice", kind:"analysis", id:"lesson"))
        try store.put(owner:"alice", kind:"lesson", id:"missing", value:["id":"missing", "course_id":"course", "title":"缺材料"])
        do { try await probe.analyzeLesson("missing"); XCTFail("expected missing materials to fail") } catch {}
        let jobs = try probe.records(kind:"job")
        XCTAssertTrue(jobs.contains { $0["target"] as? String == "missing" && $0["status"] as? String == "failed" })
        XCTAssertTrue(try store.list(owner:"bob", kind:"job").isEmpty)
    }
    @MainActor
    func testModelConfigurationRestoresPerAccountAndDoesNotReuseKeyForAnotherEndpoint() throws {
        let credentials = MemoryModelCredentials()
        let store = try NativeStore(inMemory: ())
        let school = try NativeSchoolSession(token: "example", refresh: "", role: "01")
        let first = Probe(credentials: credentials)
        try first.attachSchool(session: school, store: store, account: "alice")
        try first.configureModel(baseURL: "https://model.example/v1", model: "m1", key: "alice-secret")
        let stored = try XCTUnwrap(try store.get(owner: "alice", kind: "meta", id: "model") as? [String:Any])
        XCTAssertNil(stored["key"])
        XCTAssertEqual(stored["has_key"] as? Bool, true)
        let reopened = Probe(credentials: credentials)
        try reopened.attachSchool(session: school, store: store, account: "alice")
        XCTAssertEqual(reopened.modelBaseURL, "https://model.example/v1")
        XCTAssertEqual(reopened.modelName, "m1")
        XCTAssertTrue(reopened.modelHasKey)
        XCTAssertNotNil(reopened.modelService)
        try reopened.configureModel(baseURL: "https://model.example/v1", model: "m2", key: "")
        XCTAssertTrue(reopened.modelHasKey)
        try reopened.removeModelKey()
        XCTAssertFalse(reopened.modelHasKey)
        XCTAssertNil(credentials.values["alice|https://model.example/v1"])
        try reopened.configureModel(baseURL: "https://model.example/v1", model: "m2", key: "alice-secret")
        try reopened.configureModel(baseURL: "https://other.example/v1", model: "m3", key: "")
        XCTAssertFalse(reopened.modelHasKey)
        XCTAssertNil(credentials.values["alice|https://other.example/v1"])
        try reopened.attachSchool(session: school, store: store, account: "bob")
        XCTAssertNil(reopened.modelService)
        XCTAssertFalse(reopened.modelHasKey)
        XCTAssertEqual(reopened.modelBaseURL, "")
    }

    @MainActor
    func testUnavailableCredentialDoesNotBlockLocalWorkspaceOrSilentlyEraseKey() throws {
        let credentials = MemoryModelCredentials()
        let store = try NativeStore(inMemory: ())
        let school = try NativeSchoolSession(token: "example", refresh: "", role: "01")
        let probe = Probe(credentials: credentials)
        try probe.attachSchool(session: school, store: store, account: "alice")
        try probe.configureModel(baseURL: "https://model.example/v1", model: "m1", key: "secret")
        credentials.failReads = true
        let reopened = Probe(credentials: credentials)
        try reopened.attachSchool(session: school, store: store, account: "alice")
        XCTAssertNil(reopened.modelService)
        XCTAssertFalse(reopened.modelIssue.isEmpty)
        XCTAssertThrowsError(try reopened.configureModel(baseURL: "https://model.example/v1", model: "m1", key: ""))
        XCTAssertEqual(credentials.values["alice|https://model.example/v1"], "secret")
    }

    @MainActor
    func testFailedModelMetadataWriteRestoresPreviousCredential() throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: false)
        defer { try? FileManager.default.removeItem(at: folder) }
        let database = folder.appendingPathComponent("native.sqlite3")
        let store = try NativeStore(url: database)
        let credentials = MemoryModelCredentials()
        let school = try NativeSchoolSession(token: "example", refresh: "", role: "01")
        let probe = Probe(credentials: credentials)
        try probe.attachSchool(session: school, store: store, account: "alice")
        try probe.configureModel(baseURL: "https://model.example/v1", model: "m1", key: "old-secret")
        let process = try Process.run(URL(fileURLWithPath: "/usr/bin/sqlite3"), arguments: [database.path,
            "CREATE TRIGGER reject_model BEFORE INSERT ON records WHEN NEW.kind = 'meta' AND NEW.id = 'model' BEGIN SELECT RAISE(ABORT, 'test failure'); END;"])
        process.waitUntilExit()
        XCTAssertEqual(process.terminationStatus, 0)
        XCTAssertThrowsError(try probe.configureModel(baseURL: "https://model.example/v1", model: "m2", key: "new-secret"))
        XCTAssertEqual(credentials.values["alice|https://model.example/v1"], "old-secret")
        XCTAssertEqual(probe.modelName, "m1")
        XCTAssertEqual((try store.get(owner: "alice", kind: "meta", id: "model") as? [String:Any])?["model"] as? String, "m1")
    }
    func testTimelineSourceUsesSavedEvidenceAndValidatesRanges() {
        let analysis: [String:Any] = ["evidence": [
            "s10": ["start": 2, "end": 3, "text": "旧快照末尾"],
            "s0": ["start": 0, "end": 1, "text": "旧快照开头"],
            "s1": ["start": 1, "end": 2, "text": "旧快照中间"]
        ]]
        let evidence = TimelineEvidence(analysis: analysis)
        XCTAssertEqual(evidence.range(startID: "s0", endID: "s10").map(\.text), ["旧快照开头", "旧快照中间", "旧快照末尾"])
        XCTAssertEqual(evidence.range(for: ["evidence_ids": ["s10", "s0"]]).map(\.id), ["s0", "s1", "s10"])
        XCTAssertTrue(evidence.range(startID: "s10", endID: "s0").isEmpty)
        XCTAssertTrue(evidence.range(for: ["source_range": ["start_id": "missing", "end_id": "s1"], "evidence_ids": ["s0"]]).isEmpty)
        let refreshed = TimelineEvidence(material: ["segments": [["id": "s0", "start": 0, "end": 1, "text": "刷新后的原文"]]])
        XCTAssertEqual(refreshed.rows.first?.text, "刷新后的原文")
        XCTAssertEqual(evidence.range(startID: "s0", endID: "s0").first?.text, "旧快照开头")
    }
    @MainActor
    func testWorkspaceRestoresLastAccountAndItsLastSemesterWithoutSchoolSession() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        do {
            let store = try NativeStore.openApplicationStore(baseDirectory: directory)
            try store.put(owner: "alice", kind: "meta", id: "semester", value: ["id": "2026"])
            try store.put(owner: "alice", kind: "course", id: "current", value: ["id": "current", "name": "本学期", "semester": "2026"])
            try store.put(owner: "alice", kind: "course", id: "old", value: ["id": "old", "name": "旧学期", "semester": "2025"])
            try store.put(owner: "alice", kind: "analysis", id: "lesson", value: ["lesson_id": "lesson", "sections": [["title": "已保存"]]])
            try store.put(owner: "bob", kind: "analysis", id: "lesson", value: ["lesson_id": "lesson", "sections": [["title": "其他账号"]]])
            try NativeStore.rememberAccount("alice", baseDirectory: directory)
        }
        let probe = Probe()
        try probe.restoreWorkspace(baseDirectory: directory)
        XCTAssertFalse(probe.signedIn)
        XCTAssertEqual(probe.selectedTerm, "2026")
        XCTAssertEqual(probe.courses.map(\.id), ["current"])
        let analyses = try probe.records(kind: "analysis")
        XCTAssertEqual((analyses.first?["sections"] as? [[String:Any]])?.first?["title"] as? String, "已保存")
    }
    func testApplicationStorePersistsAndSeparatesAccounts() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        do {
            let store = try NativeStore.openApplicationStore(baseDirectory: directory)
            try store.put(owner: "alice", kind: "materials", id: "lesson", value: ["text": "saved"])
            try store.put(owner: "bob", kind: "materials", id: "lesson", value: ["text": "other"])
        }
        let reopened = try NativeStore.openApplicationStore(baseDirectory: directory)
        XCTAssertEqual((try reopened.get(owner: "alice", kind: "materials", id: "lesson") as? [String:Any])?["text"] as? String, "saved")
        XCTAssertEqual((try reopened.get(owner: "bob", kind: "materials", id: "lesson") as? [String:Any])?["text"] as? String, "other")
    }

    func testOnlySecureSchoolHostsAreAllowed() {
        XCTAssertTrue(schoolHost(URL(string: "https://spoc.buaa.edu.cn/path")!))
        XCTAssertFalse(schoolHost(URL(string: "http://spoc.buaa.edu.cn/path")!))
        XCTAssertFalse(schoolHost(URL(string: "https://buaa.edu.cn.evil.test")!))
    }
    func testOnlyKnownRecordingHTTPRedirectIsUpgraded() {
        XCTAssertEqual(upgradedRecordingURL(URL(string: "http://yjapi.msa.buaa.edu.cn/auth?x=1")!)?.absoluteString,
                       "https://yjapi.msa.buaa.edu.cn/auth?x=1")
        XCTAssertNil(upgradedRecordingURL(URL(string: "http://yjapi.msa.buaa.edu.cn:8080/auth")!))
        XCTAssertNil(upgradedRecordingURL(URL(string: "http://evil.test/auth")!))
    }
    func testRecordingTokenRequiresAllowedCookieDomain() {
        func cookie(_ domain: String) -> HTTPCookie {
            HTTPCookie(properties: [.domain: domain, .path: "/", .name: "_token", .value: "%7Bi%3A0%3Bs%3A6%3A%22_token%22%3Bi%3A1%3Bs%3A6%3A%22secret%22%3B%7D"])!
        }
        XCTAssertEqual(recordingBearer([cookie(".buaa.edu.cn")]), "secret")
        XCTAssertNil(recordingBearer([cookie(".evil.test")]))
    }
    func testCourseIdentifierOnlyComesFromSchoolURL() {
        XCTAssertEqual(courseID(from: URL(string: "https://classroom.msa.buaa.edu.cn/?course_id=123")), "123")
        XCTAssertNil(courseID(from: URL(string: "https://evil.test/?course_id=123")))
        XCTAssertNil(courseID(from: URL(string: "https://classroom.msa.buaa.edu.cn/?course_id=")))
    }

    func testPortalQueriesTermsAndCoursesWithAccountHeaders() async throws {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [PortalStub.self]
        let session = URLSession(configuration: config)
        defer { session.invalidateAndCancel() }
        let client = PortalClient(token: "example", role: "01", session: session)
        let terms = try await client.semesters()
        XCTAssertEqual(terms, ["2026-20271"])
        let courses = try await client.courses(term: terms[0])
        XCTAssertEqual(courses.map(\.name), ["示例课程"])
    }

    func testRustSchoolSessionUsesMacTransportForCourses() async throws {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [PortalStub.self]
        let session = URLSession(configuration: config)
        defer { session.invalidateAndCancel() }
        let school = try NativeSchoolSession(token: "example", refresh: "", role: "01", session: session)
        let terms = try await school.semesters()
        XCTAssertEqual(terms, ["2026-20271"])
        let courses = try await school.courses(term: terms[0])
        XCTAssertEqual(courses.map(\.name), ["示例课程"])
        let rows = try await school.schedule(start: "2026-10-21", end: "2026-10-27")
        XCTAssertEqual(rows.first?["course_id"] as? String, "1")
    }
    @MainActor
    func testCourseSyncReportsSuccessAndFailureInJobHistory() async throws {
        let config = URLSessionConfiguration.ephemeral; config.protocolClasses = [PortalStub.self]
        let session = URLSession(configuration:config); defer { session.invalidateAndCancel() }
        let school = try NativeSchoolSession(token:"example", refresh:"", role:"01", session:session)
        let store = try NativeStore(inMemory:())
        let probe = Probe()
        try probe.attachSchool(session:school, store:store, account:"test")
        probe.selectedTerm = "2026-20271"
        await probe.loadCourses()
        XCTAssertEqual(probe.courseSyncJob?.phase, .completed)
        XCTAssertEqual(probe.courseSyncJob?.message, "已同步 1 门课程。")
        var jobs = try probe.records(kind:"job")
        XCTAssertEqual(jobs.first?["status"] as? String, "completed")
        probe.selectedTerm = "invalid"
        await probe.loadCourses()
        XCTAssertEqual(probe.courseSyncJob?.phase, .failed)
        XCTAssertFalse(probe.courseSyncJob?.message.isEmpty ?? true)
        jobs = try probe.records(kind:"job")
        XCTAssertEqual(jobs.count, 2)
        XCTAssertTrue(jobs.contains { ($0["status"] as? String) == "failed" })
    }
    @MainActor
    func testLatestRecordingsSyncContinuesAfterFailureAndCountsOnlyNewReplays() async throws {
        let config = URLSessionConfiguration.ephemeral; config.protocolClasses = [SyncStub.self]
        let session = URLSession(configuration:config); defer { session.invalidateAndCancel() }
        let school = try NativeSchoolSession(token:"example", refresh:"", role:"01", session:session)
        let store = try NativeStore(inMemory:())
        try store.put(owner:"alice", kind:"meta", id:"semester", value:["id":"current"])
        for (id, term) in [("bad", "current"), ("1", "current"), ("old", "previous")] {
            try store.put(owner:"alice", kind:"course", id:id, value:["id":id, "name":id, "semester":term])
        }
        try store.put(owner:"alice", kind:"lesson", id:"7", value:["id":"7", "course_id":"1", "has_recording":false])
        var checked: [String] = []
        let probe = Probe(credentials:MemoryModelCredentials(), recordingAuthorization: { course, _ in
            checked.append(course.id)
            if course.id == "bad" { throw ProbeError.message("回放暂不可用") }
            return (id:"42", token:"recording")
        })
        try probe.attachSchool(session:school, store:store, account:"alice")
        probe.signedIn = true
        await probe.loadLatestRecordings()
        XCTAssertEqual(checked.sorted(), ["1", "bad"])
        XCTAssertEqual(probe.courseSyncJob?.kind, "recordings")
        XCTAssertEqual(probe.courseSyncJob?.phase, .failed)
        XCTAssertEqual(probe.courseSyncJob?.message, "已同步 1 门课程，新增 1 节回放。同步失败：bad")
        XCTAssertEqual((try store.get(owner:"alice", kind:"lesson", id:"7") as? [String:Any])?["has_recording"] as? Bool, true)
        XCTAssertTrue(try store.list(owner:"alice", kind:"materials").isEmpty)
        XCTAssertTrue(try store.list(owner:"alice", kind:"analysis").isEmpty)
        await probe.retryCourseSync()
        XCTAssertEqual(probe.courseSyncJob?.message, "已同步 1 门课程，新增 0 节回放。同步失败：bad")
        XCTAssertFalse(probe.working)
        XCTAssertEqual((try probe.records(kind:"job").first)?["kind"] as? String, "recordings")
    }

    @MainActor
    func testCourseSyncShowsRunningWhileNetworkIsPending() async throws {
        let config = URLSessionConfiguration.ephemeral; config.protocolClasses = [SlowSyncStub.self]
        let session = URLSession(configuration:config); defer { session.invalidateAndCancel() }
        let school = try NativeSchoolSession(token:"example", refresh:"", role:"01", session:session)
        let probe = Probe()
        try probe.attachSchool(session:school, store:NativeStore(inMemory:()), account:"test")
        probe.selectedTerm = "2026-20271"
        let task = Task { await probe.loadCourses() }
        try await Task.sleep(nanoseconds:50_000_000)
        XCTAssertTrue(probe.working)
        XCTAssertEqual(probe.courseSyncJob?.phase, .running)
        XCTAssertEqual(probe.courseSyncJob?.message, "正在同步本学期课程…")
        await task.value
        XCTAssertFalse(probe.working)
        XCTAssertEqual(probe.courseSyncJob?.phase, .completed)
    }

    @MainActor
    func testSyncPreservesLocalCourseAndSavesCompleteCatalogue() async throws {
        let config = URLSessionConfiguration.ephemeral; config.protocolClasses = [SyncStub.self]
        let session = URLSession(configuration: config); defer { session.invalidateAndCancel() }
        let school = try NativeSchoolSession(token:"example", refresh:"", role:"01", session:session)
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory:true)
        try FileManager.default.createDirectory(at:folder, withIntermediateDirectories:false)
        defer { try? FileManager.default.removeItem(at:folder) }
        let store = try NativeStore(url:folder.appendingPathComponent("native.sqlite3"))
        try store.put(owner:"alice", kind:"course", id:"1", value:["id":"1", "local":"保留"])
        let sync = try NativeCourseSync(owner:"alice", store:store, school:school)
        let courses = try await sync.syncSemester("2026-20271", syncedAt:"2026-10-29")
        let entry = try await school.recordingEntry(course: courses[0])
        XCTAssertEqual(entry.host, "classroom.msa.buaa.edu.cn")
        XCTAssertTrue((entry.query ?? "").contains("bind_id=example"))
        try await school.setRecording(token:"recording")
        let lessons = try await sync.syncCatalogue(courseID:"1", recordingID:"42", syncedAt:"2026-10-29")
        XCTAssertEqual(lessons.first?.duration, 3600)
        let course = try XCTUnwrap(try store.get(owner:"alice", kind:"course", id:"1") as? [String:Any])
        XCTAssertEqual(course["local"] as? String, "保留")
        XCTAssertEqual(course["recording_count"] as? Int, 1)
        let lesson = try XCTUnwrap(try store.get(owner:"alice", kind:"lesson", id:"7") as? [String:Any])
        XCTAssertEqual(lesson["course_id"] as? String, "1")
        XCTAssertEqual(lesson["duration"] as? Int, 3600)
        XCTAssertTrue((lesson["source_url"] as? String ?? "").contains("course_id=42"))
        XCTAssertNil(try store.get(owner:"bob", kind:"lesson", id:"7"))
    }

    @MainActor
    func testSyncMaterialAnalysisAndReopenUsingMockServices() async throws {
        let schoolConfig = URLSessionConfiguration.ephemeral; schoolConfig.protocolClasses = [SyncStub.self]
        let schoolSession = URLSession(configuration: schoolConfig); defer { schoolSession.invalidateAndCancel() }
        let modelConfig = URLSessionConfiguration.ephemeral; modelConfig.protocolClasses = [ExtractionStub.self]
        let modelSession = URLSession(configuration: modelConfig); defer { modelSession.invalidateAndCancel() }
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: false)
        defer { try? FileManager.default.removeItem(at: folder) }
        let url = folder.appendingPathComponent("native.sqlite3")
        let store = try NativeStore(url: url)
        let school = try NativeSchoolSession(token: "example", refresh: "", role: "01", session: schoolSession)
        let sync = try NativeCourseSync(owner: "alice", store: store, school: school)
        _ = try await sync.syncSemester("2026-20271", syncedAt: "2026-10-29")
        try await school.setRecording(token: "recording")
        _ = try await sync.syncCatalogue(courseID: "1", recordingID: "42", syncedAt: "2026-10-29")
        let material = try await NativeLearningWorkflow.fetchMaterials(owner: "alice", store: store, school: school, recordingCourseID: "42", lessonID: "7")
        XCTAssertEqual(material["lesson_id"] as? String, "7")
        let model = try ModelService(baseURL: "http://localhost:8080/v1", model: "fixture", session: modelSession)
        let workflow = try NativeLearningWorkflow(owner: "alice", store: store, analysis: NativeAnalysis(model: model))
        _ = try await workflow.analyze(lessonID: "7", modelName: "fixture", createdAt: "2026-10-29T00:00:00Z")
        let reopened = try NativeStore(url: url)
        XCTAssertNotNil(try reopened.get(owner: "alice", kind: "course", id: "1"))
        XCTAssertNotNil(try reopened.get(owner: "alice", kind: "materials", id: "7"))
        let analysis = try XCTUnwrap(try reopened.get(owner: "alice", kind: "analysis", id: "7") as? [String:Any])
        XCTAssertFalse(((analysis["sections"] as? [[String:Any]]) ?? []).isEmpty)
        XCTAssertNil(try reopened.get(owner: "bob", kind: "materials", id: "7"))
    }

    func testSharedCoreBridgeParsesRealisticCatalogueAndRejectsErrors() throws {
        let body = #"{"code":0,"result":{"data":[{"sub_id":"42","title":"第一课","content":"{}"}]}}"#
        XCTAssertEqual(try CoreBridge.catalogue(data: Data(body.utf8)).count, 1)
        XCTAssertThrowsError(try CoreBridge.catalogue(data: Data(#"{"code":5002}"#.utf8)))
        XCTAssertEqual(try CoreBridge.transcriptCount(data: Data(#"{"code":0,"list":[{"all_content":[{"Text":"示例","BeginSec":1}]}]}"#.utf8)), 1)
        XCTAssertEqual(try CoreBridge.slidesCount(rows: [["content": ["pptimgurl":"https://resource.msa.buaa.edu.cn/a.png?secret=1"]]]), 1)
    }

    func testNativeStoreUsesSharedCoreAndSeparatesAccounts() throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: false)
        defer { try? FileManager.default.removeItem(at: folder) }
        do {
            let store = try NativeStore(url: folder.appendingPathComponent("native.sqlite3"))
            try store.put(owner: "alice", kind: "course", id: "c", value: ["name":"测试"])
            XCTAssertEqual((try store.get(owner: "alice", kind: "course", id: "c") as? [String:String])?["name"], "测试")
            XCTAssertNil(try store.get(owner: "bob", kind: "course", id: "c"))
            XCTAssertEqual(try store.list(owner: "alice", kind: "course").count, 1)
            XCTAssertThrowsError(try store.list(owner: "alice", kind: "secret"))
        }
    }

    func testModelServiceRejectsUnsafeURLsAndProcessesMockResponse() async throws {
        XCTAssertThrowsError(try ModelService(baseURL: "http://remote.example/v1", model: "fixture"))
        XCTAssertThrowsError(try ModelService(baseURL: "https://user:pass@example.test/v1", model: "fixture"))
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [ModelStub.self]
        let session = URLSession(configuration: config)
        defer { session.invalidateAndCancel() }
        let model = try ModelService(baseURL: "http://localhost:8080/v1", model: "fixture", key: "test-key", session: session)
        let result = try await model.complete(system: "synthetic", prompt: "synthetic") as? [String:String]
        XCTAssertEqual(result?["answer"], "ok")
    }

    func testSharedCoreValidatesEvidenceAndBuildsTimeline() throws {
        let segments: [[String: Any]] = [
            ["id":"s0", "start":60.0, "end":120.0, "text":"下周五提交作业"],
            ["id":"s1", "start":240.0, "end":300.0, "text":"课堂内容"],
        ]
        let valid: [[String: Any]] = [["text":"作业", "evidence_ids":["s0"], "deadline":"下周五", "deadline_quote":"下周五提交作业"]]
        XCTAssertEqual(try CoreBridge.ground(points: valid, evidence: segments)[0]["deadline"] as? String, "下周五")
        XCTAssertThrowsError(try CoreBridge.ground(points: [["text":"虚构", "evidence_ids":["s9"]]], evidence: segments))
        let result = try CoreBridge.timeline(plan: ["topics":[], "covered_until_id":""], segments: segments,
                                             findings: ["knowledge_points":[], "assignments":[], "notices":[]], duration: 420)
        XCTAssertEqual(result.compactMap { $0["kind"] as? String }, ["gap","unclassified","gap","unclassified","gap"])
        XCTAssertNoThrow(try CoreBridge.validateAnswer("解释 [c0]", cited: ["c0"], available: ["c0"]))
        XCTAssertThrowsError(try CoreBridge.validateAnswer("解释 [c9]", cited: ["c0"], available: ["c0"]))
    }

    func testNativeAnalysisUsesMockModelAndGroundsItsResult() async throws {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [ExtractionStub.self]
        let session = URLSession(configuration: config)
        defer { session.invalidateAndCancel() }
        let model = try ModelService(baseURL: "http://localhost:8080/v1", model: "fixture", session: session)
        let analysis = NativeAnalysis(model: model)
        let evidence: [[String:Any]] = [["id":"s0", "start":1.0, "end":2.0, "text":"下周五提交作业"]]
        let result = try await analysis.extract(prompt: "合成材料", evidence: evidence)
        let assignments = try XCTUnwrap(result["assignments"] as? [[String:Any]])
        XCTAssertEqual(assignments.first?["deadline"] as? String, "下周五")
        XCTAssertEqual(assignments.first?["verification"] as? String, "待核验")
    }



    func testNativeWholeLessonAnalysisReturnsGroundedTimeline() async throws {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [ExtractionStub.self]
        let session = URLSession(configuration: config)
        defer { session.invalidateAndCancel() }
        let model = try ModelService(baseURL: "http://localhost:8080/v1", model: "fixture", session: session)
        let analysis = NativeAnalysis(model: model)
        let segment: [String:Any] = ["id":"s0", "start":1.0, "end":2.0, "text":"下周五提交作业"]
        let input: [String:Any] = ["lesson":["id":"lesson", "course_id":"course", "course_name":"示例", "title":"第一课", "duration":2.0],
            "materials":["segments":[segment], "digest":"fixture", "coverage":[:], "limitations":[], "duration":2.0],
            "model":"fixture", "created_at":"2026-10-28T00:00:00Z"]
        let result = try await analysis.analyze(input: input)
        XCTAssertEqual(result["timeline_status"] as? String, "partial")
        let sections = try XCTUnwrap(result["sections"] as? [[String:Any]])
        XCTAssertEqual(sections.first?["kind"] as? String, "unclassified")
        let assignments = try XCTUnwrap(result["assignments"] as? [[String:Any]])
        XCTAssertEqual(assignments.first?["deadline"] as? String, "下周五")
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: false)
        defer { try? FileManager.default.removeItem(at: folder) }
        let store = try NativeStore(url: folder.appendingPathComponent("native.sqlite3"))
        try store.put(owner: "alice", kind: "course", id: "course", value: ["id":"course"])
        try store.put(owner: "alice", kind: "lesson", id: "lesson", value: ["id":"lesson", "course_id":"course"])
        let tasks = try store.saveAnalysis(owner: "alice", analysis: result)
        XCTAssertEqual(tasks.first?["deadline"] as? String, "下周五")
        XCTAssertNil(try store.get(owner: "bob", kind: "analysis", id: "lesson"))
    }

    @MainActor
    func testSavedMaterialFlowsIntoAnalysisAndAccountScopedTasks() async throws {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [ExtractionStub.self]
        let session = URLSession(configuration: config)
        defer { session.invalidateAndCancel() }
        let model = try ModelService(baseURL: "http://localhost:8080/v1", model: "fixture", session: session)
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: false)
        defer { try? FileManager.default.removeItem(at: folder) }
        let store = try NativeStore(url: folder.appendingPathComponent("native.sqlite3"))
        try store.put(owner:"alice", kind:"course", id:"course", value:["id":"course", "name":"示例"])
        try store.put(owner:"alice", kind:"lesson", id:"lesson", value:["id":"lesson", "course_id":"course", "title":"第一课", "duration":2])
        try store.put(owner:"alice", kind:"materials", id:"lesson", value:["segments":[["id":"s0", "start":1, "end":2, "text":"下周五提交作业"]], "digest":"fixture", "coverage":[:], "limitations":[], "duration":2])
        let workflow = try NativeLearningWorkflow(owner:"alice", store:store, analysis:NativeAnalysis(model:model))
        _ = try await workflow.analyze(lessonID:"lesson", modelName:"fixture", createdAt:"2026-10-28T00:00:00Z")
        XCTAssertNotNil(try store.get(owner:"alice", kind:"analysis", id:"lesson"))
        XCTAssertEqual(try store.list(owner:"alice", kind:"task").count, 1)
        XCTAssertNil(try store.get(owner:"bob", kind:"analysis", id:"lesson"))
        let reopened = try NativeStore(url: folder.appendingPathComponent("native.sqlite3"))
        let saved = try XCTUnwrap(try reopened.get(owner:"alice", kind:"analysis", id:"lesson") as? [String:Any])
        XCTAssertFalse(((saved["sections"] as? [[String:Any]]) ?? []).isEmpty)
        XCTAssertNil(try reopened.get(owner:"bob", kind:"analysis", id:"lesson"))
    }




}

private final class MemoryModelCredentials: ModelCredentialStore {
    var values: [String:String] = [:]
    var failReads = false
    func load(account: String, endpoint: String) throws -> String? {
        if failReads { throw ProbeError.message("钥匙串不可用。") }
        return values["\(account)|\(endpoint)"]
    }
    func save(_ key: String, account: String, endpoint: String) throws { values["\(account)|\(endpoint)"] = key }
    func delete(account: String, endpoint: String) throws { values.removeValue(forKey: "\(account)|\(endpoint)") }
}

@MainActor private final class FakeAutomationTransport: AutomationSchedulerTransport {
    var lessons: [AutomationLesson] = []
    var failAnalysis = false
    var analysisCount = 0
    var onCatalogue: (() throws -> Void)?
    var onCatalogueAsync: (() async -> Void)?
    var catalogueCount = 0
    var onMaterials: (() throws -> Void)?
    var failSchedule = false
    var scheduleCount = 0
    func synchronizeSchedule(now: Date) async throws { scheduleCount += 1; if failSchedule { throw ProbeError.message("课表失败") } }
    func catalogue(courseID: String) async throws -> [AutomationLesson] { catalogueCount += 1; try onCatalogue?(); await onCatalogueAsync?(); return lessons }
    func materials(lesson: AutomationLesson) async throws -> (segments: Int, slides: Int) { try onMaterials?(); return (1, 0) }
    func analyze(lessonID: String) async throws { analysisCount += 1; if failAnalysis { throw ProbeError.message("模型失败") } }
}

private final class PortalStub: URLProtocol {
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        guard let url = request.url, url.host == "spoc.buaa.edu.cn",
              request.value(forHTTPHeaderField: "Token") == "Inco-example",
              request.value(forHTTPHeaderField: "RoleCode") == "01" else {
            client?.urlProtocol(self, didFailWithError: URLError(.badServerResponse)); return
        }
        let content: Any
        if url.path.hasSuffix("/component/queryXnxq"), request.httpMethod == "POST" {
            content = [["xnxq": "2026-20271"]]
        } else if url.path.hasSuffix("/jxkj/queryKclb"), url.query?.contains("xnxq=2026-20271") == true {
            content = [["kcid": "1", "kcmc": "示例课程", "juese": "3"],
                       ["kcid": "2", "kcmc": "其他角色", "juese": "1"]]
        } else if url.path.hasSuffix("/jxkj/queryRlData") {
            content = [["kcid":"1", "kcmc":"示例课程", "rq":"2026-10-21", "strkcsj":"08:00-09:35"]]
        } else { client?.urlProtocol(self, didFailWithError: URLError(.badURL)); return }
        let data = try! JSONSerialization.data(withJSONObject: ["code": 200, "content": content])
        client?.urlProtocol(self, didReceive: HTTPURLResponse(url: url, statusCode: 200, httpVersion: nil, headerFields: nil)!, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: data)
        client?.urlProtocolDidFinishLoading(self)
    }
    override func stopLoading() {}
}

private final class SlowSyncStub: URLProtocol {
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        guard let url = request.url else { client?.urlProtocol(self, didFailWithError:URLError(.badURL)); return }
        Thread.sleep(forTimeInterval:0.2)
        let body = #"{"code":200,"content":[{"kcid":"1","kcmc":"示例课程","juese":"3"}]}"#
        client?.urlProtocol(self, didReceive:HTTPURLResponse(url:url,statusCode:200,httpVersion:nil,headerFields:nil)!,cacheStoragePolicy:.notAllowed)
        client?.urlProtocol(self, didLoad:Data(body.utf8)); client?.urlProtocolDidFinishLoading(self)
    }
    override func stopLoading() {}
}

private final class SyncStub: URLProtocol {
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        guard let url = request.url else { client?.urlProtocol(self, didFailWithError:URLError(.badURL)); return }
        let body: String
        if url.host == "spoc.buaa.edu.cn" && url.path.hasSuffix("/jxkj/queryKclb") {
            body = #"{"code":200,"content":[{"kcid":"1","kcmc":"示例","juese":"3","rwh":"task","skjs":"教师"}]}"#
        } else if url.host == "spoc.buaa.edu.cn" && url.path.hasSuffix("/kc/queryYhSflx") {
            body = #"{"code":200,"content":"2"}"#
        } else if url.host == "spoc.buaa.edu.cn" && url.path.hasSuffix("/yun/getHxyTimestampAndSignature2") {
            body = #"{"code":200,"content":{"ktsl_url":"https://classroom.msa.buaa.edu.cn/entry","bind_id":"example"}}"#
        } else if url.host == "yjapi.msa.buaa.edu.cn" && url.path.hasSuffix("/courseapi/v2/course/catalogue") {
            body = #"{"code":0,"result":{"data":[{"sub_id":"7","title":"第一课","start_at":1760000000,"content":"{\"save_playback\":{\"contents\":[1],\"contents_duration\":3600}}"}]}}"#
        } else if url.path.hasSuffix("/courseapi/v3/portal-home-setting/get-sub-info") {
            body = #"{"code":0,"data":{"resource_guid":"fixture","duration":2}}"#
        } else if url.path.hasSuffix("/courseapi/v3/web-socket/search-trans-result") {
            body = #"{"code":0,"list":[{"all_content":[{"Text":"下周五提交作业","BeginSec":1,"EndSec":2}]}]}"#
        } else if url.path.hasSuffix("/pptnote/v1/schedule/search-ppt") {
            body = #"{"code":0,"total":0,"list":[]}"#
        } else { client?.urlProtocol(self, didFailWithError:URLError(.badURL)); return }
        client?.urlProtocol(self, didReceive:HTTPURLResponse(url:url, statusCode:200, httpVersion:nil, headerFields:nil)!, cacheStoragePolicy:.notAllowed)
        client?.urlProtocol(self, didLoad:Data(body.utf8)); client?.urlProtocolDidFinishLoading(self)
    }
    override func stopLoading() {}
}

private final class ModelStub: URLProtocol {
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        guard let url = request.url, url.host == "localhost", url.path == "/v1/chat/completions",
              request.value(forHTTPHeaderField: "Authorization") == "Bearer test-key",
              request.httpMethod == "POST" else {
            client?.urlProtocol(self, didFailWithError: URLError(.badServerResponse)); return
        }
        let body = #"{"choices":[{"finish_reason":"stop","message":{"content":"{\"answer\":\"ok\"}"}}]}"#
        client?.urlProtocol(self, didReceive: HTTPURLResponse(url: url, statusCode: 200, httpVersion: nil, headerFields: nil)!, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: Data(body.utf8))
        client?.urlProtocolDidFinishLoading(self)
    }
    override func stopLoading() {}
}

private class ExtractionStub: URLProtocol {
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        guard let url = request.url, url.host == "localhost", url.path == "/v1/chat/completions" else {
            client?.urlProtocol(self, didFailWithError: URLError(.badURL)); return
        }
        let extraction: [String:Any] = ["summary":"测试", "topics":[], "covered_until_id":"s0",
            "knowledge_points":[], "assignments":[["text":"作业", "evidence_ids":["s0"],
                "deadline":"下周五", "deadline_quote":"下周五提交作业"]], "notices":[], "review_suggestions":[]]
        let content = String(data: try! JSONSerialization.data(withJSONObject: extraction), encoding: .utf8)!
        let body = try! JSONSerialization.data(withJSONObject: ["choices":[["finish_reason":"stop", "message":["content":content]]]])
        client?.urlProtocol(self, didReceive: HTTPURLResponse(url: url, statusCode: 200, httpVersion: nil, headerFields: nil)!, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: body)
        client?.urlProtocolDidFinishLoading(self)
    }
    override func stopLoading() {}
}

private final class SlowExtractionStub: ExtractionStub {
    override func startLoading() { Thread.sleep(forTimeInterval:0.2); super.startLoading() }
}
