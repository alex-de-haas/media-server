import Foundation
import MediaServerAPI

/// What a server says about itself before anyone has a credential.
///
/// The one anonymous route on the whole surface, and it has to be: needing a token to discover where
/// tokens come from is a loop. The field is `coreOrigin` — its *value* is the app's `CorePublicOrigin`,
/// but that is the source, not the name on the wire.
public struct ServerBootstrap: Equatable, Sendable {
    public let serverName: String
    public let appId: String

    /// A **string**, not a number — `NativeSurface.Version` is `"1"`. This was declared as an integer
    /// once, which meant no real server could be decoded at all, and the fixture that should have caught
    /// it had been written to match the model instead of the contract.
    public let surfaceVersion: String

    /// Null on a host with no public Core origin: a pairing that cannot proceed rather than a malformed
    /// answer, so it is optional here and refused explicitly where it is used.
    public let coreOrigin: String?

    /// The origin Core has installed for this app, and the only thing it will accept as a
    /// `redirectUri` when this device asks to be authorised.
    ///
    /// Told rather than assumed, because the address a viewer types need not be one Core has ever heard
    /// of. A television reaching a server across the room types its address *on this network*, and Core
    /// checks the redirect against the app's installed endpoints — so that pairing failed at the last
    /// step, after the viewer had already approved the code, which is the most expensive place to fail.
    ///
    /// Null on a host with no public origin for this app, and then the typed address is used, which is
    /// what every pairing did before this existed.
    public let pairingOrigin: String?

    public init(
        serverName: String,
        appId: String,
        surfaceVersion: String,
        coreOrigin: String?,
        pairingOrigin: String? = nil
    ) {
        self.serverName = serverName
        self.appId = appId
        self.surfaceVersion = surfaceVersion
        self.coreOrigin = coreOrigin
        self.pairingOrigin = pairingOrigin
    }

    /// Built from the generated type rather than decoded by hand.
    ///
    /// This initialiser is the whole guarantee: the field types come from
    /// `src/api/openapi/MediaServer.Api_native.json` by way of the generator, so a surface that changes
    /// shape stops this compiling instead of failing quietly on a television.
    public init(_ generated: Components.Schemas.NativeServerBootstrap) {
        self.init(
            serverName: generated.serverName,
            appId: generated.appId,
            surfaceVersion: generated.surfaceVersion,
            coreOrigin: generated.coreOrigin,
            pairingOrigin: generated.pairingOrigin)
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

    /// A Media Server that cannot say where its Core is, so there is nowhere to be approved.
    case noCoreOrigin

    /// Core refused the credential outright: it has been revoked, or it expired. Terminal — unlike a
    /// server that is merely unreachable, retrying this one will never start working.
    case credentialRejected

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

    /// Whether this answer will still be the answer tomorrow.
    ///
    /// It decides whether a failed refresh forgets the pairing. A revoked token or an unassigned
    /// account is over; a server that was asleep when the television woke up is not, and treating the
    /// second like the first means re-pairing a device every time the network is slow at breakfast.
    public var isTerminal: Bool {
        switch self {
        case .credentialRejected, .notAssigned, .denied:
            true
        default:
            false
        }
    }
}
