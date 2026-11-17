import AppKit
import ImageIO
import SwiftUI

enum LessonNoteExport {
    static func markdown(lesson: [String:Any], analysis: [String:Any]) -> String {
        let title = lesson["title"] as? String ?? "课堂笔记"
        let course = lesson["course_name"] as? String ?? analysis["course_name"] as? String ?? ""
        var lines = ["# \(course.isEmpty ? title : "\(course) · \(title)")", ""]
        if let raw = lesson["source_url"] as? String, let url = URL(string:raw), schoolHost(url) {
            lines += [url.absoluteString, ""]
        }
        lines += ["自动识别与模型分析可能错误；作业和重要事项需核对原话。", ""]
        if analysis["timeline_status"] as? String == "partial" {
            lines += ["部分时间段尚未整理完成。", ""]
        }
        let sections = analysis["sections"] as? [[String:Any]] ?? []
        if sections.isEmpty { lines += ["这份笔记尚无课堂时间轴。", ""] }
        for section in sections {
            let start = (section["start"] as? NSNumber)?.doubleValue ?? 0
            let end = (section["end"] as? NSNumber)?.doubleValue ?? 0
            let sectionTitle = section["title"] as? String ?? "课堂内容"
            lines += ["## \(lessonClock(start))–\(lessonClock(end)) · \(sectionTitle)", "", section["summary"] as? String ?? "", ""]
            for (key, label) in [("knowledge_points", "知识点"), ("assignments", "作业（待核验）"), ("notices", "事项")] {
                let rows = section[key] as? [[String:Any]] ?? []
                if !rows.isEmpty {
                    lines += ["**\(label)**", ""]
                    for row in rows {
                        lines.append("- \(row["text"] as? String ?? "")")
                        if key == "assignments" {
                            lines.append("  截止：\(row["deadline"] as? String ?? "未说明")；对象：\(row["audience"] as? String ?? "未说明")；提交：\(row["submission"] as? String ?? "未说明")")
                        }
                    }
                    lines.append("")
                }
            }
        }
        return lines.joined(separator:"\n")
    }
}

struct MaterialSlidesView: View {
    let slides: [[String:Any]]
    let close: () -> Void
    let export: () -> Void
    let exporting: Bool
    let exportMessage: String
    let load: @MainActor (URL) async throws -> Data
    @State private var selected: SlidePreview?
    private let columns = [GridItem(.adaptive(minimum:200, maximum:320), spacing:16)]

    var body: some View {
        VStack(alignment:.leading, spacing:0) {
            HStack {
                Text("PPT 截图").font(.headline)
                Spacer()
                Button(exporting ? "正在导出…" : "导出去重 PDF", action:export).disabled(slides.isEmpty || exporting)
                Button("关闭", action:close)
            }.padding(20)
            Divider()
            if !exportMessage.isEmpty { Text(exportMessage).font(.system(size:13)).foregroundStyle(.secondary).padding(.horizontal,20).padding(.top,10) }
            ScrollView {
                if slides.isEmpty {
                    Text("暂无截图记录。").foregroundStyle(.secondary).frame(maxWidth:.infinity,alignment:.leading).padding(20)
                } else {
                    LazyVGrid(columns:columns, alignment:.leading, spacing:16) {
                        ForEach(slides.indices, id:\.self) { index in
                            SlideThumbnail(row:slides[index], load:load) { image in selected = SlidePreview(image:image) }
                        }
                    }.padding(20)
                }
            }
        }.readerSheetFrame(width:800,height:560)
            .sheet(item:$selected) { preview in
                VStack {
                    HStack { Spacer(); Button("关闭") { selected = nil } }.padding()
                    Image(nsImage:preview.image).resizable().aspectRatio(contentMode:.fit).frame(maxWidth:.infinity,maxHeight:.infinity).padding()
                }.readerSheetFrame(width:800,height:560)
            }
    }
}

extension View {
    /// Scrollable readers must have a finite viewport rather than grow to their contents.
    func readerSheetFrame(width: CGFloat, height: CGFloat) -> some View {
        let screen = NSScreen.main?.visibleFrame.size ?? CGSize(width:1024,height:768)
        return frame(width:min(width, max(440, screen.width - 120)),
                     height:min(height, max(320, screen.height - 160)))
    }
}

private struct SlidePreview: Identifiable {
    let id = UUID()
    let image: NSImage
}

private struct SlideThumbnail: View {
    let row: [String:Any]
    let load: @MainActor (URL) async throws -> Data
    let open: (NSImage) -> Void
    @State private var image: NSImage?
    @State private var failed = false
    @State private var attempt = 0
    var body: some View {
        VStack(alignment:.leading,spacing:6) {
            Group {
                if let image {
                    Button { open(image) } label: {
                        Image(nsImage:image).resizable().aspectRatio(contentMode:.fit).frame(maxWidth:.infinity,maxHeight:170)
                    }.buttonStyle(.plain).accessibilityLabel("查看 \(lessonClock((row["start"] as? NSNumber)?.doubleValue ?? 0)) 的 PPT 截图")
                } else if failed {
                    VStack { Text("截图加载失败"); Button("重试") { failed = false; attempt += 1 } }.foregroundStyle(.secondary).frame(maxWidth:.infinity,minHeight:120)
                } else {
                    ProgressView().frame(maxWidth:.infinity,minHeight:120)
                }
            }.background(Color.black.opacity(0.03)).clipShape(RoundedRectangle(cornerRadius:6))
            Text(lessonClock((row["start"] as? NSNumber)?.doubleValue ?? 0)).font(.system(size:13)).foregroundStyle(.secondary)
        }.onDisappear { image = nil }.task(id:attempt) {
            do {
                let input = try SlideInput(row)
                let data = try await load(input.url)
                guard let source = CGImageSourceCreateWithData(data as CFData, nil),
                      let type = CGImageSourceGetType(source) as String?, ["public.png", "public.jpeg", "org.webmproject.webp"].contains(type),
                      let props = CGImageSourceCopyPropertiesAtIndex(source,0,nil) as? [CFString:Any],
                      let width = props[kCGImagePropertyPixelWidth] as? Int, let height = props[kCGImagePropertyPixelHeight] as? Int,
                      width > 0, height > 0, width <= 20_000_000 / height,
                      let thumbnail = CGImageSourceCreateThumbnailAtIndex(source,0,[kCGImageSourceCreateThumbnailFromImageAlways:true,kCGImageSourceCreateThumbnailWithTransform:true,kCGImageSourceThumbnailMaxPixelSize:1600] as CFDictionary) else { throw ProbeError.message("截图格式或尺寸不支持。") }
                try Task.checkCancellation()
                image = NSImage(cgImage:thumbnail,size:.zero)
            } catch is CancellationError { }
              catch { failed = true }
        }
    }
}
