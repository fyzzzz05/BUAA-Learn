import Foundation
import AppKit
import ImageIO
import CryptoKit
import CoreText

struct SlideInput {
    let url: URL
    let time: Double
    init(_ row: [String:Any]) throws {
        guard let raw = row["url"] as? String, let components = URLComponents(string: raw),
              components.scheme?.lowercased() == "https", components.host?.lowercased() == "resource.msa.buaa.edu.cn",
              components.port == nil || components.port == 443, components.user == nil, components.password == nil,
              components.query == nil, components.fragment == nil, let url = components.url,
              let time = (row["start"] as? NSNumber)?.doubleValue, time.isFinite, time >= 0 else {
            throw ProbeError.message("PPT 图片地址或时间无效，请刷新课堂材料。")
        }
        self.url = url; self.time = time
    }
}

/// Streams one image into bounded memory, rejecting redirects and cookies.
final class SlideDownload: NSObject, URLSessionDataDelegate, URLSessionTaskDelegate, @unchecked Sendable {
    private var bytes = Data()
    private var continuation: CheckedContinuation<Data, Error>?
    private var issue: String?
    private var session: URLSession?
    func get(_ url: URL) async throws -> Data {
        try await withCheckedThrowingContinuation { continuation in
            self.continuation = continuation
            let config = URLSessionConfiguration.ephemeral
            config.httpCookieStorage = nil; config.urlCredentialStorage = nil; config.timeoutIntervalForRequest = 30
            let session = URLSession(configuration: config, delegate: self, delegateQueue: nil)
            self.session = session; session.dataTask(with: url).resume()
        }
    }
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) { completionHandler(nil) }
    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse, completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        guard let response = response as? HTTPURLResponse, response.statusCode == 200 else { issue = "PPT 图片读取失败，请刷新材料后重试。"; completionHandler(.cancel); return }
        guard response.expectedContentLength <= 12 * 1024 * 1024 else { issue = "单张 PPT 图片过大，导出已停止。"; completionHandler(.cancel); return }
        completionHandler(.allow)
    }
    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        guard bytes.count + data.count <= 12 * 1024 * 1024 else { issue = "单张 PPT 图片过大，导出已停止。"; dataTask.cancel(); return }
        bytes.append(data)
    }
    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        defer { session.finishTasksAndInvalidate(); self.session = nil; continuation = nil }
        if let issue { continuation?.resume(throwing: ProbeError.message(issue)) }
        else if error != nil { continuation?.resume(throwing: ProbeError.message("PPT 图片下载失败或超时，请稍后重试。")) }
        else { continuation?.resume(returning: bytes) }
    }
}

struct SlideFingerprint {
    let digest: SHA256.Digest
    let ratio: Double
    let pixels: [UInt8]
    let hash: [UInt64]
    init(image: CGImage, original: Data) throws {
        digest = SHA256.hash(data: original); ratio = Double(image.width) / Double(image.height)
        var rgba = [UInt8](repeating: 255, count: 640 * 360 * 4)
        guard let context = CGContext(data: &rgba, width: 640, height: 360, bitsPerComponent: 8, bytesPerRow: 640 * 4,
                                      space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { throw ProbeError.message("无法处理 PPT 图片。") }
        context.setFillColor(NSColor.white.cgColor); context.fill(CGRect(x:0,y:0,width:640,height:360))
        context.interpolationQuality = .high; context.draw(image, in: CGRect(x:0,y:0,width:640,height:360))
        pixels = rgba
        var bits = [UInt64](repeating: 0, count: 4)
        func luminance(_ x: Int, _ y: Int) -> Int { let i = ((y * 359 / 15) * 640 + x * 639 / 16) * 4; return 299 * Int(rgba[i]) + 587 * Int(rgba[i+1]) + 114 * Int(rgba[i+2]) }
        for y in 0..<16 { for x in 0..<16 { let i = y * 16 + x; if luminance(x,y) > luminance(x+1,y) { bits[i/64] |= UInt64(1) << (i%64) } } }
        hash = bits
    }
    func matches(_ other: SlideFingerprint) -> Bool {
        if digest == other.digest { return true }
        guard abs(ratio - other.ratio) <= 0.005, zip(hash, other.hash).reduce(0, { $0 + ($1.0 ^ $1.1).nonzeroBitCount }) <= 12 else { return false }
        var channel = [Int](repeating:0,count:3), blocks = [Int](repeating:0,count:40*23), counts = [Int](repeating:0,count:40*23)
        for y in 0..<360 { for x in 0..<640 {
            let i = (y*640+x)*4; var changed = false
            for c in 0..<3 { let d = abs(Int(pixels[i+c])-Int(other.pixels[i+c])); channel[c] += d; changed = changed || d > 24 }
            let block = min(22,y*23/360)*40 + x/16; counts[block] += 1; if changed { blocks[block] += 1 }
        } }
        guard channel.allSatisfy({ Double($0) / Double(640*360) <= 2.5 }) else { return false }
        return zip(blocks,counts).allSatisfy { Double($0.0) / Double($0.1) <= 3.0/255.0 }
    }
}

enum SlideExporter {
    static func decode(_ data: Data) throws -> CGImage {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil),
              let type = CGImageSourceGetType(source) as String?, ["public.png", "public.jpeg", "org.webmproject.webp"].contains(type),
              let info = CGImageSourceCopyPropertiesAtIndex(source,0,nil) as? [CFString:Any],
              let width = info[kCGImagePropertyPixelWidth] as? Int, let height = info[kCGImagePropertyPixelHeight] as? Int,
              width > 0, height > 0, width <= 20_000_000 / height,
              let image = CGImageSourceCreateThumbnailAtIndex(source,0,[kCGImageSourceCreateThumbnailFromImageAlways:true, kCGImageSourceCreateThumbnailWithTransform:true, kCGImageSourceThumbnailMaxPixelSize:max(width,height)] as CFDictionary) else { throw ProbeError.message("PPT 图片格式或尺寸不支持。") }
        return image
    }
    static func export(rows: [SlideInput], destination: URL, progress: @escaping @Sendable (String) -> Void,
                       fetch: @escaping (URL) async throws -> Data = { try await SlideDownload().get($0) }) async throws -> (Int, Int) {
        guard !rows.isEmpty, rows.count <= 1000 else { throw ProbeError.message("没有可导出的截图，或截图超过 1000 张。") }
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory:true)
        try FileManager.default.createDirectory(at:dir,withIntermediateDirectories:false,attributes:[.posixPermissions:0o700])
        defer { try? FileManager.default.removeItem(at:dir) }
        let draft = dir.appendingPathComponent("slides.pdf")
        var fingerprints: [SlideFingerprint] = []; var total = 0
        var pages: [(url:URL,times:[Double])] = []
        for (index,row) in rows.sorted(by: { $0.time < $1.time }).enumerated() {
            let data = try await fetch(row.url); total += data.count
            guard data.count <= 12*1024*1024, total <= 256*1024*1024 else { throw ProbeError.message("截图大小超过导出上限。") }
            let image = try decode(data); let fp = try SlideFingerprint(image:image, original:data)
            if let existing = fingerprints.firstIndex(where: { $0.matches(fp) }) {
                pages[existing].times.append(row.time)
            } else {
                guard (fingerprints.count + 1) * 640 * 360 * 4 <= 256*1024*1024 else { throw ProbeError.message("不同截图过多，超出导出内存上限。") }
                fingerprints.append(fp)
                let file = dir.appendingPathComponent("image-\(pages.count)")
                try data.write(to:file,options:.atomic)
                pages.append((url:file,times:[row.time]))
            }
            progress("正在去重 PPT 截图 \(index+1)/\(rows.count)，保留 \(fingerprints.count) 页")
        }
        guard let pdf = CGContext(draft as CFURL, mediaBox:nil, nil) else { throw ProbeError.message("无法创建 PDF。") }
        for (index,page) in pages.enumerated() {
            let image = try decode(Data(contentsOf:page.url,options:.mappedIfSafe))
            let imageHeight = 960.0 * Double(image.height) / Double(image.width)
            var box = CGRect(x:0,y:0,width:960,height:imageHeight+22)
            let boxData = NSData(bytes:&box,length:MemoryLayout<CGRect>.size)
            pdf.beginPDFPage([kCGPDFContextMediaBox as String:boxData] as CFDictionary)
            pdf.draw(image,in:CGRect(x:0,y:22,width:960,height:imageHeight))
            let times = page.times.map(lessonClock).joined(separator:", ")
            let compactTimes = times.count > 110 ? "\(lessonClock(page.times[0]))  (+\(page.times.count-1))" : times
            let text = "\(index+1) / \(pages.count)    \(compactTimes)" as CFString
            let attrs = [kCTFontAttributeName:CTFontCreateWithName("Helvetica" as CFString,9,nil), kCTForegroundColorAttributeName:NSColor.gray.cgColor] as CFDictionary
            let line = CTLineCreateWithAttributedString(CFAttributedStringCreate(nil,text,attrs)); pdf.textPosition = CGPoint(x:12,y:7); CTLineDraw(line,pdf)
            pdf.endPDFPage()
        }
        pdf.closePDF()
        let output = try Data(contentsOf:draft,options:.mappedIfSafe)
        try output.write(to:destination,options:.atomic)
        return (rows.count, fingerprints.count)
    }
}
