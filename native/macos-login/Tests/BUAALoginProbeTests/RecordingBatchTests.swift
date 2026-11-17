import Foundation
import XCTest
@testable import BUAALoginProbe

final class RecordingBatchTests: XCTestCase {
    @MainActor
    func testBatchReallyOverlapsWorkAndNeverExceedsThree() async {
        var active = 0, peak = 0
        var finished: [Int] = []
        await RecordingBatch.run(items:Array(0..<9)) { index in
            active += 1; peak = max(peak,active)
            try? await Task.sleep(nanoseconds:index == 0 ? 80_000_000 : 10_000_000)
            active -= 1; finished.append(index)
        }
        XCTAssertEqual(peak,3)
        XCTAssertEqual(finished.sorted(),Array(0..<9))
        XCTAssertNotEqual(finished.first,0)
    }

    @MainActor
    func testCachedEmptyCatalogueFinishesWithoutOpeningAuthorizationBrowser() async throws {
        let config = URLSessionConfiguration.ephemeral; config.protocolClasses = [EmptyCatalogueStub.self]
        let session = URLSession(configuration:config); defer { session.invalidateAndCancel() }
        let school = try NativeSchoolSession(token:"test",refresh:"",role:"01",session:session)
        try await school.setRecording(token:"cached-recording-token")
        let store = try NativeStore(inMemory:())
        try store.put(owner:"alice",kind:"meta",id:"semester",value:["id":"current"])
        for id in ["a","b","c"] {
            try store.put(owner:"alice",kind:"course",id:id,value:["id":id,"name":id,"semester":"current","recording_id":"42"])
        }
        let probe = Probe(credentials:BatchTestCredentials(),recordingAuthorization:{ _,_ in
            XCTFail("An empty authenticated catalogue must not trigger a browser wait")
            throw ProbeError.message("unexpected authorization")
        })
        try probe.attachSchool(session:school,store:store,account:"alice")
        probe.signedIn = true
        await probe.loadLatestRecordings()
        XCTAssertEqual(probe.courseSyncJob?.phase,.completed)
        XCTAssertEqual(probe.courseSyncJob?.message,"已同步 3 门课程，新增 0 节回放。")
        XCTAssertTrue(try probe.records(kind:"course").allSatisfy { $0["recording_count"] as? Int == 0 })
    }
}
private final class EmptyCatalogueStub: URLProtocol {
    override class func canInit(with request:URLRequest) -> Bool { true }
    override class func canonicalRequest(for request:URLRequest) -> URLRequest { request }
    override func startLoading() {
        client?.urlProtocol(self,didReceive:HTTPURLResponse(url:request.url!,statusCode:200,httpVersion:nil,headerFields:nil)!,cacheStoragePolicy:.notAllowed)
        client?.urlProtocol(self,didLoad:Data(#"{"code":0,"result":{"data":[]}}"#.utf8))
        client?.urlProtocolDidFinishLoading(self)
    }
    override func stopLoading() {}
}

private final class BatchTestCredentials: ModelCredentialStore {
    func load(account:String, endpoint:String) throws -> String? { nil }
    func save(_ key:String, account:String, endpoint:String) throws {}
    func delete(account:String, endpoint:String) throws {}
}
