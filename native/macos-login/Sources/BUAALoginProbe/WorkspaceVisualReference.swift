import AppKit
import SwiftUI
import UniformTypeIdentifiers

private struct TaskSource: Identifiable {
    let id = UUID()
    let rows: [TimelineTranscriptRow]
}

/// Native workspace under construction; incomplete functions are identified in the UI.
struct WorkspaceVisualReference: View {
    @StateObject private var probe = Probe()
    @StateObject private var desktopSettings = NativeDesktopSettings()
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    private let ink = Color(red: 0.10, green: 0.10, blue: 0.10)
    private let muted = Color(red: 0.46, green: 0.46, blue: 0.46)
    private let blue = Color(red: 0.25, green: 0.40, blue: 0.54)
    private let nav: [(String,String)] = [("library","本学期课程"),("calendar-days","课程表"),("notebook-pen","作业与事项"),("timer","课程自动化")]
    @State private var loginOpen = false
    @State private var sidebarOpen = true
    @State private var page = "本学期课程"
    @State private var tasks: [[String:Any]] = []
    @State private var jobs: [[String:Any]] = []
    @State private var notices: [[String:Any]] = []
    @State private var savedCourses: [[String:Any]] = []
    @State private var savedLessons: [[String:Any]] = []
    @State private var savedMaterials: [[String:Any]] = []
    @State private var savedAnalyses: [[String:Any]] = []
    @State private var savedAutomations: [[String:Any]] = []
    @State private var savedSchedule: [[String:Any]]?
    @State private var savedSemesterSchedule: [[String:Any]]?
    @State private var timetableWeek: Int?
    @State private var timetableWeekdaysOnly = true
    @State private var courseReturnPage = "本学期课程"
    @State private var detailCourse: Course?
    @State private var selectedLesson: String?
    @State private var transcriptOpen = false
    @State private var notesOpen = false
    @State private var slidesOpen = false
    @State private var taskSource: TaskSource?
    @State private var exportingSlides = false
    @State private var slideExportMessage = ""
    @State private var slideExportLessonID = ""
    @State private var baseURL = ""
    @State private var modelName = ""
    @State private var modelKey = ""
    @State private var feedback = ""
    @State private var automationDraft: [String:Any]?
    private func login() { loginOpen = true; probe.startLogin() }
    private func icon(_ name: String, size: CGFloat = 17) -> some View {
        let url = Bundle.main.resourceURL?.appendingPathComponent("icons/\(name).svg")
        let image = url.flatMap { NSImage(contentsOf: $0) } ?? NSImage()
        return Image(nsImage: image).resizable().renderingMode(.template).frame(width:size,height:size).foregroundStyle(ink.opacity(0.8))
    }
    private func navRow(_ name: String, _ title: String, selected: Bool = false) -> some View {
        Button { page = title } label: {
            HStack(spacing:10) {
                icon(name, size:18).frame(width:18)
                Text(title).font(.system(size:14,weight:selected ? .semibold : .regular)).opacity(sidebarOpen ? 1 : 0)
                Spacer(minLength:0)
            }.frame(width:202,height:36,alignment:.leading).padding(.horizontal,9)
                .background(alignment:.leading) { RoundedRectangle(cornerRadius:6).fill(selected ? Color.black.opacity(0.055) : .clear).frame(width:sidebarOpen ? 220 : 36) }
                .modifier(HoverFeedback(width:sidebarOpen ? 220 : 36))
        }.buttonStyle(.plain).accessibilityLabel(title).help(sidebarOpen ? "" : title)
    }
    private var sidebar: some View {
        VStack(alignment:.leading, spacing:0) {
            HStack(spacing:8) {
                icon("book-open",size:18).frame(width:18)
                (Text("BUAA").font(.custom("SourceSerif4Roman-Bold",size:22)).foregroundColor(Color(red:0.17,green:0.42,blue:0.64)) + Text(" Learn").font(.custom("SourceSerif4Italic-Italic",size:22)).foregroundColor(Color(red:0.08,green:0.13,blue:0.18)))
                    .opacity(sidebarOpen ? 1 : 0)
                Spacer()
            }.frame(width:202,height:64,alignment:.leading).padding(.horizontal,19)
            VStack(spacing:5) {
                ForEach(nav, id:\.1) { item in navRow(item.0,item.1,selected:item.1 == page || (page == "课程详情" && item.1 == courseReturnPage)) }
            }.padding(.horizontal,10)
            Spacer(minLength:0)
            Divider().overlay(Color.black.opacity(0.04))
            VStack(spacing:5) {
                navRow("clock-3","任务状态",selected:page == "任务状态")
                navRow("settings-2","设置",selected:page == "设置")
                navRow("circle-user-round","个人学习空间",selected:page == "个人学习空间" || (page == "课程详情" && courseReturnPage == "个人学习空间"))
            }.padding(.horizontal,10).padding(.vertical,8)
        }.frame(width:240,alignment:.leading)
            .frame(width:sidebarOpen ? 240 : 56,alignment:.leading).clipped()
            .background(Color(red:0.969,green:0.969,blue:0.969)).overlay(alignment:.trailing){Rectangle().fill(Color.black.opacity(0.08)).frame(width:1)}
    }
    private var topbar: some View {
        HStack(spacing:14) {
            Button { withAnimation(reduceMotion ? nil : .linear(duration:0.2)) { sidebarOpen.toggle() } } label: { icon("panel-left",size:18).frame(width:28,height:28).modifier(HoverFeedback()) }.buttonStyle(.plain).help(sidebarOpen ? "收起侧栏" : "展开侧栏").accessibilityLabel(sidebarOpen ? "收起侧栏" : "展开侧栏").keyboardShortcut("b",modifiers:.command)
            Text(page == "课程详情" ? (detailCourse?.name ?? "课程学习") : page).font(.custom("Songti SC",size:14)).fontWeight(.semibold)
            Spacer()
            Button { login() } label: { HStack(spacing:7) { icon("log-in",size:16); Text(probe.signedIn ? "已登录" : "SSO 登录").font(.system(size:14,weight:.medium)) }
                .padding(.horizontal,13).frame(height:36).overlay(RoundedRectangle(cornerRadius:7).stroke(Color.black.opacity(0.12))).modifier(HoverFeedback(cornerRadius:7))
            }.buttonStyle(.plain).disabled(probe.working)
        }.padding(.horizontal,28).frame(height:56).overlay(alignment:.bottom){Rectangle().fill(Color.black.opacity(0.06)).frame(height:1)}
    }
    var body: some View {
        HStack(spacing:0) {
            sidebar
            VStack(spacing:0) {
                topbar
                if !probe.sessionIssue.isEmpty {
                    HStack {
                        Text(probe.sessionIssue).font(.system(size:14))
                        Spacer()
                        Button("重新登录") { login() }.disabled(probe.working)
                    }.padding(12).background(Color.orange.opacity(0.12))
                }
                if !probe.operationIssue.isEmpty && probe.operationIssue != probe.sessionIssue {
                    HStack {
                        Text(probe.operationIssue).font(.system(size:14)).textSelection(.enabled)
                        Spacer()
                        Button("关闭") { probe.clearOperationIssue() }
                    }.padding(12).background(Color.orange.opacity(0.12))
                }
                if probe.recoveredJobs > 0 {
                    HStack {
                        Text("已恢复 \(probe.recoveredJobs) 条上次运行的任务记录。处理中的任务可重新执行。 ").font(.system(size:14))
                        Spacer()
                        Button("查看任务") { page = "任务状态"; probe.dismissRecoveredJobs() }
                        Button("关闭") { probe.dismissRecoveredJobs() }
                    }.padding(12).background(blue.opacity(0.08))
                }
                if let job = probe.courseSyncJob { syncProgressBar(job) }
                GeometryReader { _ in
                Group { switch page {
                case "本学期课程": courseList
                case "课程详情": courseDetail
                case "课程表": ScheduleTimetable(workspace:probe.workspaceRevision, rows:savedSemesterSchedule ?? savedSchedule, fullSemester:savedSemesterSchedule != nil, semester:TimetableSemester(id:probe.selectedTerm), selectedWeek:$timetableWeek, weekdaysOnly:$timetableWeekdaysOnly, courses:probe.courses, syncing:probe.working, canSync:probe.signedIn, sync:{ probe.checkSemesterSchedule() }, open:{ course, date in openCourse(course, from:"课程表", date:date) })
                case "个人学习空间": learningRecordsPage
                case "作业与事项": tasksPage
                case "课程自动化": automationPage
                case "任务状态": jobsPage
                case "设置": settingsPage
                default: courseList
                } }.frame(maxWidth:.infinity,maxHeight:.infinity,alignment:.topLeading)
                }
                Text("BUAA Learn 为非官方学习工具。重要事项请结合课堂原话核对。")
                    .font(.system(size:14)).foregroundStyle(muted).padding(.vertical,11)
            }.frame(maxWidth:.infinity,maxHeight:.infinity).background(.white)
        }.frame(minWidth:960,minHeight:640).font(.system(size:15)).preferredColorScheme(.light)
            .onAppear {
                do {
                    #if DEBUG
                    let testBase = ProcessInfo.processInfo.environment["BUAA_TEST_WORKSPACE_BASE"].map { URL(fileURLWithPath: $0, isDirectory: true) }
                    try probe.restoreWorkspace(baseDirectory: testBase)
                    #else
                    try probe.restoreWorkspace()
                    #endif
                    refreshRecords()
                    probe.startAutomationLoop()
                    Task { await probe.resumeSchoolSession() }
                }
                catch { feedback = (error as? LocalizedError)?.errorDescription ?? "无法打开本地学习记录。" }
            }
            .sheet(isPresented:$loginOpen) {
                VStack(spacing:0) { SchoolBrowser(browser:probe.browser); HStack { Text(probe.status).font(.system(size:14)); Spacer(); Button("关闭") { loginOpen = false } }.padding() }.frame(minWidth:720,minHeight:540)
            }.onChange(of:probe.signedIn) { if $0 { loginOpen = false }; refreshRecords() }
            .onChange(of:page) { value in
                refreshRecords()
                if value == "设置" { loadModelDraft(); desktopSettings.checkOnce() }
                if value == "课程表" && savedSemesterSchedule == nil && probe.signedIn { probe.checkSemesterSchedule() }
            }
            .onChange(of:probe.modelRevision) { _ in loadModelDraft() }
            .onChange(of:probe.workspaceRevision) { _ in automationDraft = nil; timetableWeek = nil }
            .onChange(of:probe.dataVersion) { _ in refreshRecords() }
            .alert("提示", isPresented: Binding(get:{ !feedback.isEmpty }, set:{ if !$0 { feedback = "" } })) { Button("好", role:.cancel) { feedback = "" } } message: { Text(feedback) }
    }
    private func syncProgressBar(_ job: CourseSyncJob) -> some View {
        HStack(alignment:.center,spacing:18) {
            VStack(alignment:.leading,spacing:5) {
                HStack(spacing:7) {
                    if job.phase == .running { ProgressView().controlSize(.small) }
                    Text(job.kind == "recordings" ? "同步最新回放" : "同步课程").font(.system(size:14,weight:.semibold))
                }
                Text(job.message).font(.system(size:14)).foregroundStyle(job.phase == .failed ? Color(red:0.65,green:0.34,blue:0.15) : .secondary)
            }
            Spacer(minLength:10)
            VStack(alignment:.trailing,spacing:5) {
                if job.phase == .running {
                    Text("处理中").font(.system(size:14)).foregroundStyle(.secondary)
                    ProgressView().progressViewStyle(.linear).frame(width:140)
                } else if job.phase == .failed {
                    Button("重试") { Task { await probe.retryCourseSync() } }.disabled(!probe.signedIn || probe.working)
                } else {
                    Text("已完成").font(.system(size:14)).foregroundStyle(.secondary)
                }
                Button("查看任务") { page = "任务状态" }.buttonStyle(.plain).font(.system(size:14)).foregroundStyle(blue)
            }
        }.padding(.horizontal,28).padding(.vertical,12)
            .background(job.phase == .failed ? Color(red:1,green:0.98,blue:0.94) : Color(red:0.96,green:0.97,blue:0.98))
            .accessibilityElement(children:.contain)
    }
    private func refreshRecords() {
        do {
            tasks = try probe.records(kind:"task")
            jobs = try probe.records(kind:"job")
            savedCourses = try probe.records(kind:"course")
            savedLessons = try probe.records(kind:"lesson")
            savedMaterials = try probe.records(kind:"materials")
            savedAnalyses = try probe.records(kind:"analysis")
            savedAutomations = try probe.records(kind:"automation")
            savedSchedule = try probe.cachedSchedule()
            savedSemesterSchedule = try probe.cachedSemesterSchedule()
            notices = savedAnalyses.flatMap { analysis in
                (analysis["notices"] as? [[String:Any]] ?? []).map { notice in
                    var row = notice; row["course_name"] = analysis["course_name"]; row["lesson_title"] = analysis["lesson_title"]; row["course_id"] = analysis["course_id"]; row["lesson_id"] = analysis["lesson_id"]; return row
                }
            }
        } catch { feedback = (error as? LocalizedError)?.errorDescription ?? "读取本地数据失败。" }
    }
    private func loadModelDraft() {
        baseURL = probe.modelBaseURL; modelName = probe.modelName; modelKey = ""
    }
    private func panel<Content: View>(@ViewBuilder content: () -> Content) -> some View {
        VStack(alignment:.leading,spacing:0) { content() }.frame(maxWidth:.infinity,alignment:.leading)
            .background(.white).overlay(RoundedRectangle(cornerRadius:8).stroke(Color.black.opacity(0.1)))
    }
    private func pageShell<Content: View>(_ title: String, @ViewBuilder content: () -> Content) -> some View {
        ScrollView { VStack(alignment:.leading,spacing:20) {
            Text(title).font(.custom("Songti SC",size:30))
            content()
        }.frame(maxWidth:1100,alignment:.leading).padding(32).frame(maxWidth:.infinity,alignment:.top) }
    }
    private var tasksPage: some View {
        pageShell("作业与事项") {
            Text("作业由 AI 提取，请核对课堂原话。").font(.system(size:14)).padding(12).frame(maxWidth:.infinity,alignment:.leading).background(Color.yellow.opacity(0.12)).clipShape(RoundedRectangle(cornerRadius:6))
            panel {
                HStack { Text("课堂作业").font(.headline); Spacer(); Text("\(tasks.filter { !($0["done"] as? Bool ?? false) }.count) 项未完成").foregroundStyle(.secondary).font(.system(size:14)) }.padding(16)
                Divider()
                if tasks.isEmpty { Text("还没有提取到作业。先选择一节课进行分析；没有结果不代表老师没有布置作业。").foregroundStyle(.secondary).padding(16) }
                ForEach(tasks.indices, id:\.self) { index in
                    let row = tasks[index]
                    if let id = row["id"] as? String {
                        HStack(alignment:.top,spacing:12) {
                            Toggle("标记作业完成", isOn:Binding(get:{ row["done"] as? Bool ?? false }, set:{ value in
                                do { try probe.setTaskDone(id:id, done:value) } catch { feedback = error.localizedDescription }
                            })).labelsHidden().toggleStyle(.checkbox)
                            VStack(alignment:.leading,spacing:5) { Text(row["text"] as? String ?? ""); Text("\(row["course_name"] as? String ?? "") · \(row["lesson_title"] as? String ?? "")").font(.system(size:14)).foregroundStyle(.secondary); Text("截止：\(row["deadline"] as? String ?? "未说明") · 提交：\(row["submission"] as? String ?? "未说明")").font(.system(size:14)).foregroundStyle(.secondary); Text("待核验").font(.system(size:14)) }
                            if let lessonID = row["lesson_id"] as? String,
                               let analysis = savedAnalyses.first(where:{ $0["lesson_id"] as? String == lessonID }) {
                                let evidence = TimelineEvidence(analysis:analysis).range(for:row)
                                Button("查看课堂原话") { taskSource = TaskSource(rows:evidence) }.disabled(evidence.isEmpty)
                            }
                        }.padding(16); Divider()
                    }
                }
            }
            panel {
                HStack { Text("重要事项").font(.headline); Spacer(); Text("\(notices.count) 条").font(.system(size:14)).foregroundStyle(.secondary) }.padding(16); Divider()
                if notices.isEmpty { Text("当前尚未从已分析的课堂中提取到重要事项。").foregroundStyle(.secondary).padding(16) }
                ForEach(notices.indices, id:\.self) { index in
                    let notice = notices[index]
                    VStack(alignment:.leading,spacing:8) {
                        Text(notice["text"] as? String ?? "").textSelection(.enabled)
                        Text("\(notice["course_name"] as? String ?? "") · \(notice["lesson_title"] as? String ?? "")").font(.system(size:14)).foregroundStyle(.secondary)
                        if let lessonID = notice["lesson_id"] as? String,
                           let analysis = savedAnalyses.first(where: { $0["lesson_id"] as? String == lessonID }) {
                            let rows = TimelineEvidence(analysis: analysis).range(for: notice)
                            if !rows.isEmpty {
                                DisclosureGroup("查看课堂原话") {
                                    ForEach(rows) { row in
                                        HStack(alignment:.top,spacing:12) {
                                            Text(lessonClock(row.start)).monospacedDigit().foregroundStyle(.secondary)
                                            Text(row.text).textSelection(.enabled)
                                        }.padding(.vertical,4)
                                    }
                                }.font(.system(size:14))
                            }
                        }
                    }.frame(maxWidth:.infinity,alignment:.leading).padding(16)
                    Divider()
                }
            }
        }.sheet(item:$taskSource) { source in
            MaterialTranscriptView(material:["segments":source.rows.map { ["id":$0.id,"start":$0.start,"end":$0.end,"text":$0.text] }]) { taskSource = nil }
        }
    }
    private var jobsPage: some View {
        pageShell("任务状态") { panel {
            if jobs.isEmpty { Text("暂无任务。").foregroundStyle(.secondary).padding(16) }
            ForEach(jobs.indices, id:\.self) { index in
                let job = jobs[index]
                let status = job["status"] as? String ?? ""
                VStack(alignment:.leading,spacing:5) {
                    Text(["sync":"同步学期课程", "recordings":"同步最新回放", "semester-schedule":"同步学期课表", "analyze":"分析这节课", "materials":"获取课堂材料", "schedule":"同步课表", "automation":"课程自动化"][job["kind"] as? String ?? ""] ?? "任务").font(.headline)
                    if let title = job["title"] as? String { Text(title).font(.system(size:14)).foregroundStyle(.secondary) }
                    Text(job["message"] as? String ?? "").foregroundStyle(.secondary)
                    HStack {
                        let isCurrentAnalysis = (job["kind"] as? String) == "analyze" && (job["id"] as? String) == probe.analysisJob?.id && probe.analysisJob?.phase == .running
                        let isCurrentAutomation = ["automation", "schedule", "semester-schedule"].contains(job["kind"] as? String ?? "") && (job["id"] as? String) == probe.activeAutomationJobID
                        let isCurrentSync = ["sync","recordings"].contains(job["kind"] as? String ?? "") && (job["id"] as? String) == probe.courseSyncJob?.id && probe.courseSyncJob?.phase == .running
                        let isCurrentMaterials = (job["kind"] as? String) == "materials" && (job["id"] as? String) == probe.activeMaterialJobID
                        Text(status == "running" && !isCurrentAnalysis && !isCurrentAutomation && !isCurrentSync && !isCurrentMaterials ? "运行状态未确认" : (["running":"处理中", "completed":"已完成", "failed":"失败", "interrupted":"已中断"][status] ?? status)).font(.system(size:14))
                        if ["failed","interrupted"].contains(status), ["sync","recordings"].contains(job["kind"] as? String ?? "") { Button("重新执行") { Task { if job["kind"] as? String == "recordings" { await probe.loadLatestRecordings() } else { await probe.loadCourses() } } }.disabled(!probe.signedIn || probe.working) }
                        if (["failed","interrupted"].contains(status) || (status == "running" && !isCurrentAutomation)), ["schedule","semester-schedule"].contains(job["kind"] as? String ?? "") { Button("重新执行") { if job["kind"] as? String == "semester-schedule" { probe.checkSemesterSchedule() } else { probe.checkSchedule() } }.disabled(!probe.signedIn || probe.working) }
                        if (["failed","interrupted"].contains(status) || (status == "running" && !isCurrentAutomation)), (job["kind"] as? String) == "automation", let courseID = job["target"] as? String {
                            Button("重试") { do { try probe.retryAutomation(courseID:courseID) } catch { feedback = error.localizedDescription } }.disabled(!probe.signedIn || probe.working)
                        }
                        if ["failed","interrupted"].contains(status), (job["kind"] as? String) == "analyze", let lessonID = job["target"] as? String {
                            Button("重试") { Task { do { try await probe.analyzeLesson(lessonID) } catch { feedback = error.localizedDescription } } }.disabled(probe.working)
                        }
                        if ["failed","interrupted"].contains(status), (job["kind"] as? String) == "materials", let lessonID = job["target"] as? String {
                            Button("重新获取") { Task { do { try await probe.acquireMaterials(lessonID) } catch { feedback = error.localizedDescription } } }.disabled(!probe.signedIn || probe.working)
                        }
                        if status == "completed", (job["kind"] as? String) == "analyze",
                           let lessonID = job["target"] as? String, let courseID = job["course_id"] as? String,
                           let course = probe.courses.first(where: { $0.id == courseID }),
                           savedAnalyses.contains(where: { $0["lesson_id"] as? String == lessonID }) {
                            Button("查看笔记") { openCourse(course); selectedLesson = lessonID; notesOpen = true }
                        }
                    }
                }.padding(16); Divider()
            }
        } }
    }
    private var automationPage: some View {
        VStack(alignment:.leading,spacing:20) {
            Text("课程自动化").font(.custom("Songti SC",size:30))
            HStack {
                Text("App 打开且学校已登录时，上课次日起每 2 小时检查新回放。").foregroundStyle(.secondary)
                Spacer()
                Button("同步课表") { probe.checkSchedule() }.disabled(!probe.signedIn || probe.working)
            }
            if !probe.signedIn { Text("学校尚未登录，保存设置后需登录才能自动获取材料。")
                .font(.system(size:14)).padding(12).frame(maxWidth:.infinity,alignment:.leading).background(Color.yellow.opacity(0.12)).clipShape(RoundedRectangle(cornerRadius:6)) }
            AutomationList(workspace: probe.workspaceRevision, courses: probe.courses,
                           savedCourses: savedCourses, rules: savedAutomations,
                           save: { probe.saveAutomation(changes:$0, workspace:$1) }, draft: $automationDraft)
                .frame(maxWidth:.infinity,maxHeight:.infinity)
        }.padding(32).frame(maxWidth:1164,maxHeight:.infinity,alignment:.topLeading)
            .frame(maxWidth:.infinity,maxHeight:.infinity,alignment:.top)
    }
    private var settingsPage: some View {
        pageShell("设置") {
            panel {
                HStack { Text("学习模型").font(.headline); Spacer(); Text(probe.modelService == nil ? "待配置" : "已配置").font(.system(size:14)).foregroundStyle(.secondary) }.padding(16); Divider()
                VStack(alignment:.leading,spacing:14) {
                    Text("服务地址"); TextField("https://你的模型服务地址/v1", text:$baseURL).textFieldStyle(.roundedBorder)
                    Text("模型名称"); TextField("服务商提供的模型 ID", text:$modelName).textFieldStyle(.roundedBorder)
                    Text("API Key"); SecureField(probe.modelHasKey ? "已保存，地址不变时留空保留" : "本地无鉴权模型可留空", text:$modelKey).textFieldStyle(.roundedBorder)
                    if !probe.modelIssue.isEmpty { Text(probe.modelIssue).foregroundStyle(.orange).font(.system(size:14)) }
                    Text("密钥保存在 macOS 钥匙串，更换服务地址时请重新填写。分析会把相关课堂文本发送到该服务，服务商可能收取费用。").font(.system(size:14)).foregroundStyle(.secondary)
                    HStack {
                        Button("保存配置") {
                            do { try probe.configureModel(baseURL:baseURL, model:modelName, key:modelKey); modelKey = ""; feedback = "模型配置已保存。" }
                            catch { feedback = error.localizedDescription }
                        }
                        if probe.modelHasKey {
                            Button("移除已保存密钥") {
                                do { try probe.removeModelKey(); feedback = "模型密钥已移除。" }
                                catch { feedback = error.localizedDescription }
                            }
                        }
                    }
                }.padding(16)
            }.frame(maxWidth:650,alignment:.leading)
            panel {
                HStack {
                    Text("应用更新").font(.headline)
                    Spacer()
                    Text("当前版本 \(desktopSettings.version)").foregroundStyle(.secondary)
                }.padding(16)
                Divider()
                VStack(alignment:.leading, spacing:10) {
                    if let release = desktopSettings.release,
                       [.available, .downloading, .ready].contains(desktopSettings.phase) {
                        Text("\(release.version) · \(Int(ceil(Double(release.size) / 1_048_576))) MB")
                        if let notes = release.notes, !notes.isEmpty { Text(String(notes.prefix(1200))) }
                    }
                    if !desktopSettings.message.isEmpty { Text(desktopSettings.message).foregroundStyle(.orange) }
                    switch desktopSettings.phase {
                    case .idle, .current:
                        if desktopSettings.phase == .current { Text("当前没有可用更新").foregroundStyle(.secondary) }
                        Button("检查更新") { desktopSettings.check() }
                    case .checking:
                        ProgressView("正在检查更新…")
                    case .available:
                        Button("下载更新") { desktopSettings.download() }
                    case .downloading:
                        ProgressView("正在下载更新…")
                        Button("取消下载") { desktopSettings.cancelDownload() }
                    case .ready:
                        Text("安装前请退出 BUAA Learn。").foregroundStyle(.secondary)
                        Button("打开安装包") { desktopSettings.openInstaller() }
                    }
                }.frame(maxWidth:.infinity, alignment:.leading).padding(16)
            }.frame(maxWidth:650,alignment:.leading)
            panel {
                Text("本地数据与诊断").font(.headline).frame(maxWidth:.infinity, alignment:.leading).padding(16)
                Divider()
                HStack {
                    Button("打开数据目录") { openLocalFile { try NativeStore.applicationDirectory() } }
                    Button("打开诊断日志") { openLocalFile { try DesktopDiagnostics.file() } }
                }.frame(maxWidth:.infinity, alignment:.leading).padding(16)
            }.frame(maxWidth:650,alignment:.leading)
        }
    }

    private func openLocalFile(_ url: () throws -> URL) {
        do {
            if try !NSWorkspace.shared.open(url()) { feedback = "无法打开本地文件。" }
        } catch { feedback = "无法打开本地文件。" }
    }

    private var learningRecordsPage: some View {
        pageShell("个人学习空间") {
            HStack(spacing:28) {
                Text("\(savedAnalyses.count) 份笔记")
                Text("\(savedMaterials.count) 节课堂材料")
                Text("\(tasks.filter { $0["done"] as? Bool == true }.count) 项作业已完成")
            }.foregroundStyle(.secondary)
            let records = savedLessons.filter { lesson in
                let id = lesson["id"] as? String ?? ""
                return savedAnalyses.contains { $0["lesson_id"] as? String == id } || savedMaterials.contains { $0["lesson_id"] as? String == id }
            }.sorted { ($0["date"] as? String ?? "") > ($1["date"] as? String ?? "") }
            if records.isEmpty { Text("还没有保存的学习记录。").foregroundStyle(.secondary) }
            panel {
                ForEach(records.indices,id:\.self) { index in
                    let row = records[index]
                    let id = row["id"] as? String ?? ""
                    let analyzed = savedAnalyses.contains { $0["lesson_id"] as? String == id }
                    HStack(alignment:.center,spacing:16) {
                        VStack(alignment:.leading,spacing:6) {
                            Text(row["course_name"] as? String ?? "课程").font(.headline)
                            Text(row["title"] as? String ?? "课堂").fixedSize(horizontal:false,vertical:true)
                            Text(String((row["date"] as? String ?? "").prefix(10))).font(.system(size:13)).foregroundStyle(.secondary)
                        }.frame(maxWidth:.infinity,alignment:.leading)
                        lessonStatusBadge(analyzed ? "已分析" : "材料已获取")
                        if let course = probe.courses.first(where:{ $0.id == row["course_id"] as? String }) {
                            Button(analyzed ? "查看笔记" : "查看材料") {
                                openCourse(course,from:"个人学习空间")
                                selectedLesson = id
                                if analyzed { notesOpen = true } else { transcriptOpen = true }
                            }.buttonStyle(LessonActionStyle())
                        }
                    }.padding(16)
                    if index + 1 < records.count { Divider() }
                }
            }
        }
    }

    private var courseList: some View {
        ScrollView {
        VStack(alignment:.leading,spacing:20) {
            HStack { Text("本学期课程").font(.custom("Songti SC",size:30)); Spacer(); Button(probe.working ? "同步中…" : probe.courses.isEmpty ? "同步本学期课程" : "同步最新回放") { Task { if probe.courses.isEmpty { await probe.loadCourses() } else { await probe.loadLatestRecordings() } } }.disabled(!probe.signedIn || probe.working) }
            if probe.courses.isEmpty {
                VStack(spacing:12) { Text(probe.signedIn ? "已登录，开始同步课程" : "连接你的北航课程").font(.title2); Button(probe.signedIn ? "同步本学期课程" : "使用北航 SSO 登录") { if probe.signedIn { Task { await probe.loadCourses() } } else { login() } } }.frame(maxWidth:.infinity,maxHeight:.infinity)
            } else {
                Text("\(probe.courses.count) 门课程").foregroundStyle(.secondary)
                HStack(alignment:.top,spacing:20) {
                VStack(alignment:.leading,spacing:0) {
                    Text("我的课程").font(.headline).padding(12)
                    HStack { Text("课程").frame(maxWidth:.infinity,alignment:.leading); Text("授课教师").frame(width:110,alignment:.leading); Text("课堂材料").frame(width:110,alignment:.leading); Text("").frame(width:80) }.font(.system(size:14)).foregroundStyle(.secondary).padding(12)
                    ForEach(probe.courses.sorted { left, right in
                        let a = savedCourses.first(where: { $0["id"] as? String == left.id })?["recording_count"] as? Int ?? 0
                        let b = savedCourses.first(where: { $0["id"] as? String == right.id })?["recording_count"] as? Int ?? 0
                        return a == b ? left.code.localizedStandardCompare(right.code) == .orderedAscending : a > b
                    }) { course in
                        Divider(); HStack {
                            Button { openCourse(course) } label: { VStack(alignment:.leading) { Text(course.name); Text(course.code).font(.system(size:14)).foregroundStyle(.secondary) }.frame(maxWidth:.infinity,alignment:.leading).contentShape(Rectangle()).modifier(HoverFeedback()) }.buttonStyle(.plain)
                            Text(course.teachers).lineLimit(2).frame(width:110,alignment:.leading)
                            courseStatus(course).frame(width:110,alignment:.leading)
                            Button("进入课程") { openCourse(course) }.frame(width:80)
                        }.padding(12)
                    }
                }.overlay(RoundedRectangle(cornerRadius:8).stroke(Color.black.opacity(0.1)))
                }
            }
        }.padding(32).frame(maxWidth:.infinity,alignment:.topLeading)
        }.frame(maxWidth:.infinity,maxHeight:.infinity)
    }
    private func openCourse(_ course: Course, from source: String = "本学期课程", date: String? = nil) {
        courseReturnPage = source
        detailCourse = course
        selectedLesson = date.flatMap { date in savedLessons.first { $0["course_id"] as? String == course.id && ($0["date"] as? String ?? "").hasPrefix(date) }?["id"] as? String }
        page = "课程详情"
        refreshRecords()
    }
    private func lessonStatusBadge(_ status: String) -> some View {
        let completed = status == "已分析", acquired = status == "材料已获取"
        let color = completed ? Color(red:0.16,green:0.43,blue:0.30) : acquired ? blue : muted
        let symbol = completed ? "checkmark.circle.fill" : acquired ? "doc.text.fill" : status == "回放可用" ? "play.circle" : "clock"
        return Label(status,systemImage:symbol).font(.system(size:12,weight:completed ? .semibold : .regular))
            .foregroundStyle(color).padding(.horizontal,7).padding(.vertical,4)
            .background(Capsule().fill(completed || acquired ? color.opacity(0.10) : .clear))
    }
    @ViewBuilder private func courseStatus(_ course: Course) -> some View {
        switch probe.recordingSyncStates[course.id] {
        case .syncing: HStack(spacing:5) { ProgressView().controlSize(.small); Text("同步中…") }.font(.system(size:14))
        case .succeeded(let count): Text("\(count) 节回放").font(.system(size:14)).foregroundStyle(blue)
        case .failed(let message): Text("同步需重试：\(message)").font(.system(size:14)).foregroundStyle(.orange).lineLimit(2).help(message)
        case nil:
            if let row = savedCourses.first(where:{ $0["id"] as? String == course.id }), row["checked_at"] != nil {
                Text("\((row["recording_count"] as? Int) ?? 0) 节回放").font(.system(size:14)).foregroundStyle(blue)
            } else { Text("尚未同步课次").font(.system(size:14)).foregroundStyle(.secondary) }
        }
    }
    private var courseDetail: some View {
        Group {
            if let course = detailCourse {
                let lessons = savedLessons.filter { $0["course_id"] as? String == course.id }.sorted { left, right in
                    let a = left["has_recording"] as? Bool ?? false, b = right["has_recording"] as? Bool ?? false
                    if a != b { return a && !b }
                    let aDate = left["date"] as? String ?? "", bDate = right["date"] as? String ?? ""
                    return a ? aDate > bDate : aDate < bDate
                }
                pageShell(course.name) {
                    Button { page = courseReturnPage } label: { Label("返回\(courseReturnPage)", systemImage: "arrow.left") }.buttonStyle(LessonActionStyle())
                    HStack { Text("\(course.teachers) · \(course.code)").foregroundStyle(.secondary); Spacer(); Button("同步课次") { probe.checkRecording(course) }.buttonStyle(LessonActionStyle()).disabled(!probe.signedIn || probe.working); Button("设置自动化") { page = "课程自动化" }.buttonStyle(LessonActionStyle()) }
                    if case .syncing = probe.recordingSyncStates[course.id] { HStack { ProgressView().controlSize(.small); Text("正在同步课次…").foregroundStyle(.secondary) } }
                    if case .succeeded(let count) = probe.recordingSyncStates[course.id] { Text("已同步 \(count) 节回放。").foregroundStyle(.secondary).font(.system(size:14)) }
                    if case .failed(let message) = probe.recordingSyncStates[course.id] { Text(message).foregroundStyle(.orange).font(.system(size:14)) }
                    HStack(alignment:.top,spacing:22) {
                        panel {
                            HStack { Text("课程目录").font(.headline); Spacer(); Text("\(lessons.count) 节").font(.system(size:14)).foregroundStyle(.secondary) }.padding(16); Divider()
                            if lessons.isEmpty { Text("先同步课程目录，查看可学习的课次。").font(.system(size:14)).foregroundStyle(.secondary).padding(16) }
                            ForEach(lessons.indices,id:\.self) { index in
                                let lesson = lessons[index]
                                let lessonID = lesson["id"] as? String ?? ""
                                let status = savedAnalyses.contains(where: { $0["lesson_id"] as? String == lessonID }) ? "已分析" : savedMaterials.contains(where: { $0["lesson_id"] as? String == lessonID }) ? "材料已获取" : (lesson["has_recording"] as? Bool ?? false) ? "回放可用" : "尚无回放"
                                Button { selectedLesson = lessonID } label: {
                                    VStack(alignment:.leading,spacing:4) {
                                        Text(lesson["title"] as? String ?? "课次")
                                        if let date = lesson["date"] as? String, !date.isEmpty { Text(String(date.prefix(10))).font(.system(size:13)).foregroundStyle(.secondary) }
                                        lessonStatusBadge(status)
                                    }.frame(maxWidth:.infinity,alignment:.leading).padding(12)
                                        .background(selectedLesson == lessonID ? blue.opacity(0.09) : .clear).modifier(HoverFeedback())
                                }.buttonStyle(.plain); Divider()
                            }
                        }.frame(width:260)
                        panel {
                            if let selectedLesson, let lesson = lessons.first(where:{ $0["id"] as? String == selectedLesson }) {
                                let material = savedMaterials.first(where:{ $0["lesson_id"] as? String == selectedLesson })
                                let hasNotes = savedAnalyses.contains(where: { $0["lesson_id"] as? String == selectedLesson })
                                let hasText = !(material?["segments"] as? [Any] ?? []).isEmpty
                                let isAnalyzing = probe.analysisJob?.lessonID == selectedLesson && probe.analysisJob?.phase == .running
                                VStack(alignment:.leading,spacing:16) {
                                    HStack(alignment:.center,spacing:12) {
                                        Text(lesson["title"] as? String ?? "课次").font(.headline).frame(maxWidth:.infinity,alignment:.leading)
                                        if hasNotes {
                                            Button("查看学习笔记") { notesOpen = true }.buttonStyle(LessonActionStyle(primary:true))
                                        } else if hasText {
                                            Button(isAnalyzing ? "正在分析…" : "分析这节课") { startAnalysis(selectedLesson) }
                                                .buttonStyle(LessonActionStyle(primary:true)).disabled(probe.working)
                                        }
                                    }
                                    if let material {
                                        Text("课堂文本 \((material["segments"] as? [Any])?.count ?? 0) 段 · PPT 截图 \((material["slides"] as? [Any])?.count ?? 0) 张")
                                            .font(.system(size:14)).foregroundStyle(.secondary)
                                    }
                                    LessonActionFlow(spacing:8) {
                                        if material != nil {
                                            Button("查看课堂原文") { transcriptOpen = true }.buttonStyle(LessonActionStyle())
                                            Button("PPT 截图") { slidesOpen = true }.buttonStyle(LessonActionStyle())
                                        } else {
                                            Button("获取课堂材料") {
                                                Task { do { try await probe.acquireMaterials(selectedLesson) } catch { feedback = error.localizedDescription } }
                                            }.buttonStyle(LessonActionStyle(primary:true)).disabled(!probe.signedIn || !(lesson["has_recording"] as? Bool ?? false) || probe.working)
                                        }
                                        if let source = lesson["source_url"] as? String, let url = URL(string:source), schoolHost(url) {
                                            Button("原始回放") { NSWorkspace.shared.open(url) }.buttonStyle(LessonActionStyle())
                                        }
                                        if hasNotes, let analysis = savedAnalyses.first(where: { $0["lesson_id"] as? String == selectedLesson }) {
                                            Button("导出笔记") { exportNotes(lesson:lesson, analysis:analysis) }.buttonStyle(LessonActionStyle())
                                        }
                                    }
                                    if let material {
                                    if let coverage = material["coverage"] as? [String:Any] {
                                        DisclosureGroup("材料覆盖情况") {
                                            if let ratio = coverage["ratio"] as? NSNumber, ratio.doubleValue.isFinite, (0...1).contains(ratio.doubleValue) {
                                                Text("识别文本时间覆盖：\(Int((ratio.doubleValue * 100).rounded()))%")
                                            } else { Text("识别文本时间覆盖：未知") }
                                            if let note = coverage["note"] as? String, !note.isEmpty { Text(note) }
                                            Text("自动分析未核听原声，请结合原始回放核对。")
                                        }.font(.system(size:14)).foregroundStyle(.secondary)
                                    }
                                    if (material["segments"] as? [Any] ?? []).isEmpty {
                                        Text("暂无课堂识别文本，无法生成笔记。").font(.system(size:14)).foregroundStyle(.orange)
                                    }
                                    }
                                    if let message = probe.materialFeedback[selectedLesson] { Text(message).font(.system(size:14)).foregroundStyle(.secondary) }
                                    if isAnalyzing, let job = probe.analysisJob {
                                        HStack { ProgressView().controlSize(.small); Text(job.message); Button("查看任务") { page = "任务状态" } }
                                            .font(.system(size:14))
                                    }
                                }.padding(20)
                            } else { Text(lessons.isEmpty ? "还没有同步课堂目录" : "选择一节课开始").foregroundStyle(.secondary).padding(16) }
                        }.frame(maxWidth:.infinity)
                    }
                }
            }
        }.sheet(isPresented: $transcriptOpen) {
            if let selectedLesson, let material = savedMaterials.first(where: { $0["lesson_id"] as? String == selectedLesson }) {
                MaterialTranscriptView(material: material) { transcriptOpen = false }
            }
        }.sheet(isPresented: $notesOpen) {
            if let selectedLesson, let analysis = savedAnalyses.first(where: { $0["lesson_id"] as? String == selectedLesson }) {
                VStack(spacing:0) {
                    HStack {
                        Text(analysis["lesson_title"] as? String ?? "学习笔记").font(.headline); Spacer()
                        if let lesson = savedLessons.first(where: { $0["id"] as? String == selectedLesson }) {
                            Button("导出笔记") { exportNotes(lesson:lesson, analysis:analysis) }
                        }
                        Button("关闭") { notesOpen = false }
                    }.padding(20)
                    Divider()
                    ScrollView {
                        VStack(alignment:.leading,spacing:0) {
                            if let material = savedMaterials.first(where: { $0["lesson_id"] as? String == selectedLesson }),
                               let currentDigest = material["digest"] as? String, let savedDigest = analysis["digest"] as? String, currentDigest != savedDigest {
                                Text("课堂材料已更新；当前笔记和原文来源仍基于之前的材料。")
                                    .foregroundStyle(.orange).padding(16)
                            }
                            LessonTimeline(analysis: analysis)
                        }.frame(maxWidth:.infinity,alignment:.leading)
                    }
                }.readerSheetFrame(width:900,height:680)
            }
        }.sheet(isPresented:$slidesOpen) {
            if let selectedLesson, let material = savedMaterials.first(where: { $0["lesson_id"] as? String == selectedLesson }) {
                let slides = material["slides"] as? [[String:Any]] ?? []
                MaterialSlidesView(slides:slides, close:{ slidesOpen = false }, export:{ exportSlides(slides, lessonID:selectedLesson) }, exporting:exportingSlides, exportMessage:slideExportLessonID == selectedLesson ? slideExportMessage : "", load:probe.slideLoader())
            }
        }
    }
    private func startAnalysis(_ lessonID: String) {
        Task { do { try await probe.analyzeLesson(lessonID) } catch { feedback = error.localizedDescription } }
    }
    private func exportNotes(lesson: [String:Any], analysis: [String:Any]) {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [UTType(filenameExtension:"md") ?? .plainText]
        panel.nameFieldStringValue = "课堂笔记.md"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do { try LessonNoteExport.markdown(lesson:lesson, analysis:analysis).write(to:url, atomically:true, encoding:.utf8) }
        catch { feedback = "导出笔记失败：\(error.localizedDescription)" }
    }
    private func exportSlides(_ slides: [[String:Any]], lessonID: String) {
        do {
            let rows = try slides.map(SlideInput.init)
            let panel = NSSavePanel(); panel.allowedContentTypes = [.pdf]; panel.nameFieldStringValue = "课堂去重 PPT.pdf"
            guard panel.runModal() == .OK, let url = panel.url else { return }
            exportingSlides = true; slideExportLessonID = lessonID; slideExportMessage = "正在准备导出…"
            let load = probe.slideLoader()
            Task {
                do {
                    let result = try await Task.detached {
                        try await SlideExporter.export(rows:rows, destination:url, progress: { message in
                            Task { @MainActor in if exportingSlides && slideExportLessonID == lessonID { slideExportMessage = message } }
                        }, fetch:{ try await load($0) })
                    }.value
                    slideExportMessage = "已导出 \(result.1) 页，合并 \(result.0 - result.1) 张重复截图。"
                } catch { slideExportMessage = error.localizedDescription }
                exportingSlides = false
            }
        } catch { feedback = error.localizedDescription }
    }
}

private struct LessonActionStyle: ButtonStyle {
    var primary = false
    func makeBody(configuration: Configuration) -> some View {
        LessonActionBody(configuration: configuration, primary: primary)
    }
    private struct LessonActionBody: View {
        let configuration: Configuration
        let primary: Bool
        @Environment(\.isEnabled) private var enabled
        @State private var hovering = false
        var body: some View {
            configuration.label
                .font(.system(size:14,weight:.medium))
                .padding(.horizontal,14).frame(minHeight:36)
                .foregroundStyle(primary ? Color.white : Color(red:0.16,green:0.20,blue:0.24))
                .background(primary ? Color(red:0.23,green:0.38,blue:0.51) : (hovering ? Color.black.opacity(0.045) : Color.white))
                .clipShape(RoundedRectangle(cornerRadius:7))
                .overlay(RoundedRectangle(cornerRadius:7).stroke(primary ? Color.clear : Color.black.opacity(0.13)))
                .opacity(enabled ? (configuration.isPressed ? 0.72 : 1) : 0.4)
                .onHover { hovering = $0 }
        }
    }
}

private struct HoverFeedback: ViewModifier {
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var hovering = false
    var width: CGFloat? = nil
    var cornerRadius: CGFloat = 6
    func body(content: Content) -> some View {
        content.contentShape(Rectangle())
            .background(alignment:.leading) {
                RoundedRectangle(cornerRadius:cornerRadius)
                    .fill(hovering ? Color.black.opacity(0.055) : .clear)
                    .frame(width:width)
            }
            .onHover { inside in
                withAnimation(reduceMotion ? nil : .easeOut(duration:0.12)) { hovering = inside }
            }
    }
}
