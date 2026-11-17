import Foundation
import Security
import CryptoKit

protocol ModelCredentialStore {
    func load(account: String, endpoint: String) throws -> String?
    func save(_ key: String, account: String, endpoint: String) throws
    func delete(account: String, endpoint: String) throws
}

/// Keys are scoped to both the school account and the model endpoint.
struct KeychainModelCredentialStore: ModelCredentialStore {
    private let service: String
    init(service: String = "io.github.fyzzzz05.buaa-learn.model-key") { self.service = service }
    private func query(account: String, endpoint: String) throws -> [String:Any] {
        let bytes = try JSONEncoder().encode([account, endpoint])
        let id = SHA256.hash(data: bytes).map { String(format: "%02x", $0) }.joined()
        return [kSecClass as String: kSecClassGenericPassword,
                kSecAttrService as String: service, kSecAttrAccount as String: id]
    }
    func load(account: String, endpoint: String) throws -> String? {
        var request = try query(account: account, endpoint: endpoint)
        request[kSecReturnData as String] = true
        request[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        let status = SecItemCopyMatching(request as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let data = result as? Data, let key = String(data: data, encoding: .utf8) else {
            throw ProbeError.message("无法读取钥匙串中的模型密钥，请检查钥匙串访问权限。")
        }
        return key
    }
    func save(_ key: String, account: String, endpoint: String) throws {
        let request = try query(account: account, endpoint: endpoint)
        let attributes = [kSecValueData as String: Data(key.utf8)]
        let status = SecItemUpdate(request as CFDictionary, attributes as CFDictionary)
        if status == errSecSuccess { return }
        guard status == errSecItemNotFound else { throw ProbeError.message("无法保存模型密钥，请检查钥匙串访问权限。") }
        var item = request
        item[kSecValueData as String] = Data(key.utf8)
        let added = SecItemAdd(item as CFDictionary, nil)
        guard added == errSecSuccess || (added == errSecDuplicateItem && SecItemUpdate(request as CFDictionary, attributes as CFDictionary) == errSecSuccess) else {
            throw ProbeError.message("无法保存模型密钥，请检查钥匙串访问权限。")
        }
    }
    func delete(account: String, endpoint: String) throws {
        let status = SecItemDelete(try query(account: account, endpoint: endpoint) as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw ProbeError.message("无法移除模型密钥，请检查钥匙串访问权限。")
        }
    }
}
