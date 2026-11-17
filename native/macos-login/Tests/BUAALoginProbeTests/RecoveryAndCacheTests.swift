import XCTest
import AppKit
import PDFKit
@testable import BUAALoginProbe

private final class SessionCredentials: ModelCredentialStore {
    var values: [String:String] = [:]
    func load(account:String,endpoint:String) throws -> String? { values[account + endpoint] }
    func save(_ key:String,account:String,endpoint:String) throws { values[account + endpoint] = key }
    func delete(account:String,endpoint:String) throws { values.removeValue(forKey:account + endpoint) }
}

private final class SessionStub: URLProtocol {
    override class func canInit(with request:URLRequest) -> Bool { true }
    override class func canonicalRequest(for request:URLRequest) -> URLRequest { request }
    override func startLoading() {
        guard let url = request.url else { return }
        let token = request.value(forHTTPHeaderField:"Token") ?? ""
        let status: Int
        let body: String
        if url.path.hasSuffix("refreshToken") { status = 200; body = #"{"code":200,"content":{"token":"renewed","refreshToken":"renewed-refresh"}}"# }
        else if token == "Inco-expired" || token == "Inco-old" { status = 401; body = #"{"code":401}"# }
        else if url.path.hasSuffix("queryXnxq") { status = 200; body = #"{"code":200,"content":[{"xnxq":"2026"}]}"# }
        else if url.path.contains("catalogue") { status = 401; body = #"{"code":5002}"# }
        else { status = 200; body = #"{"code":200,"content":[]}"# }
        client?.urlProtocol(self,didReceive:HTTPURLResponse(url:url,statusCode:status,httpVersion:nil,headerFields:nil)!,cacheStoragePolicy:.notAllowed)
        client?.urlProtocol(self,didLoad:Data(body.utf8)); client?.urlProtocolDidFinishLoading(self)
    }
    override func stopLoading() {}
}

final class RecoveryAndCacheTests: XCTestCase {
    private func session() -> URLSession {
        let config = URLSessionConfiguration.ephemeral; config.protocolClasses = [SessionStub.self]
        return URLSession(configuration:config)
    }
    @MainActor func testExpiredSessionUpdatesUIAndKeepsLocalRecords() async throws {
        let transport = session(); defer { transport.invalidateAndCancel() }
        let credentials = SessionCredentials(), store = try NativeStore(inMemory:())
        let probe = Probe(credentials:credentials,schoolCredentials:credentials)
        try credentials.save("expired-session",account:"alice",endpoint:"https://spoc.buaa.edu.cn/session")
        let school = try NativeSchoolSession(token:"expired",refresh:"",role:"01",session:transport)
        try store.put(owner:"alice",kind:"course",id:"c",value:["id":"c","name":"保留课程","semester":"2026"])
        try probe.attachSchool(session:school,store:store,account:"alice")
        probe.signedIn = true; probe.selectedTerm = "2026"
        await probe.loadCourses()
        XCTAssertFalse(probe.signedIn)
        XCTAssertTrue(probe.sessionIssue.contains("过期"))
        XCTAssertTrue(probe.operationIssue.contains("过期"))
        XCTAssertEqual(try probe.records(kind:"course").count,1)
        XCTAssertEqual(try probe.records(kind:"job").first?["status"] as? String,"failed")
        XCTAssertFalse(probe.working)
        XCTAssertNil(try credentials.load(account:"alice",endpoint:"https://spoc.buaa.edu.cn/session"))
    }
    @MainActor func testRefreshUpdatesSecureCredentialAndResumeValidatesIt() async throws {
        let transport = session(); defer { transport.invalidateAndCancel() }
        let credentials = SessionCredentials()
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at:directory) }
        try NativeStore.rememberAccount("alice",baseDirectory:directory)
        try credentials.save(#"{"token":"old","refresh":"refresh","role":"01"}"#,account:"alice",endpoint:"https://spoc.buaa.edu.cn/session")
        let probe = Probe(credentials:SessionCredentials(),schoolCredentials:credentials,makeSchool:{ try NativeSchoolSession(token:$0,refresh:$1,role:$2,session:transport) })
        try probe.restoreWorkspace(baseDirectory:directory)
        await probe.resumeSchoolSession()
        XCTAssertTrue(probe.signedIn)
        let saved = try XCTUnwrap(credentials.load(account:"alice",endpoint:"https://spoc.buaa.edu.cn/session"))
        let value = try XCTUnwrap(JSONSerialization.jsonObject(with:Data(saved.utf8)) as? [String:Any])
        XCTAssertEqual(value["token"] as? String,"renewed")
        XCTAssertEqual(value["refresh"] as? String,"renewed-refresh")
        XCTAssertTrue(probe.sessionIssue.isEmpty)
        XCTAssertFalse(probe.working)
    }
    @MainActor func testRecordingExpiryAlsoInvalidatesConnectedIndicator() async throws {
        let transport = session(); defer { transport.invalidateAndCancel() }
        let probe = Probe(credentials:SessionCredentials()), store = try NativeStore(inMemory:())
        let school = try NativeSchoolSession(token:"valid",refresh:"",role:"01",session:transport)
        try probe.attachSchool(session:school,store:store,account:"alice"); probe.signedIn = true
        try await school.setRecording(token:"recording")
        do { _ = try await school.catalogue(course:"1"); XCTFail("Expected authorization failure") }
        catch { XCTAssertTrue(error is SchoolAuthorizationIssue) }
        XCTAssertFalse(probe.signedIn)
        XCTAssertTrue(probe.sessionIssue.contains("录播"))
    }
    func testInterruptedRecoveryIsIdempotentAndAccountScoped() throws {
        let store = try NativeStore(inMemory:())
        try store.put(owner:"alice",kind:"job",id:"unfinished",value:["id":"unfinished","kind":"analyze","status":"running","target":"l","created_at":"new"])
        try store.put(owner:"alice",kind:"job",id:"saved",value:["id":"saved","kind":"analyze","status":"running","target":"saved","created_at":"same"])
        try store.put(owner:"alice",kind:"analysis",id:"saved",value:["created_at":"same","sections":[]])
        try store.put(owner:"alice",kind:"analysis",id:"l",value:["created_at":"old","sections":[]])
        try store.put(owner:"bob",kind:"job",id:"other",value:["id":"other","status":"running"])
        XCTAssertEqual(try InterruptedJobs.recover(store:store,owner:"alice"),2)
        XCTAssertEqual((try store.get(owner:"alice",kind:"job",id:"unfinished") as? [String:Any])?["status"] as? String,"interrupted")
        XCTAssertEqual((try store.get(owner:"alice",kind:"job",id:"saved") as? [String:Any])?["status"] as? String,"completed")
        XCTAssertEqual((try store.get(owner:"bob",kind:"job",id:"other") as? [String:Any])?["status"] as? String,"running")
        XCTAssertEqual(try InterruptedJobs.recover(store:store,owner:"alice"),0)
    }
    @MainActor func testAnotherAppCannotRecoverLiveWorkspaceJobs() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at:directory) }
        try NativeStore.rememberAccount("alice",baseDirectory:directory)
        let store = try NativeStore.openApplicationStore(baseDirectory:directory)
        let first = Probe(credentials:SessionCredentials())
        try first.restoreWorkspace(baseDirectory:directory)
        try store.put(owner:"alice",kind:"job",id:"live",value:["id":"live","status":"running","kind":"analyze"])
        let second = Probe(credentials:SessionCredentials())
        XCTAssertThrowsError(try second.restoreWorkspace(baseDirectory:directory))
        XCTAssertEqual((try store.get(owner:"alice",kind:"job",id:"live") as? [String:Any])?["status"] as? String,"running")
        withExtendedLifetime(first) {}
    }
    @MainActor func testInvalidImageNeverEntersDatabase() async throws {
        let store = try NativeStore(inMemory:()), url = URL(string:"https://resource.msa.buaa.edu.cn/invalid.png")!
        do { _ = try await SlideAssetCache.load(url:url,owner:"alice",store:store,fetch:{ _ in Data("invalid".utf8) }); XCTFail("Expected image rejection") }
        catch { XCTAssertNil(try store.slideData(owner:"alice",url:url)) }
    }
    @MainActor func testOfflineSlideSurvivesReopenAndExportsWithoutNetwork() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at:directory,withIntermediateDirectories:false)
        defer { try? FileManager.default.removeItem(at:directory) }
        let path = directory.appendingPathComponent("test.sqlite3"), url = URL(string:"https://resource.msa.buaa.edu.cn/test.png")!
        let bitmap = NSBitmapImageRep(bitmapDataPlanes:nil,pixelsWide:2,pixelsHigh:2,bitsPerSample:8,samplesPerPixel:4,hasAlpha:true,isPlanar:false,colorSpaceName:.deviceRGB,bytesPerRow:8,bitsPerPixel:32)!
        let data = try XCTUnwrap(bitmap.representation(using:.png,properties:[:]))
        do {
            let store = try NativeStore(url:path)
            let fetched = try await SlideAssetCache.load(url:url,owner:"alice",store:store,fetch:{ _ in data })
            XCTAssertEqual(fetched,data)
            XCTAssertNil(try store.slideData(owner:"bob",url:url))
        }
        let reopened = try NativeStore(url:path)
        let load: (URL) async throws -> Data = { url in
            try await SlideAssetCache.load(url:url,owner:"alice",store:reopened,fetch:{ _ in throw URLError(.notConnectedToInternet) })
        }
        let saved = try await load(url)
        XCTAssertEqual(saved,data)
        let output = directory.appendingPathComponent("offline.pdf")
        let result = try await SlideExporter.export(rows:[SlideInput(["url":url.absoluteString,"start":12])],destination:output,progress:{ _ in },fetch:load)
        XCTAssertEqual(result.1,1)
        XCTAssertEqual(PDFDocument(url:output)?.pageCount,1)
    }
}
