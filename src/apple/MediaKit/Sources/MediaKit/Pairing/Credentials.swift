import Foundation
import Security

/// Everything a paired device holds, and it holds it only while paired.
public struct PairedServer: Codable, Equatable, Sendable {
    public let server: URL
    public let serverName: String
    public let appId: String

    /// The Core this device was actually approved against, pinned at pairing time.
    ///
    /// Refreshing must not re-ask an anonymous route where Core lives. That answer comes from the media
    /// endpoint, and the credential it would be handed is the *full-privilege* one — a compromised or
    /// intercepted endpoint could name its own origin and be given a token that reaches Core and every
    /// other app on the host. An origin that changes is a re-pairing, not a redirect.
    public let coreOrigin: URL

    /// Core's own credential. **This is the sensitive one.** Core has no scopes, so it carries its
    /// holder's full Core role — it can reach Core itself and every other app on the host. It is kept
    /// only because the app grant lapses long before it does, and re-minting one silently is the
    /// difference between a device that keeps working and a television that asks to be paired again
    /// every week.
    public let coreToken: String

    /// The credential that actually leaves the device on a request to us: audience-scoped to this app,
    /// and useless anywhere else.
    public var identity: AppIdentity

    public init(
        server: URL,
        serverName: String,
        appId: String,
        coreOrigin: URL,
        coreToken: String,
        identity: AppIdentity
    ) {
        self.server = server
        self.serverName = serverName
        self.appId = appId
        self.coreOrigin = coreOrigin
        self.coreToken = coreToken
        self.identity = identity
    }

    /// Whether the app grant is close enough to lapsing to be worth re-minting now.
    ///
    /// A minute of margin, because the alternative is discovering it mid-request. The grant is seven
    /// days idle and thirty absolute, so this is rare and cheap.
    public func identityIsStale(now: Date = Date()) -> Bool {
        identity.expiresAt.timeIntervalSince(now) < 60
    }
}

public protocol CredentialStore: Sendable {
    func load() -> PairedServer?

    /// Returns whether it stuck. A caller that cannot store a credential should not pretend it did.
    @discardableResult
    func save(_ paired: PairedServer) -> Bool

    func clear()
}

/// The Keychain, holding the pair as one item.
///
/// One item rather than two: a device that held a Core token and no app grant, or the reverse, would be
/// a state nothing knows how to resume from. Pairing is all-or-nothing and the storage says so.
public struct KeychainCredentialStore: CredentialStore {
    private let service: String
    private let account = "paired-server"

    public init(service: String = "com.haas.mediaserver.credentials") {
        self.service = service
    }

    private var query: [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
    }

    public func load() -> PairedServer? {
        var lookup = query
        lookup[kSecReturnData as String] = true
        lookup[kSecMatchLimit as String] = kSecMatchLimitOne

        var item: CFTypeRef?
        guard SecItemCopyMatching(lookup as CFDictionary, &item) == errSecSuccess,
              let data = item as? Data
        else {
            return nil
        }

        // Something written by a version that stored a different shape is not a credential this build
        // can use, and an unpairing is a better answer than a crash.
        return try? JSONDecoder.pairing.decode(PairedServer.self, from: data)
    }

    /// Update in place, and only fall back to inserting when there is nothing to update.
    ///
    /// Deleting first and adding after would mean a failed add — a Keychain error, a missing
    /// entitlement — silently unpaired the device, which is the worst possible outcome of *saving*
    /// something. This way a failure leaves the previous credential where it was.
    @discardableResult
    public func save(_ paired: PairedServer) -> Bool {
        guard let data = try? JSONEncoder.pairing.encode(paired) else { return false }

        let updated = SecItemUpdate(
            query as CFDictionary, [kSecValueData as String: data] as CFDictionary)
        if updated == errSecSuccess {
            return true
        }

        var insert = query
        insert[kSecValueData as String] = data
        // The device is a television that unlocks itself; anything stricter than "after first unlock"
        // would leave the app unable to read its own credential on a cold boot.
        insert[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock
        return SecItemAdd(insert as CFDictionary, nil) == errSecSuccess
    }

    public func clear() {
        SecItemDelete(query as CFDictionary)
    }
}

/// For tests, and for a preview that must not touch a real Keychain.
public final class InMemoryCredentialStore: CredentialStore, @unchecked Sendable {
    private let lock = NSLock()
    private var stored: PairedServer?

    public init(_ initial: PairedServer? = nil) {
        stored = initial
    }

    public func load() -> PairedServer? {
        lock.withLock { stored }
    }

    @discardableResult
    public func save(_ paired: PairedServer) -> Bool {
        lock.withLock { stored = paired }
        return true
    }

    public func clear() {
        lock.withLock { stored = nil }
    }
}

extension JSONEncoder {
    static var pairing: JSONEncoder {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .custom { date, encoder in
            var container = encoder.singleValueContainer()
            try container.encode(PairingDates.format(date))
        }

        return encoder
    }
}
