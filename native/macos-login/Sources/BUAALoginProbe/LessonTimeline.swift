import SwiftUI

struct TimelineTranscriptRow: Identifiable {
    let id: String
    let start: Double
    let end: Double
    let text: String
}

/// Always resolve against the immutable evidence saved with the analysis, not refreshed materials.
struct TimelineEvidence {
    let rows: [TimelineTranscriptRow]

    init(analysis: [String:Any]) {
        let evidence = analysis["evidence"] as? [String:Any] ?? [:]
        rows = Self.parse(evidence)
    }

    init(material: [String:Any]) {
        let segments = material["segments"] as? [[String:Any]] ?? []
        var evidence: [String:Any] = [:]
        for segment in segments {
            if let id = segment["id"] as? String { evidence[id] = segment }
        }
        rows = Self.parse(evidence)
    }

    private static func parse(_ evidence: [String:Any]) -> [TimelineTranscriptRow] {
        evidence.compactMap { key, value -> TimelineTranscriptRow? in
            guard let item = value as? [String:Any],
                  let start = (item["start"] as? NSNumber)?.doubleValue,
                  let end = (item["end"] as? NSNumber)?.doubleValue,
                  start.isFinite, end.isFinite, start >= 0, end >= start,
                  let text = item["text"] as? String else { return nil }
            return TimelineTranscriptRow(id: key, start: start, end: end, text: text)
        }.sorted {
            if $0.start != $1.start { return $0.start < $1.start }
            if $0.end != $1.end { return $0.end < $1.end }
            return $0.id.localizedStandardCompare($1.id) == .orderedAscending
        }
    }

    func range(startID: String, endID: String) -> [TimelineTranscriptRow] {
        guard let start = rows.firstIndex(where: { $0.id == startID }),
              let end = rows.firstIndex(where: { $0.id == endID }), start <= end else { return [] }
        return Array(rows[start...end])
    }

    func range(for finding: [String:Any]) -> [TimelineTranscriptRow] {
        if let source = finding["source_range"] as? [String:Any],
           let start = source["start_id"] as? String, let end = source["end_id"] as? String {
            return range(startID: start, endID: end)
        }
        let ids = Set(finding["evidence_ids"] as? [String] ?? [])
        let refs = rows.filter { ids.contains($0.id) }
        guard let first = refs.first, let last = refs.last else { return [] }
        return range(startID: first.id, endID: last.id)
    }
}

private struct TimelineSource: Identifiable {
    let id = UUID()
    let rows: [TimelineTranscriptRow]
}

func lessonClock(_ seconds: Double) -> String {
    let value = seconds.isFinite ? max(0, Int(min(seconds, Double(Int.max / 2)))) : 0
    return String(format: "%02d:%02d:%02d", value / 3600, (value % 3600) / 60, value % 60)
}

struct MaterialTranscriptView: View {
    let material: [String:Any]
    let close: () -> Void
    @State private var search = ""
    private var rows: [TimelineTranscriptRow] {
        let all = TimelineEvidence(material: material).rows
        return search.isEmpty ? all : all.filter { $0.text.localizedCaseInsensitiveContains(search) }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                Text("课堂原文").font(.headline)
                Spacer()
                TextField("查找课堂原话", text: $search).textFieldStyle(.roundedBorder).frame(width: 230)
                Button("关闭", action: close)
            }.padding(20)
            Divider()
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 0) {
                    if rows.isEmpty { Text(search.isEmpty ? "这节课暂无识别文本。" : "没有找到匹配的原文。").foregroundStyle(.secondary).padding(.vertical, 16) }
                    ForEach(rows) { row in
                        HStack(alignment: .top, spacing: 16) {
                            Text(lessonClock(row.start)).font(.system(size:14).monospacedDigit()).foregroundStyle(.secondary).frame(width: 75, alignment: .leading)
                            Text(row.text).fixedSize(horizontal:false, vertical:true).textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading)
                        }.padding(.vertical, 10)
                        Divider()
                    }
                }.padding(20)
            }
        }.readerSheetFrame(width:720,height:520)
    }
}

struct LessonTimeline: View {
    let analysis: [String:Any]
    @State private var source: TimelineSource?
    private var evidence: TimelineEvidence { TimelineEvidence(analysis: analysis) }
    private var sections: [[String:Any]] { analysis["sections"] as? [[String:Any]] ?? [] }

    var body: some View {
        LazyVStack(alignment: .leading, spacing: 20) {
            if analysis["timeline_status"] as? String == "partial" {
                Text("部分时间段尚未整理完成。")
                    .font(.system(size:14)).foregroundStyle(.secondary)
                    .fixedSize(horizontal:false, vertical:true)
            }
            ForEach(sections.indices, id: \.self) { index in
                let section = sections[index]
                let isGap = (section["kind"] as? String) == "gap"
                VStack(alignment:.leading, spacing:12) {
                    HStack(alignment:.center) {
                        Label("\(lessonClock((section["start"] as? NSNumber)?.doubleValue ?? 0)) – \(lessonClock((section["end"] as? NSNumber)?.doubleValue ?? 0))",
                              systemImage:isGap ? "exclamationmark.circle" : "book")
                            .font(.system(size:13).monospacedDigit()).foregroundStyle(.secondary)
                        Spacer(minLength:12)
                        if let start = section["start_id"] as? String, let end = section["end_id"] as? String {
                            sourceButton(evidence.range(startID:start, endID:end), compact:true)
                        }
                    }
                    Text(section["title"] as? String ?? "课堂内容")
                        .font(.system(size:isGap ? 15 : 18, weight:.semibold))
                        .fixedSize(horizontal:false, vertical:true)
                    Text(section["summary"] as? String ?? "")
                        .font(.system(size:isGap ? 14 : 15))
                        .foregroundStyle(isGap ? Color.secondary : Color.primary)
                        .lineSpacing(6).textSelection(.enabled)
                        .fixedSize(horizontal:false, vertical:true)
                        .frame(maxWidth:.infinity, alignment:.leading)
                    findings(section)
                }
                .frame(maxWidth:.infinity, alignment:.leading)
                .padding(22)
                .background(isGap ? Color.primary.opacity(0.025) : Color(NSColor.controlBackgroundColor))
                .clipShape(RoundedRectangle(cornerRadius:12))
                .overlay(RoundedRectangle(cornerRadius:12).strokeBorder(Color.primary.opacity(isGap ? 0 : 0.09)))
            }
        }.frame(maxWidth:.infinity, alignment:.leading).padding(24)
        .sheet(item: $source) { selected in
            VStack(alignment: .leading, spacing: 0) {
                HStack { Text("课堂原文").font(.headline); Spacer(); Button("关闭") { source = nil } }.padding(20)
                Divider()
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 0) {
                        ForEach(selected.rows) { row in
                            HStack(alignment: .top, spacing: 16) {
                                Text(lessonClock(row.start)).font(.system(size:14).monospacedDigit()).foregroundStyle(.secondary).frame(width: 75, alignment: .leading)
                                Text(row.text).fixedSize(horizontal:false, vertical:true).textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading)
                            }.padding(.vertical, 10)
                            Divider()
                        }
                    }.padding(20)
                }
            }.readerSheetFrame(width:720,height:520)
        }
    }

    @ViewBuilder private func sourceButton(_ rows: [TimelineTranscriptRow], compact: Bool = false) -> some View {
        if let first = rows.first, let last = rows.last {
            Button(compact ? "查看原文" : "\(lessonClock(first.start))–\(lessonClock(last.end)) 查看原文") { source = TimelineSource(rows: rows) }
                .buttonStyle(.borderless).font(.system(size:13)).foregroundStyle(Color.accentColor)
                .accessibilityLabel("查看 \(lessonClock(first.start)) 至 \(lessonClock(last.end)) 的课堂原文")
        } else {
            Text("原文来源不可用").font(.system(size:14)).foregroundStyle(.secondary)
        }
    }

    @ViewBuilder private func findings(_ section: [String:Any]) -> some View {
        let points = section["knowledge_points"] as? [[String:Any]] ?? []
        let assignments = section["assignments"] as? [[String:Any]] ?? []
        let notices = section["notices"] as? [[String:Any]] ?? []
        if !points.isEmpty || !assignments.isEmpty || !notices.isEmpty {
            DisclosureGroup([(points.isEmpty ? nil : "知识点 \(points.count)"), (assignments.isEmpty ? nil : "作业 \(assignments.count)"), (notices.isEmpty ? nil : "事项 \(notices.count)")].compactMap { $0 }.joined(separator:" · ")) {
                VStack(alignment:.leading, spacing:8) {
                    let showHeadings = [points, assignments, notices].filter { !$0.isEmpty }.count > 1
                    findingRows("知识点", points, assignment:false, showTitle:showHeadings)
                    findingRows("作业", assignments, assignment:true, showTitle:showHeadings)
                    findingRows("事项", notices, assignment:false, showTitle:showHeadings)
                }.frame(maxWidth:.infinity, alignment:.leading).padding(.top,8)
            }.font(.system(size:14)).padding(.vertical, 6)
        }
    }

    @ViewBuilder private func findingRows(_ title: String, _ rows: [[String:Any]], assignment: Bool, showTitle: Bool = true) -> some View {
        if !rows.isEmpty {
            if showTitle { Text(title).font(.system(size:14,weight:.semibold)).padding(.top,8) }
            ForEach(rows.indices, id: \.self) { index in
                let row = rows[index]
                VStack(alignment: .leading, spacing: 6) {
                    Text(row["text"] as? String ?? "").lineSpacing(5).textSelection(.enabled).fixedSize(horizontal:false, vertical:true)
                    if assignment {
                        Text("待核验 · 截止：\(row["deadline"] as? String ?? "未说明") · 对象：\(row["audience"] as? String ?? "未说明")\n提交：\(row["submission"] as? String ?? "未说明")").foregroundStyle(.secondary)
                    }
                    sourceButton(evidence.range(for: row))
                }.frame(maxWidth:.infinity, alignment:.leading).padding(.vertical,8)
            }
        }
    }
}
