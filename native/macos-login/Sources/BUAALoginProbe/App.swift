import AppKit
import CoreText
import SwiftUI
import WebKit

struct CourseSyncJob: Equatable {
    enum Phase: String { case running, completed, failed }
    let id: String
    let createdAt: String
    var phase: Phase
    var message: String
    var finishedAt: String?
    var kind: String = "sync"
}
struct AnalysisJob: Equatable {
    let id: String
    let lessonID: String
    let courseID: String
    let title: String
    let createdAt: String
    var phase: CourseSyncJob.Phase
    var message: String
    var finishedAt: String?
}
enum RecordingSyncStatus: Equatable {
    case syncing
    case succeeded(Int)
    case failed(String)
}

@MainActor
final class Probe: NSObject, ObservableObject, WKNavigationDelegate, WKUIDelegate {
    @Published var status = "请连接北航教学空间。"
    @Published private(set) var sessionIssue = ""
    @Published private(set) var recoveredJobs = 0
    @Published private(set) var operationIssue = ""
    func clearOperationIssue() { operationIssue = "" }
    func dismissRecoveredJobs() { recoveredJobs = 0 }
    private func reportIssue(_ message: String, event: String = "operation-failed") {
        operationIssue = message
        status = message
        if let directory = probeStore?.directory { DesktopDiagnostics.record(event, in: directory) }
    }
    @Published var semesters: [String] = []
    @Published var selectedTerm = ""
    @Published var courses: [Course] = []
    @Published var signedIn = false
    @Published var recordingReady = false
    @Published var lessonCount: Int?
    @Published var lessons: [ProbeLesson] = []
    @Published var working = false
    @Published private(set) var modelName = ""
    @Published private(set) var modelBaseURL = ""
    @Published private(set) var modelHasKey = false
    @Published private(set) var modelIssue = ""
    @Published private(set) var modelRevision = 0
    @Published private(set) var workspaceRevision = 0
    @Published private(set) var dataVersion = 0
    @Published private(set) var courseSyncJob: CourseSyncJob?
    @Published private(set) var analysisJob: AnalysisJob?
    @Published private(set) var activeAutomationJobID: String?
    @Published private(set) var activeMaterialJobID: String?
    @Published private(set) var recordingSyncStates: [String:RecordingSyncStatus] = [:]
    @Published private(set) var recordingSyncCourse: String?
    @Published private(set) var materialFeedback: [String:String] = [:]
    private var dismissSyncSuccess: Task<Void, Never>?
    private(set) var modelService: ModelService?
    private var modelKey = ""
    private let credentials: any ModelCredentialStore
    private let schoolCredentials: (any ModelCredentialStore)?
    private let makeSchool: (String, String, String) throws -> NativeSchoolSession
    private var savedSchoolCredentials = ""
    private let schoolCredentialEndpoint = "https://spoc.buaa.edu.cn/session"
    private let makeModel: (String, String, String) throws -> ModelService
    private let recordingAuthorization: ((Course, NativeSchoolSession) async throws -> (id: String, token: String))?
    let browser: WKWebView
    private var school: NativeSchoolSession?
    private var sync: NativeCourseSync?
    private var probeStore: NativeStore?
    private var owner = ""
    private var loginPoll: Task<Void, Never>?
    private var recordPoll: Task<Void, Never>?
    private var recordingCourseID: String?
    private var recordingAccess: String?
    private var navigationIssue = ""
    private var blockedNavigation = ""
    private var automationLoop: Task<Void, Never>?
    private var automationLease: AutomationLease?
    private var automationScheduler: NativeAutomationScheduler?
    private var automationOwner = ""
    private var lastSessionCheck = Date.distantPast
    private var lastSessionRestore = Date.distantPast
    private var canResumeSchoolSession = true
    private var workspaceLease: AutomationLease?
    private var workspaceLeaseDirectory: URL?

    override convenience init() { self.init(credentials: KeychainModelCredentialStore(), schoolCredentials:KeychainModelCredentialStore(service:"io.github.fyzzzz05.buaa-learn.school-session")) }
    init(credentials: any ModelCredentialStore, schoolCredentials: (any ModelCredentialStore)? = nil, makeSchool: @escaping (String,String,String) throws -> NativeSchoolSession = { try NativeSchoolSession(token:$0,refresh:$1,role:$2) }, makeModel: @escaping (String, String, String) throws -> ModelService = { try ModelService(baseURL: $0, model: $1, key: $2) }, recordingAuthorization: ((Course, NativeSchoolSession) async throws -> (id: String, token: String))? = nil) {
        self.credentials = credentials
        self.schoolCredentials = schoolCredentials
        self.makeSchool = makeSchool
        self.makeModel = makeModel
        self.recordingAuthorization = recordingAuthorization
        let config = WKWebViewConfiguration()
        config.websiteDataStore = .nonPersistent()
        browser = WKWebView(frame: .zero, configuration: config)
        super.init()
        browser.navigationDelegate = self
        browser.uiDelegate = self
    }

    func startLogin() {
        guard !working else { return }
        operationIssue = ""
        loginPoll?.cancel(); recordPoll?.cancel()
        dismissSyncSuccess?.cancel(); courseSyncJob = nil; recordingSyncStates = [:]; recordingSyncCourse = nil; materialFeedback = [:]; working = false
        school = nil; sync = nil; signedIn = false; recordingReady = false; semesters = []; lessonCount = nil; lessons = []; recordingAccess = nil
        status = "请在学校页面完成登录。"
        browser.load(URLRequest(url: URL(string: "https://spoc.buaa.edu.cn/spocnewht/cas")!))
        loginPoll = Task { [weak self] in
            for _ in 0..<600 {
                if Task.isCancelled { return }
                guard let self else { return }
                if await self.captureLogin() { return }
                try? await Task.sleep(nanoseconds: 1_000_000_000)
            }
            self?.reportIssue("等待登录超时，请重试。")
        }
    }

    func records(kind: String) throws -> [[String:Any]] {
        guard let probeStore, !owner.isEmpty else { return [] }
        return try probeStore.list(owner:owner, kind:kind).compactMap { $0 as? [String:Any] }
    }
    func cachedSchedule() throws -> [[String:Any]]? {
        guard let probeStore, !owner.isEmpty,
              let schedule = try probeStore.get(owner:owner, kind:"meta", id:"automation_schedule") as? [String:Any] else { return nil }
        return schedule["rows"] as? [[String:Any]] ?? []
    }
    func cachedSemesterSchedule() throws -> [[String:Any]]? {
        guard let probeStore, !owner.isEmpty,
              let schedule = try probeStore.get(owner:owner, kind:"meta", id:"semester_schedule") as? [String:Any],
              schedule["semester"] as? String == selectedTerm else { return nil }
        return schedule["rows"] as? [[String:Any]] ?? []
    }
    func slideLoader() -> @MainActor (URL) async throws -> Data {
        let account = owner, store = probeStore
        return { url in
            guard let store, !account.isEmpty else { throw ProbeError.message("请先打开本地学习空间。") }
            return try await SlideAssetCache.load(url:url,owner:account,store:store)
        }
    }
    func setTaskDone(id: String, done: Bool) throws {
        guard let probeStore, !owner.isEmpty,
              var task = try probeStore.get(owner:owner, kind:"task", id:id) as? [String:Any] else {
            throw ProbeError.message("未找到这项作业。")
        }
        task["done"] = done
        try probeStore.put(owner:owner, kind:"task", id:id, value:task)
        dataVersion += 1
    }
    func configureModel(baseURL: String, model: String, key: String) throws {
        guard let store = probeStore, !owner.isEmpty else { throw ProbeError.message("请先打开本地学习空间。") }
        let endpoint = baseURL.trimmingCharacters(in: .whitespacesAndNewlines)
        let name = model.trimmingCharacters(in: .whitespacesAndNewlines)
        let sameEndpoint = endpoint == modelBaseURL
        if key.isEmpty && sameEndpoint && modelHasKey && modelKey.isEmpty {
            throw ProbeError.message("原有模型密钥无法读取，请重新填写。")
        }
        let effectiveKey = key.isEmpty && sameEndpoint ? modelKey : key
        let service = try makeModel(endpoint, name, effectiveKey)
        let previousKey = !key.isEmpty ? try credentials.load(account: owner, endpoint: endpoint) : nil
        if !key.isEmpty { try credentials.save(key, account: owner, endpoint: endpoint) }
        do {
            try store.put(owner: owner, kind: "meta", id: "model", value: ["base_url":endpoint, "model":name, "has_key":!effectiveKey.isEmpty])
        } catch {
            if !key.isEmpty {
                do {
                    if let previousKey { try credentials.save(previousKey, account: owner, endpoint: endpoint) }
                    else { try credentials.delete(account: owner, endpoint: endpoint) }
                } catch {
                    modelService = nil; modelKey = ""; modelIssue = "配置保存失败，密钥恢复也失败，请重新填写模型配置。"; modelRevision += 1
                    throw ProbeError.message(modelIssue)
                }
            }
            throw error
        }
        modelService = service; modelKey = effectiveKey; modelName = name; modelBaseURL = endpoint
        modelHasKey = !effectiveKey.isEmpty; modelIssue = ""; modelRevision += 1
    }
    func removeModelKey() throws {
        guard let store = probeStore, !owner.isEmpty, modelHasKey else { throw ProbeError.message("没有可移除的模型密钥。") }
        let service = try makeModel(modelBaseURL, modelName, "")
        try credentials.delete(account: owner, endpoint: modelBaseURL)
        do {
            try store.put(owner: owner, kind: "meta", id: "model", value: ["base_url":modelBaseURL, "model":modelName, "has_key":false])
        } catch {
            modelService = nil; modelKey = ""; modelIssue = "密钥已移除，但模型配置未能更新，请重新保存。"; modelRevision += 1
            throw ProbeError.message(modelIssue)
        }
        modelService = service; modelKey = ""; modelHasKey = false; modelIssue = ""; modelRevision += 1
    }
    private func restoreModel(store: NativeStore, account: String) {
        modelService = nil; modelKey = ""; modelName = ""; modelBaseURL = ""; modelHasKey = false; modelIssue = ""
        defer { modelRevision += 1 }
        do {
            guard let config = try store.get(owner: account, kind: "meta", id: "model") as? [String:Any] else { return }
            guard let baseURL = config["base_url"] as? String, let name = config["model"] as? String,
                  let hasKey = config["has_key"] as? Bool else { throw ProbeError.message("模型配置格式异常，请重新保存。") }
            modelBaseURL = baseURL; modelName = name; modelHasKey = hasKey
            let key: String
            if hasKey {
                guard let stored = try credentials.load(account: account, endpoint: baseURL), !stored.isEmpty else {
                    throw ProbeError.message("未找到模型密钥，请重新填写。")
                }
                key = stored
            } else { key = "" }
            modelService = try makeModel(baseURL, name, key)
            modelKey = key
        } catch { modelIssue = (error as? LocalizedError)?.errorDescription ?? "无法恢复模型配置，请重新保存。" }
    }
    func analyzeLesson(_ lessonID: String) async throws {
        guard let store = probeStore, let modelService, !owner.isEmpty else {
            throw ProbeError.message("请先打开本地学习空间并配置学习模型。")
        }
        guard !working else { throw ProbeError.message("请等待当前操作完成。") }
        guard let lesson = try store.get(owner: owner, kind: "lesson", id: lessonID) as? [String:Any],
              let courseID = lesson["course_id"] as? String, let title = lesson["title"] as? String else {
            throw ProbeError.message("未找到这节课，请先同步课程。")
        }
        let account = owner
        let jobID = UUID().uuidString
        let createdAt = ISO8601DateFormatter().string(from: Date())
        let initial = AnalysisJob(id: jobID, lessonID: lessonID, courseID: courseID, title: title, createdAt: createdAt,
                                  phase: .running, message: "正在准备课堂分析…", finishedAt: nil)
        try saveAnalysisJob(initial, store: store, account: account)
        analysisJob = initial
        dataVersion += 1
        working = true
        defer { working = false }
        let analysis = NativeAnalysis(model: modelService) { [weak self] message in
            Task { @MainActor in self?.updateAnalysisProgress(jobID: jobID, account: account, message: message) }
        }
        let workflow = try NativeLearningWorkflow(owner: account, store: store, analysis: analysis)
        do {
            _ = try await workflow.analyze(lessonID: lessonID, modelName: modelName, createdAt: createdAt)
        } catch {
            var failed = initial
            failed.phase = .failed
            failed.message = (error as? LocalizedError)?.errorDescription ?? "课堂分析失败，请重试。"
            failed.finishedAt = ISO8601DateFormatter().string(from: Date())
            analysisJob = failed
            do { try saveAnalysisJob(failed, store: store, account: account) }
            catch { status = "任务状态保存失败。" }
            dataVersion += 1
            throw error
        }
        var completed = initial
        completed.phase = .completed; completed.message = "学习笔记已生成。"
        completed.finishedAt = ISO8601DateFormatter().string(from: Date())
        analysisJob = completed
        do { try saveAnalysisJob(completed, store: store, account: account) }
        catch { dataVersion += 1; throw ProbeError.message("笔记已生成，但任务状态保存失败。请打开课堂查看。") }
        dataVersion += 1
    }
    private func saveAnalysisJob(_ job: AnalysisJob, store: NativeStore, account: String) throws {
        var row: [String:Any] = ["id":job.id, "kind":"analyze", "target":job.lessonID, "course_id":job.courseID,
                                 "title":job.title, "status":job.phase.rawValue, "message":job.message, "created_at":job.createdAt]
        if let finished = job.finishedAt { row["finished_at"] = finished }
        try store.put(owner: account, kind: "job", id: job.id, value: row)
    }
    private func updateAnalysisProgress(jobID: String, account: String, message: String) {
        guard owner == account, let store = probeStore, var job = analysisJob, job.id == jobID, job.phase == .running else { return }
        job.message = message
        analysisJob = job
        do { try saveAnalysisJob(job, store: store, account: account); dataVersion += 1 }
        catch { reportIssue("任务进度保存失败。") }
    }
    private func attachLocalStore(_ store: NativeStore, account: String) throws {
        guard !account.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { throw ProbeError.message("学校账号无效。") }
        if let directory = store.directory, workspaceLeaseDirectory != directory {
            guard let lease = AutomationLease(directory:directory, filename:"workspace.lock") else {
                throw ProbeError.message("这个学习空间已在另一个 BUAA Learn 窗口中打开，请先关闭另一个窗口。")
            }
            workspaceLease = lease; workspaceLeaseDirectory = directory
        }
        if probeStore !== store || owner != account {
            recoveredJobs = try InterruptedJobs.recover(store:store, owner:account)
        }
        let term = (try store.get(owner: account, kind: "meta", id: "semester") as? [String:Any])?["id"] as? String ?? ""
        let cached = try store.list(owner: account, kind: "course").compactMap { $0 as? [String:Any] }.compactMap { row -> Course? in
            guard let id = row["id"] as? String, let name = row["name"] as? String else { return nil }
            return Course(id:id, name:name, taskID:row["task_id"] as? String ?? "", semester:row["semester"] as? String ?? "", code:row["code"] as? String ?? "", teachers:row["teachers"] as? String ?? "")
        }
        if owner != account { workspaceRevision += 1; analysisJob = nil }
        owner = account; probeStore = store; selectedTerm = term
        courses = term.isEmpty ? cached : cached.filter { $0.semester == term }
        restoreModel(store: store, account: account)
        dataVersion += 1
    }
    func restoreWorkspace(baseDirectory: URL? = nil) throws {
        guard probeStore == nil, let account = try NativeStore.rememberedAccount(baseDirectory: baseDirectory) else { return }
        let store = try NativeStore.openApplicationStore(baseDirectory: baseDirectory)
        try attachLocalStore(store, account: account)
        status = "已打开本地学习记录，连接学校后可同步。"
    }
    func attachSchool(session: NativeSchoolSession, store: NativeStore, account: String) throws {
        let synchronizer = try NativeCourseSync(owner:account, store:store, school:session)
        try attachLocalStore(store, account: account)
        school = session; sync = synchronizer
        canResumeSchoolSession = true
        lastSessionCheck = Date()
        savedSchoolCredentials = ""
        sessionIssue = ""
        session.onAuthorizationExpired = { [weak self, weak session] issue in
            guard let self, let session, self.school === session else { return }
            self.signedIn = false; self.recordingReady = false; self.recordingAccess = nil
            self.sessionIssue = issue.localizedDescription
            self.canResumeSchoolSession = false
            self.status = issue.localizedDescription
            do { try self.schoolCredentials?.delete(account:account,endpoint:self.schoolCredentialEndpoint); self.savedSchoolCredentials = "" }
            catch { self.reportIssue("学校授权已失效，但未能清理钥匙串中的旧会话。请重新登录。") }
        }
        session.onCredentialsChanged = { [weak self, weak session] value in
            guard let self, let session, self.school === session, self.savedSchoolCredentials != value else { return }
            do {
                try self.schoolCredentials?.save(value,account:account,endpoint:self.schoolCredentialEndpoint)
                self.savedSchoolCredentials = value
            } catch { self.reportIssue("本次学校连接可继续使用，但会话未能保存到钥匙串；重启后需重新登录。") }
        }
    }

    func resumeSchoolSession() async {
        guard school == nil, !working, let schoolCredentials, let store = probeStore, !owner.isEmpty else { return }
        lastSessionRestore = Date()
        working = true; defer { working = false }
        do {
            guard let saved = try schoolCredentials.load(account:owner,endpoint:schoolCredentialEndpoint) else { canResumeSchoolSession = false; return }
            guard let data = saved.data(using:.utf8), let value = try JSONSerialization.jsonObject(with:data) as? [String:Any],
                  let token = value["token"] as? String, let role = value["role"] as? String else { throw ProbeError.message("保存的学校会话无效，请重新登录。") }
            let session = try makeSchool(token,value["refresh"] as? String ?? "",role)
            try attachSchool(session:session,store:store,account:owner)
            if let recording = value["recording"] as? String, !recording.isEmpty { try await session.setRecording(token:recording); recordingReady = true }
            let terms = try await session.semesters()
            guard school === session else { return }
            semesters = terms; signedIn = true
            if !terms.contains(selectedTerm), let first = terms.first { selectedTerm = first }
            status = "已恢复学校连接。"
            if operationIssue.hasPrefix("暂未恢复学校连接") { operationIssue = "" }
        } catch {
            signedIn = false
            if error is SchoolConnectionUnavailable { school = nil; sync = nil; recordingReady = false }
            else { canResumeSchoolSession = false }
            if sessionIssue.isEmpty { reportIssue("暂未恢复学校连接，可稍后重新登录；本地材料仍可查看。") }
        }
    }

    private func captureLogin() async -> Bool {
        guard browser.url?.scheme == "https", browser.url?.host == "spoc.buaa.edu.cn" else { return false }
        let script = """
        (() => { try {
          const token = localStorage.getItem('token');
          const user = JSON.parse(localStorage.getItem('userinfo') || 'null');
          if (!token || !user?.jsdm || !user?.yhdm) return null;
          return {token: token, refresh: localStorage.getItem('refreshToken') || '', role: String(user.jsdm), owner: String(user.yhdm)};
        } catch (_) { return null; } })()
        """
        guard let result = try? await browser.evaluateJavaScript(script) as? [String: String],
              let token = result["token"], let role = result["role"], let owner = result["owner"], !owner.isEmpty else { return false }
        guard role == "01" else { status = "请在教学空间切换为学生角色后重试。"; return true }
        do {
            let session = try makeSchool(token,result["refresh"] ?? "",role)
            let store = try NativeStore.openApplicationStore()
            try attachSchool(session:session, store:store, account:owner)
            try NativeStore.rememberAccount(owner)
        }
        catch { reportIssue((error as? LocalizedError)?.errorDescription ?? "无法创建学校会话或打开本地数据库，请检查本地存储后重试。"); return true }
        signedIn = true; status = "学校登录成功，正在读取学期。"
        do {
            guard let session = school else { return true }
            let result = try await session.semesters()
            guard school === session else { return true }
            semesters = result
            guard let first = semesters.first else { reportIssue("学校未返回学期数据。"); return true }
            selectedTerm = semesters.contains(selectedTerm) ? selectedTerm : first
            await loadCourses()
        } catch { reportIssue((error as? LocalizedError)?.errorDescription ?? "学校登录成功，但读取学期失败。请检查学校网络后重试。") }
        return true
    }

    func loadCourses() async {
        guard let school, let sync, let store = probeStore, !owner.isEmpty, !selectedTerm.isEmpty, !working else { return }
        let term = selectedTerm
        let account = owner
        let now = ISO8601DateFormatter().string(from: Date())
        dismissSyncSuccess?.cancel()
        let jobID = UUID().uuidString
        courseSyncJob = CourseSyncJob(id:jobID, createdAt:now, phase:.running, message:"正在同步本学期课程…", finishedAt:nil)
        saveSyncJob(store:store, owner:account)
        status = "正在同步本学期课程…"
        working = true; defer { working = false }
        do {
            let result = try await sync.syncSemester(term, syncedAt: now)
            guard self.school === school, selectedTerm == term else { return }
            courses = result
            status = courses.isEmpty ? "该学期没有读取到学生课程，请检查所选学期。" : "已同步 \(courses.count) 门课程。"
            courseSyncJob = CourseSyncJob(id:jobID, createdAt:now, phase:.completed, message:status, finishedAt:ISO8601DateFormatter().string(from:Date()))
            saveSyncJob(store:store, owner:account)
            dismissSyncSuccess = Task { [weak self] in
                try? await Task.sleep(nanoseconds:5_000_000_000)
                guard !Task.isCancelled, let self, self.courseSyncJob?.id == jobID else { return }
                self.courseSyncJob = nil
            }
        } catch {
            guard self.school === school, selectedTerm == term else { return }
            reportIssue((error as? LocalizedError)?.errorDescription ?? "课程读取失败，请检查学校网络后重试。", event: "course-sync-failed")
            courseSyncJob = CourseSyncJob(id:jobID, createdAt:now, phase:.failed, message:status, finishedAt:ISO8601DateFormatter().string(from:Date()))
            saveSyncJob(store:store, owner:account)
        }
    }
    func retryCourseSync() async {
        if courseSyncJob?.kind == "recordings" { await loadLatestRecordings() }
        else { await loadCourses() }
    }

    func loadLatestRecordings() async {
        guard let school, let store = probeStore, signedIn, !owner.isEmpty, !working else { return }
        let account = owner
        let targets = courses.filter { $0.semester == selectedTerm }
        guard !targets.isEmpty else { await loadCourses(); return }
        let created = ISO8601DateFormatter().string(from:Date())
        let jobID = UUID().uuidString
        dismissSyncSuccess?.cancel()
        courseSyncJob = CourseSyncJob(id:jobID, createdAt:created, phase:.running,
            message:"正在同步最新回放…", finishedAt:nil, kind:"recordings")
        saveSyncJob(store:store, owner:account)
        working = true
        defer { working = false; dataVersion += 1 }
        var failures: [String] = []
        var added = 0
        var succeeded = 0
        var completed = 0
        await RecordingBatch.run(items:targets) { [self] course in
            guard self.school === school, owner == account, signedIn else { return }
            recordingSyncStates[course.id] = .syncing
            do {
                let cached = try store.list(owner:account, kind:"lesson").compactMap { $0 as? [String:Any] }
                let known = Set(cached.filter { $0["course_id"] as? String == course.id && $0["has_recording"] as? Bool == true }.compactMap { $0["id"] as? String })
                let lessons = try await parallelCatalogue(course:course, account:account, school:school, store:store)
                guard self.school === school, owner == account else { return }
                succeeded += 1
                added += lessons.filter { $0.hasRecording && !known.contains($0.id) }.count
                recordingSyncStates[course.id] = .succeeded(lessons.filter(\.hasRecording).count)
            } catch {
                guard self.school === school, owner == account else { return }
                failures.append(course.name)
                recordingSyncStates[course.id] = .failed(error.localizedDescription)
            }
            completed += 1
            courseSyncJob?.message = "已检查 \(completed)/\(targets.count) 门课程，新增 \(added) 节回放"
            saveSyncJob(store:store, owner:account)
            dataVersion += 1
        }
        guard self.school === school, owner == account else { return }
        status = "已同步 \(succeeded) 门课程，新增 \(added) 节回放。"
        if !failures.isEmpty { status += "同步失败：" + failures.joined(separator:"、") }
        courseSyncJob = CourseSyncJob(id:jobID, createdAt:created, phase:failures.isEmpty ? .completed : .failed,
            message:status, finishedAt:ISO8601DateFormatter().string(from:Date()), kind:"recordings")
        saveSyncJob(store:store, owner:account)
        if failures.isEmpty {
            dismissSyncSuccess = Task { [weak self] in
                try? await Task.sleep(nanoseconds:5_000_000_000)
                guard !Task.isCancelled, let self, self.courseSyncJob?.id == jobID else { return }
                self.courseSyncJob = nil
            }
        }
    }

    private func saveSyncJob(store: NativeStore, owner: String) {
        guard let job = courseSyncJob else { return }
        var row: [String:Any] = ["id":job.id, "kind":job.kind, "status":job.phase.rawValue, "message":job.message, "created_at":job.createdAt]
        if let finished = job.finishedAt { row["finished_at"] = finished }
        do { try store.put(owner:owner, kind:"job", id:job.id, value:row); dataVersion += 1 }
        catch { reportIssue("任务状态保存失败。") }
    }

    func checkSchedule() { syncSchedule(wholeSemester:false) }
    func checkSemesterSchedule() { syncSchedule(wholeSemester:true) }
    private func syncSchedule(wholeSemester: Bool) {
        guard let school, let store = probeStore, !owner.isEmpty, !working else { return }
        let lease = automationLease ?? store.directory.flatMap { AutomationLease(directory:$0) }
        guard store.directory == nil || lease != nil else { reportIssue("另一个应用窗口正在运行自动化。"); return }
        let account = owner, term = selectedTerm
        Task {
            working = true; defer { working = false; withExtendedLifetime(lease) {} }
            let id = UUID().uuidString
            let created = ISO8601DateFormatter().string(from:Date())
            @MainActor func saveJob(_ state: String, _ message: String, finished: Bool = false) throws {
                var value: [String:Any] = ["id":id, "kind":wholeSemester ? "semester-schedule" : "schedule", "target":"", "status":state, "message":message, "created_at":created]
                if finished { value["finished_at"] = ISO8601DateFormatter().string(from:Date()) }
                try store.put(owner:account, kind:"job", id:id, value:value)
                activeAutomationJobID = finished ? nil : id
                dataVersion += 1
            }
            do {
                status = "正在读取课表。"
                try saveJob("running", status)
                let rows: [[String:Any]]
                if wholeSemester {
                    guard let semester = TimetableSemester(id:term) else { throw ProbeError.message("尚未配置该学期校历，无法确认教学周。") }
                    rows = try await NativeSemesterSchedule.sync(semester:semester, school:school, store:store, owner:account) { completed, total in
                        try saveJob("running", "正在同步本学期课表 \(completed)/\(total)")
                    }
                } else {
                    let rules = try NativeAutomationRules(owner:account, store:store)
                    rows = try await rules.syncSchedule(school:school)
                }
                guard self.school === school, self.owner == account else { return }
                status = "课表读取成功，共 \(rows.count) 条上课记录。"
                try saveJob("completed", status, finished:true)
            } catch {
                reportIssue((error as? LocalizedError)?.errorDescription ?? "课表读取失败。")
                do { try saveJob("failed", status, finished:true) } catch { activeAutomationJobID = nil; status = "课表任务状态保存失败。" }
            }
        }
    }

    func startAutomationLoop() {
        guard automationLoop == nil else { return }
        automationLoop = Task { [weak self] in
            while !Task.isCancelled {
                await self?.checkSchoolConnection()
                await self?.automationTick()
                do { try await Task.sleep(nanoseconds: 10_000_000_000) } catch { return }
            }
        }
    }
    private func checkSchoolConnection() async {
        if !signedIn, canResumeSchoolSession, sessionIssue.isEmpty, Date().timeIntervalSince(lastSessionRestore) >= 60 {
            await resumeSchoolSession()
            return
        }
        guard signedIn, !working, let school, Date().timeIntervalSince(lastSessionCheck) >= 600 else { return }
        lastSessionCheck = Date()
        working = true; defer { working = false }
        do {
            _ = try await school.semesters()
            if operationIssue.hasPrefix("学校连接检查失败") { operationIssue = "" }
        }
        catch {
            guard self.school === school else { return }
            if !(error is SchoolAuthorizationIssue) { reportIssue("学校连接检查失败，请检查网络；本地学习材料仍可查看。") }
        }
    }

    func saveAutomation(changes: [String:Any], workspace: Int) -> [String:Any] {
        guard workspace == workspaceRevision, let store = probeStore, !owner.isEmpty else {
            return ["saved":[], "errors":changes.keys.sorted().map { ["id":$0, "message":"学习空间已改变，请重新选择。"] }]
        }
        let account = owner
        var saved: [[String:Any]] = [], errors: [[String:String]] = []
        do {
            let lease = automationLease ?? store.directory.flatMap { AutomationLease(directory:$0) }
            guard store.directory == nil || lease != nil else { throw ProbeError.message("另一个应用窗口正在运行自动化，请在该窗口修改。") }
            defer { withExtendedLifetime(lease) {} }
            let rules = try NativeAutomationRules(owner:account, store:store)
            let now = ISO8601DateFormatter().string(from:Date())
            for id in changes.keys.sorted() {
                do {
                    guard let value = changes[id] as? [String:Any], let enabled = value["enabled"] as? Bool,
                          let analyze = value["auto_analyze"] as? Bool else { throw ProbeError.message("课程设置格式异常。") }
                    saved.append(try rules.configure(courseID:id, enabled:enabled, autoAnalyze:analyze, modelReady:modelService != nil, now:now))
                } catch { errors.append(["id":id, "message":(error as? LocalizedError)?.errorDescription ?? "保存失败。 "]) }
            }
        } catch { errors = changes.keys.sorted().map { ["id":$0, "message":(error as? LocalizedError)?.errorDescription ?? "无法打开自动化设置。"] } }
        if !saved.isEmpty { dataVersion += 1 }
        return ["saved":saved, "errors":errors]
    }

    func retryAutomation(courseID: String) throws {
        guard signedIn, !working, let store = probeStore, !owner.isEmpty else { throw ProbeError.message("请先连接学校，并等待当前任务完成。") }
        let lease = automationLease ?? store.directory.flatMap { AutomationLease(directory:$0) }
        guard store.directory == nil || lease != nil else { throw ProbeError.message("另一个应用窗口正在运行自动化。") }
        defer { withExtendedLifetime(lease) {} }
        let rules = try NativeAutomationRules(owner:owner, store:store)
        try rules.retry(courseID:courseID, now:ISO8601DateFormatter().string(from:Date()))
        dataVersion += 1
        Task { await automationTick() }
    }

    func automationTick(now: Date = Date()) async {
        guard signedIn, !working, let school, let sync, let store = probeStore, !owner.isEmpty else {
            if !signedIn { automationLease = nil; automationScheduler = nil; automationOwner = "" }
            return
        }
        let account = owner
        do {
            let active = try store.list(owner: account, kind: "automation").contains { ($0 as? [String:Any])?["enabled"] as? Bool == true }
            guard active else { automationLease = nil; automationScheduler = nil; automationOwner = ""; return }
            if automationLease == nil, let directory = store.directory { automationLease = AutomationLease(directory: directory) }
            guard store.directory == nil || automationLease != nil else { return }
            if automationScheduler == nil || automationOwner != account {
                automationScheduler = try NativeAutomationScheduler(owner: account, store: store, onChange: { [weak self] id, state in
                    self?.activeAutomationJobID = state == "running" ? id : nil
                    self?.dataVersion += 1
                }); automationOwner = account
            }
            guard let scheduler = automationScheduler else { return }
            working = true; defer { working = false; dataVersion += 1 }
            let host = AutomationHost(schedule: { now in
                guard self.school === school, self.owner == account else { throw ProbeError.message("学校会话已结束，请重新登录。") }
                _ = try await NativeAutomationRules(owner: account, store: store).syncSchedule(school: school, now: now)
            }, catalogue: { id in
                try await self.automationCatalogue(courseID:id, account:account, school:school, sync:sync, store:store)
            }, materials: { lesson in
                guard self.school === school, self.owner == account else { throw ProbeError.message("学校会话已结束，请重新登录。") }
                let material = try await NativeLearningWorkflow.fetchMaterials(owner:account, store:store, school:school, recordingCourseID:lesson.recordingCourseID, lessonID:lesson.id)
                return ((material["segments"] as? [Any])?.count ?? 0, (material["slides"] as? [Any])?.count ?? 0)
            }, analyze: { id in
                guard self.school === school, self.owner == account, let model = self.modelService else { throw ProbeError.message("学校会话或模型配置不可用。") }
                let workflow = try NativeLearningWorkflow(owner:account, store:store, analysis:NativeAnalysis(model:model))
                _ = try await workflow.analyze(lessonID:id, modelName:self.modelName, createdAt:ISO8601DateFormatter().string(from:Date()))
            })
            try await scheduler.tick(now: now, transport: host)
        } catch { reportIssue((error as? LocalizedError)?.errorDescription ?? "课程自动化检查失败。") }
    }

    private func parallelCatalogue(course:Course, account:String, school:NativeSchoolSession, store:NativeStore) async throws -> [ProbeLesson] {
        let (worker, hasRecordingToken) = try await school.recordingWorker()
        let synchronizer = try NativeCourseSync(owner:account, store:store, school:worker)
        let cached = try store.get(owner:account, kind:"course", id:course.id) as? [String:Any]
        if hasRecordingToken, let id = cached?["recording_id"] as? String, !id.isEmpty {
            do { return try await synchronizer.syncCatalogue(courseID:course.id, recordingID:id, syncedAt:ISO8601DateFormatter().string(from:Date())) }
            catch is SchoolAuthorizationIssue { /* Reauthorize only the affected worker. */ }
        }
        let authorization: (id:String,token:String)
        if let recordingAuthorization { authorization = try await recordingAuthorization(course,worker) }
        else {
            let entry = try await worker.recordingEntry(course:course)
            let resolver = RecordingAuthorizationResolver(dataStore:browser.configuration.websiteDataStore)
            authorization = try await resolver.resolve(entry:entry)
        }
        guard self.school === school, owner == account, signedIn else { throw ProbeError.message("学校会话已结束，请重新登录。") }
        try await worker.setRecording(token:authorization.token)
        return try await synchronizer.syncCatalogue(courseID:course.id, recordingID:authorization.id, syncedAt:ISO8601DateFormatter().string(from:Date()))
    }

    private func automationCatalogue(courseID: String, account: String, school: NativeSchoolSession, sync: NativeCourseSync, store: NativeStore) async throws -> [AutomationLesson] {
        guard self.school === school, owner == account,
              let row = try store.get(owner:account, kind:"course", id:courseID) as? [String:Any],
              let name = row["name"] as? String else { throw ProbeError.message("课程或学校会话不可用。") }
        let course = Course(id:courseID, name:name, taskID:row["task_id"] as? String ?? "", semester:row["semester"] as? String ?? "", code:row["code"] as? String ?? "", teachers:row["teachers"] as? String ?? "")
        recordPoll?.cancel(); browser.stopLoading()
        recordingCourseID = nil; recordingSyncCourse = courseID
        defer { recordingSyncCourse = nil }
        if let recordingAuthorization {
            let authorization = try await recordingAuthorization(course, school)
            guard self.school === school, owner == account, !authorization.id.isEmpty, !authorization.token.isEmpty else { throw ProbeError.message("录播授权不可用。") }
            try await school.setRecording(token:authorization.token)
            let items = try await sync.syncCatalogue(courseID:courseID, recordingID:authorization.id, syncedAt:ISO8601DateFormatter().string(from:Date()))
            return items.map { AutomationLesson(id:$0.id, start:Date(timeIntervalSince1970:TimeInterval($0.startAt)), hasRecording:$0.hasRecording, recordingCourseID:$0.recordingCourseID.isEmpty ? authorization.id : $0.recordingCourseID) }
        }
        let entry = try await school.recordingEntry(course: course)
        guard self.school === school, owner == account else { throw ProbeError.message("学校会话已结束，请重新登录。") }
        browser.load(URLRequest(url:entry))
        for _ in 0..<70 {
            try Task.checkCancellation()
            let cookies = await browser.configuration.websiteDataStore.httpCookieStore.allCookies()
            guard self.school === school, owner == account else { throw ProbeError.message("学校会话已结束，请重新登录。") }
            if let bearer = recordingBearer(cookies), let id = recordingCourseID {
                try await school.setRecording(token:bearer)
                let items = try await sync.syncCatalogue(courseID:courseID, recordingID:id, syncedAt:ISO8601DateFormatter().string(from:Date()))
                recordingAccess = id; lessons = items; recordingReady = true; lessonCount = items.count
                recordingSyncStates[courseID] = .succeeded(items.filter(\.hasRecording).count)
                return items.map { AutomationLesson(id:$0.id, start:Date(timeIntervalSince1970:TimeInterval($0.startAt)), hasRecording:$0.hasRecording, recordingCourseID:$0.recordingCourseID.isEmpty ? id : $0.recordingCourseID) }
            }
            try await Task.sleep(nanoseconds:500_000_000)
        }
        throw ProbeError.message("录播授权或课程编号不可用，请重新登录后重试。")
    }

    func checkRecording(_ course: Course) {
        guard school != nil, !working, recordingSyncCourse == nil else { return }
        recordPoll?.cancel(); recordingReady = false; lessonCount = nil; lessons = []; recordingAccess = nil; recordingCourseID = nil; navigationIssue = ""; blockedNavigation = ""
        recordingSyncCourse = course.id; recordingSyncStates[course.id] = .syncing; working = true
        Task { [self] in
            guard let school else { recordingSyncCourse = nil; working = false; return }
            do {
                status = "正在连接录播系统。"
                let entry = try await school.recordingEntry(course: course)
                guard self.school === school, recordingSyncCourse == course.id else { return }
                browser.load(URLRequest(url: entry))
                recordPoll = Task { [weak self] in
                    for _ in 0..<70 {
                        if Task.isCancelled { return }
                        guard let self else { return }
                        let cookies = await self.browser.configuration.websiteDataStore.httpCookieStore.allCookies()
                        guard self.school === school, self.recordingSyncCourse == course.id else { return }
                        if let bearer = recordingBearer(cookies), let id = self.recordingCourseID {
                            await self.checkCatalogue(course: course, recordingID: id, bearer: bearer)
                            return
                        }
                        try? await Task.sleep(nanoseconds: 500_000_000)
                    }
                    if let self {
                        let cookies = await self.browser.configuration.websiteDataStore.httpCookieStore.allCookies()
                        let tokenCookie = cookies.contains { $0.name == "_token" && $0.domain.hasSuffix("buaa.edu.cn") }
                        self.status = (recordingBearer(cookies) != nil ? "已取得录播授权，但跳转没有提供课程编号。"
                            : tokenCookie ? "发现录播 Cookie，但授权格式无法识别。" : "未取得录播 Cookie。")
                            + self.blockedNavigation + self.navigationIssue
                        self.recordingSyncStates[course.id] = .failed(self.status)
                        self.reportIssue(self.status)
                        self.recordingSyncCourse = nil; self.working = false
                    }
                }
            } catch {
                guard self.school === school, recordingSyncCourse == course.id else { return }
                working = false; recordingSyncCourse = nil
                reportIssue((error as? LocalizedError)?.errorDescription ?? "录播入口连接失败，请检查学校网络。")
                recordingSyncStates[course.id] = .failed(status)
            }
        }
    }

    private func checkCatalogue(course: Course, recordingID: String, bearer: String) async {
        do {
            guard let school, let sync else { throw ProbeError.message("请先连接学校。") }
            try await school.setRecording(token: bearer)
            let items = try await sync.syncCatalogue(courseID: course.id, recordingID: recordingID, syncedAt: ISO8601DateFormatter().string(from: Date()))
            guard self.school === school, recordingSyncCourse == course.id else { return }
            recordingAccess = recordingID; lessons = items
            recordingReady = true; lessonCount = items.count
            status = "录播授权成功，已读取 \(items.count) 节课次。请选择回放验证课堂文本。"
            recordingSyncStates[course.id] = .succeeded(items.filter(\.hasRecording).count)
            recordingSyncCourse = nil; working = false; dataVersion += 1
        } catch {
            guard recordingSyncCourse == course.id else { return }
            reportIssue((error as? LocalizedError)?.errorDescription ?? "录播目录读取失败，请检查网络或重新登录。")
            recordingSyncStates[course.id] = .failed(status); recordingSyncCourse = nil; working = false
        }
    }

    func checkTranscript(_ lesson: ProbeLesson) {
        Task { do { try await acquireMaterials(lesson.id) } catch { reportIssue(error.localizedDescription) } }
    }

    func acquireMaterials(_ lessonID: String) async throws {
        guard let school, let sync, let store = probeStore, !owner.isEmpty, signedIn else { throw ProbeError.message("请先登录学校，再获取课堂材料。") }
        guard !working else { throw ProbeError.message("请等待当前操作完成。") }
        guard let lesson = try store.get(owner:owner,kind:"lesson",id:lessonID) as? [String:Any],
              let courseID = lesson["course_id"] as? String,
              lesson["has_recording"] as? Bool == true else { throw ProbeError.message("这节课暂无可获取的回放。") }
        let account = owner
        let id = UUID().uuidString, created = ISO8601DateFormatter().string(from:Date())
        var job: [String:Any] = ["id":id,"kind":"materials","target":lessonID,"course_id":courseID,"title":lesson["title"] as? String ?? "课次","created_at":created,"status":"running","message":"正在获取课堂材料与 PPT 图片。"]
        try store.put(owner:account,kind:"job",id:id,value:job)
        working = true; activeMaterialJobID = id; operationIssue = ""; materialFeedback[lessonID] = "正在获取课堂材料与 PPT 图片…"; dataVersion += 1
        defer { working = false; activeMaterialJobID = nil; dataVersion += 1 }
        do {
            if !recordingReady {
                _ = try await automationCatalogue(courseID:courseID,account:account,school:school,sync:sync,store:store)
            }
            guard self.school === school,
                  let current = try store.get(owner:account,kind:"lesson",id:lessonID) as? [String:Any],
                  let recordingID = current["recording_course_id"] as? String else { throw ProbeError.message("课次录播信息已改变，请重新同步。") }
            let material = try await NativeLearningWorkflow.fetchMaterials(owner:account,store:store,school:school,recordingCourseID:recordingID,lessonID:lessonID)
            let missing = material["uncached_slides"] as? Int ?? 0
            status = "课堂材料已保存：文本 \((material["segments"] as? [Any])?.count ?? 0) 段，PPT 截图 \((material["slides"] as? [Any])?.count ?? 0) 张。"
            if missing > 0 { reportIssue("课堂文本已保存；\(missing) 张 PPT 图片未能保存到本地，可刷新材料重试。") }
            materialFeedback[lessonID] = status
            job["status"] = "completed"; job["message"] = status
        } catch {
            job["status"] = "failed"; job["message"] = error.localizedDescription
            job["finished_at"] = ISO8601DateFormatter().string(from:Date())
            try store.put(owner:account,kind:"job",id:id,value:job)
            materialFeedback[lessonID] = error.localizedDescription
            reportIssue(error.localizedDescription)
            throw error
        }
        job["finished_at"] = ISO8601DateFormatter().string(from:Date())
        try store.put(owner:account,kind:"job",id:id,value:job)
    }

    func webView(_ webView: WKWebView, decidePolicyFor action: WKNavigationAction, decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        guard let url = action.request.url else { decisionHandler(.cancel); return }
        // The school's recording entry currently redirects to HTTP. Never send its signed
        // query over plaintext; retry only this known school host using HTTPS.
        if let target = upgradedRecordingURL(url) {
            var request = action.request
            request.url = target
            decisionHandler(.cancel)
            DispatchQueue.main.async { webView.load(request) }
            return
        }
        if let id = courseID(from: url) { recordingCourseID = id }
        if !schoolHost(url) {
            let scheme = url.scheme?.lowercased() ?? "unknown"
            let host = url.host?.lowercased() ?? "无主机"
            blockedNavigation = " 已阻止跳转（协议：\(scheme)，主机：\(host)）。"
        }
        decisionHandler(schoolHost(url) ? .allow : .cancel)
    }
    func webView(_ webView: WKWebView, createWebViewWith configuration: WKWebViewConfiguration, for action: WKNavigationAction, windowFeatures: WKWindowFeatures) -> WKWebView? {
        if let url = action.request.url, schoolHost(url) { webView.load(action.request) }
        return nil
    }
    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        let issue = error as NSError
        if issue.code == NSURLErrorCancelled || (issue.domain == "WebKitErrorDomain" && issue.code == 102) { return }
        navigationIssue = " 页面加载错误：\((error as NSError).domain) \((error as NSError).code)。"
        reportIssue("学校页面无法打开。" + navigationIssue)
    }
}

struct SchoolBrowser: NSViewRepresentable {
    let browser: WKWebView
    func makeNSView(context: Context) -> WKWebView { browser }
    func updateNSView(_ view: WKWebView, context: Context) {}
}

struct ContentView: View {
    @StateObject private var probe = Probe()
    var body: some View {
        HSplitView {
            VStack(alignment: .leading, spacing: 12) {
                Text("学校连接验证").font(.title2.bold())
                Text(probe.status).textSelection(.enabled)
                Button(probe.signedIn ? "重新登录" : "连接学校") { probe.startLogin() }
                if probe.signedIn {
                    Button("验证课表") { probe.checkSchedule() }.disabled(probe.working)
                    Picker("学期", selection: Binding(get: { probe.selectedTerm }, set: { term in
                        probe.selectedTerm = term
                        Task { await probe.loadCourses() }
                    })) {
                        ForEach(probe.semesters, id: \.self) { Text($0).tag($0) }
                    }
                    ScrollView {
                        VStack(alignment: .leading, spacing: 4) {
                            ForEach(probe.courses) { course in
                                Button(course.name) { probe.checkRecording(course) }
                                    .frame(maxWidth: .infinity, alignment: .leading)
                                    .disabled(probe.working)
                            }
                        }
                    }
                    if !probe.lessons.isEmpty {
                        Text("课堂文本验证").font(.headline)
                        ScrollView {
                            VStack(alignment: .leading, spacing: 4) {
                                ForEach(probe.lessons) { lesson in
                                    Button(lesson.title) { probe.checkTranscript(lesson) }
                                        .disabled(probe.working || !lesson.hasRecording)
                                }
                            }
                        }.frame(maxHeight: 160)
                    }
                }
                Spacer()
            }.padding(20).frame(minWidth: 270, idealWidth: 320, maxWidth: 360)
            SchoolBrowser(browser: probe.browser).frame(minWidth: 600, minHeight: 500)
        }.frame(minWidth: 900, minHeight: 600)
    }
}

@main
struct BUAALoginProbeApp: App {
    init() {
        for name in ["source-serif-4.ttf", "source-serif-4-italic.ttf"] {
            if let font = Bundle.main.resourceURL?.appendingPathComponent(name) {
                CTFontManagerRegisterFontsForURL(font as CFURL, .process, nil)
            }
        }
    }
    var body: some Scene {
        WindowGroup(ProcessInfo.processInfo.environment["BUAA_PREVIEW_TITLE"] ?? "BUAA Learn") {
            if ProcessInfo.processInfo.environment["BUAA_LOGIN_PROBE"] == "1" {
                ContentView()
            } else {
                WorkspaceVisualReference()
            }
        }.defaultSize(width: 1400, height: 868)
    }
}
