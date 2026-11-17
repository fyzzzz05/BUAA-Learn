import Foundation

enum InterruptedJobs {
    /// Called only after obtaining the workspace lease; a live second app is never recovered.
    static func recover(store: NativeStore, owner: String) throws -> Int {
        var count = 0
        for var job in try store.list(owner:owner, kind:"job").compactMap({ $0 as? [String:Any] }) {
            guard ["running", "queued"].contains(job["status"] as? String ?? ""), let id = job["id"] as? String else { continue }
            var completed = false
            if job["kind"] as? String == "analyze", let target = job["target"] as? String,
               let analysis = try store.get(owner:owner, kind:"analysis", id:target) as? [String:Any],
               let created = job["created_at"] as? String, analysis["created_at"] as? String == created {
                completed = true
            }
            job["status"] = completed ? "completed" : "interrupted"
            job["message"] = completed ? "上次退出前已保存学习笔记。" : "上次运行已中断，已保存的材料和笔记仍保留，可重新执行。"
            job["finished_at"] = ISO8601DateFormatter().string(from:Date())
            try store.put(owner:owner, kind:"job", id:id, value:job)
            count += 1
        }
        return count
    }
}
