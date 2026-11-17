import SwiftUI
import WebKit

/// Isolated local React island, sharing the project's Base UI Combobox, Button and Sheet.
struct ScheduleTimetable: NSViewRepresentable {
    let workspace: Int
    let rows: [[String:Any]]?
    let fullSemester: Bool
    let semester: TimetableSemester?
    @Binding var selectedWeek: Int?
    @Binding var weekdaysOnly: Bool
    let courses: [Course]
    let syncing: Bool
    let canSync: Bool
    let sync: () -> Void
    let open: (Course,String) -> Void

    private var number: Int { min(semester?.weeks ?? 1,max(1,selectedWeek ?? semester?.currentWeek() ?? 1)) }
    private var week: TimetableWeek { TimetableWeek(rows:rows ?? [],courses:courses,now:semester?.date(week:number) ?? Date()) }
    func makeCoordinator() -> Coordinator { Coordinator(self) }
    func makeNSView(context: Context) -> WKWebView {
        let config = WKWebViewConfiguration()
        config.websiteDataStore = .nonPersistent()
        config.userContentController.add(context.coordinator,name:"timetableReady")
        config.userContentController.add(context.coordinator,name:"timetableAction")
        let view = WKWebView(frame:.zero,configuration:config)
        view.setValue(false,forKey:"drawsBackground")
        view.navigationDelegate = context.coordinator
        context.coordinator.webView = view
        if let url = Bundle.main.resourceURL?.appendingPathComponent("timetable.html") {
            view.loadFileURL(url,allowingReadAccessTo:url.deletingLastPathComponent())
        }
        return view
    }
    func updateNSView(_ view: WKWebView,context: Context) {
        context.coordinator.parent = self
        context.coordinator.renderIfNeeded()
    }
    static func dismantleNSView(_ view: WKWebView,coordinator: Coordinator) {
        view.configuration.userContentController.removeScriptMessageHandler(forName:"timetableReady")
        view.configuration.userContentController.removeScriptMessageHandler(forName:"timetableAction")
        view.navigationDelegate = nil
        view.stopLoading()
    }
    func renderPayload() -> [String:Any] {
        let week = week
        let days = week.days.map(TimetableWeek.dateKey)
        let placements = days.flatMap { date in TimetablePlacement.layout(week.entries.filter { $0.date == date }) }
        func entry(_ row: TimetableEntry,lane: Int = 0,lanes: Int = 1) -> [String:Any] {
            ["id":row.id,"date":row.date,"name":row.name,"room":row.room,"time":row.time,
             "start":row.start.map { $0 as Any } ?? NSNull(),"end":row.end.map { $0 as Any } ?? NSNull(),
             "courseID":row.course?.id ?? "","teachers":row.course?.teachers ?? "","code":row.course?.code ?? "",
             "lane":lane,"lanes":lanes]
        }
        let entries = placements.map { entry($0.entry,lane:$0.lane,lanes:$0.lanes) } + week.entries.filter { $0.start == nil }.map { entry($0) }
        let formatter = DateFormatter(); formatter.calendar = TimetableWeek.calendar
        formatter.timeZone = TimetableWeek.calendar.timeZone; formatter.locale = Locale(identifier:"zh_CN")
        formatter.dateFormat = "M月d日"
        let options: [[String:Any]] = (1...(semester?.weeks ?? 1)).map { number in
            let start = semester?.date(week:number) ?? week.days[0]
            let end = TimetableWeek.calendar.date(byAdding:.day,value:6,to:start)!
            return ["value":String(number),"label":"第\(number)周","description":"\(formatter.string(from:start)) – \(formatter.string(from:end))"]
        }
        return ["workspace":workspace,"courseKeys":courses.map { $0.code.isEmpty ? $0.id : $0.code },"semester":semester?.id ?? "","weeks":semester?.weeks ?? 1,
                "week":number,"currentWeek":semester?.currentWeek().map { $0 as Any } ?? NSNull(),
                "dates":days,"weekOptions":options,"entries":entries,
                "firstHour":min(8,(week.entries.compactMap(\.start).min() ?? 480) / 60),
                "lastHour":max(22,((week.entries.compactMap(\.end).max() ?? 1320) + 59) / 60),
                "fullSemester":fullSemester,"canSync":canSync && semester != nil,"syncing":syncing,"weekdaysOnly":weekdaysOnly]
    }
    final class Coordinator: NSObject,WKScriptMessageHandler,WKNavigationDelegate {
        var parent: ScheduleTimetable
        weak var webView: WKWebView?
        var ready = false
        var rendered: Data?
        init(_ parent: ScheduleTimetable) { self.parent = parent }
        func webView(_ webView: WKWebView,decidePolicyFor action: WKNavigationAction,decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
            let url = action.request.url
            decisionHandler(url?.isFileURL == true && url?.lastPathComponent == "timetable.html" ? .allow : .cancel)
        }
        func userContentController(_ userContentController: WKUserContentController,didReceive message: WKScriptMessage) {
            guard message.frameInfo.isMainFrame,message.frameInfo.request.url?.isFileURL == true,
                  message.frameInfo.request.url?.lastPathComponent == "timetable.html" else { return }
            if message.name == "timetableReady" { ready = true; renderIfNeeded(); return }
            guard let body = message.body as? [String:Any],body["workspace"] as? Int == parent.workspace else { return }
            switch body["action"] as? String {
            case "week":
                guard let number = body["value"] as? Int,(1...(parent.semester?.weeks ?? 1)).contains(number) else { return }
                parent.selectedWeek = number
            case "view":
                guard let value = body["value"] as? Bool else { return }
                parent.weekdaysOnly = value
            case "sync":
                guard parent.canSync,!parent.syncing else { return }
                parent.sync()
            case "open":
                guard let value = body["value"] as? [String:Any],let id = value["id"] as? String,
                      let date = value["date"] as? String,parent.week.days.map(TimetableWeek.dateKey).contains(date),
                      let course = parent.courses.first(where: { $0.id == id }),
                      parent.week.entries.contains(where: { $0.date == date && $0.course?.id == id }) else { return }
                parent.open(course,date)
            default: return
            }
            // Binding changes need an immediate render even when SwiftUI keeps this representable.
            renderIfNeeded()
        }
        func renderIfNeeded() {
            guard ready,let webView,
                  let data = try? JSONSerialization.data(withJSONObject:parent.renderPayload(),options:[.sortedKeys]),
                  data != rendered,let json = String(data:data,encoding:.utf8) else { return }
            rendered = data
            webView.evaluateJavaScript("window.renderTimetable(\(json))") { _,error in
                if error != nil { self.rendered = nil }
            }
        }
    }
}
