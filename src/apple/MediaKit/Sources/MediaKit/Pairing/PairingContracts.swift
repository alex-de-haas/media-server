import Foundation

/// What a server says about itself before anyone has a credential.
///
/// The one anonymous route on the whole surface, and it has to be: needing a token to discover where
/// tokens come from is a loop. The field is `coreOrigin` — its *value* is the app's `CorePublicOrigin`,
/// but that is the source, not the name on the wire.
public struct ServerBootstrap: Codable, Equatable, Sendable {
    public let serverName: String
    public let appId: String
    public let surfaceVersion: Int
    public let coreOrigin: String

    public init(serverName: String, appId: String, surfaceVersion: Int, coreOrigin: String) {
        self.serverName = serverName
        self.appId = appId
        self.surfaceVersion = surfaceVersion
        self.coreOrigin = coreOrigin
    }
}

/// A pending device authorization: what goes on the television, and how long it is worth waiting.
public struct DeviceCodeGrant: Codable, Equatable, Sendable {
    /// The client's half, never shown — it is what the poll is made with.
    public let deviceCode: String

    /// The human's half: eight characters in a lookalike-free alphabet, read across a room.
    public let userCode: String

    /// Where to approve it. Null when the host runs no Shell, in which case the viewer has to find the
    /// approval screen themselves rather than be sent to an address Core invented.
    public let verificationUri: String?

    public let intervalSeconds: Int
    public let expiresInSeconds: Int
}

/// The four answers a poll can give. Anything else is a protocol error rather than a state.
public enum DevicePollOutcome: Equatable, Sendable {
    case pending
    case approved(token: String)
    case denied
    case expired
}

/// A token audience-scoped to one app, which is the only credential that ever reaches the media server.
public struct AppIdentity: Codable, Equatable, Sendable {
    public let accessToken: String
    public let expiresAt: Date

    public init(accessToken: String, expiresAt: Date) {
        self.accessToken = accessToken
        self.expiresAt = expiresAt
    }
}

/// Everything that stops a pairing, in terms a screen can say out loud.
public enum PairingError: Error, Equatable, Sendable {
    /// The address answered, but not as a Media Server.
    case notAMediaServer

    /// Nothing answered at all.
    case unreachable(String)

    /// This Core predates the device routes — 0.73.0 added them. A sign-in that appears to work and
    /// then bounces is the failure this exists to prevent.
    case coreTooOld

    /// Core is holding too many pending requests from this address. Waiting is the whole remedy.
    case throttled

    /// The viewer took too long, or the code was never approved.
    case codeExpired

    /// Somebody said no.
    case denied

    /// The approving user is not assigned to this app. Assignment is enforced at issuance, so this is a
    /// permission answer rather than a network one.
    case notAssigned

    /// Anything the server said that this client has no better word for.
    case server(code: String, message: String)
}
