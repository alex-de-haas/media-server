import Foundation
import Observation

/// Where a pairing has got to, in the terms a screen shows.
public enum PairingState: Equatable, Sendable {
    case idle
    case checking
    case awaitingApproval(DeviceCodeGrant, serverName: String)
    case paired(PairedServer)
    case failed(PairingError)
}

/// Drives the whole chain from a typed address to a stored credential.
///
/// The chain is Core's, not ours — see `docs/features/apple-client-core/plan.md`. What lives here is the
/// waiting: a poll that runs no faster than Core asks, stops when the code expires, and is cancelled the
/// moment the screen showing the code goes away. A poll that outlives its screen is a device quietly
/// asking to be signed in while nobody is looking at it.
@MainActor
@Observable
public final class PairingSession {
    public private(set) var state: PairingState = .idle

    private let client: PairingClient
    private let store: any CredentialStore
    private let label: String
    private let sleep: @Sendable (Duration) async throws -> Void
    private var work: Task<Void, Never>?

    public init(
        client: PairingClient = PairingClient(),
        store: any CredentialStore = KeychainCredentialStore(),
        label: String = PairingSession.deviceLabel,
        sleep: @escaping @Sendable (Duration) async throws -> Void = { try await Task.sleep(for: $0) }
    ) {
        self.client = client
        self.store = store
        self.label = label
        self.sleep = sleep
    }

    /// The name the approving human sees in Shell. Theirs is the decision, so it should say which
    /// television is asking rather than "device".
    public static var deviceLabel: String {
        #if os(tvOS) || os(iOS)
        return ProcessInfo.processInfo.hostName
        #else
        return Host.current().localizedName ?? "Mac"
        #endif
    }

    /// Restores a previous pairing, re-minting the app grant if it has lapsed.
    ///
    /// The two lifetimes are why this exists: the app grant is days, Core's own token is months. A
    /// viewer should see the pairing screen when the second has gone, not every time the first does.
    public func restore() async {
        guard let paired = store.load() else {
            state = .idle
            return
        }

        guard paired.identityIsStale() else {
            state = .paired(paired)
            return
        }

        do {
            // The Core pinned at pairing time, never one an anonymous route named just now: the token
            // about to be presented is the full-privilege one, and an endpoint that could redirect it
            // could steal it.
            let identity = try await client.exchange(
                core: paired.coreOrigin,
                appId: paired.appId,
                redirectUri: paired.server,
                coreToken: paired.coreToken)

            var refreshed = paired
            refreshed.identity = identity
            store.save(refreshed)
            state = .paired(refreshed)
        } catch {
            let failure = error as? PairingError ?? .unreachable("\(error)")

            // Only a refusal is a reason to forget. A television that woke up before the network did
            // must not have to be paired again over it, so a transient failure keeps the credential and
            // stays paired — the next request will refresh it or fail honestly.
            if failure.isTerminal {
                store.clear()
                state = .failed(failure)
            } else {
                state = .paired(paired)
            }
        }
    }

    /// Begins a pairing against a typed address, and keeps polling until it resolves.
    public func start(address: String) {
        work?.cancel()

        guard let server = PairingSession.normalise(address) else {
            state = .failed(.notAMediaServer)
            return
        }

        state = .checking
        work = Task { [weak self] in
            await self?.run(server: server)
        }
    }

    /// Stops a pairing in flight. Called when the screen goes away, which is the point.
    ///
    /// A pairing that already succeeded is left alone. The screen showing the code disappears *because*
    /// it succeeded, and its `onDisappear` arrives after the state has moved on — so treating that as a
    /// cancellation would drop the viewer back on address entry the instant they finished pairing.
    public func cancel() {
        if case .paired = state { return }

        work?.cancel()
        work = nil
        state = .idle
    }

    /// Forgets the pairing entirely.
    public func unpair() {
        cancel()
        store.clear()
    }

    private func run(server: URL) async {
        do {
            let bootstrap = try await client.bootstrap(server: server)
            guard let origin = bootstrap.coreOrigin, let core = URL(string: origin) else {
                // A Media Server that cannot say where its Core is leaves nowhere to be approved.
                throw PairingError.noCoreOrigin
            }

            let grant = try await client.requestDeviceCode(core: core, label: label)
            state = .awaitingApproval(grant, serverName: bootstrap.serverName)

            let coreToken = try await awaitApproval(core: core, grant: grant)
            let identity = try await client.exchange(
                core: core, appId: bootstrap.appId, redirectUri: server, coreToken: coreToken)

            let paired = PairedServer(
                server: server,
                serverName: bootstrap.serverName,
                appId: bootstrap.appId,
                coreOrigin: core,
                coreToken: coreToken,
                identity: identity)

            store.save(paired)
            state = .paired(paired)
        } catch is CancellationError {
            // The screen went away. Not a failure, and not something to report.
        } catch {
            state = .failed(error as? PairingError ?? .unreachable("\(error)"))
        }
    }

    private func awaitApproval(core: URL, grant: DeviceCodeGrant) async throws -> String {
        // Core states both the interval and the lifetime, so neither is guessed. Polling faster than
        // asked is what earns a throttle; polling past the lifetime is asking about a code nobody can
        // approve any more.
        let interval = Duration.seconds(max(1, grant.intervalSeconds))
        let deadline = Date().addingTimeInterval(TimeInterval(grant.expiresInSeconds))

        while Date() < deadline {
            try await sleep(interval)
            try Task.checkCancellation()

            // Checked again after waiting: an interval longer than the time left would otherwise ask
            // about a code that expired while this was asleep.
            guard Date() < deadline else { break }

            switch try await client.poll(core: core, deviceCode: grant.deviceCode) {
            case .approved(let token): return token
            case .denied: throw PairingError.denied
            case .expired: throw PairingError.codeExpired
            case .pending: continue
            }
        }

        throw PairingError.codeExpired
    }

    /// What a viewer types is not a URL. `media.example.com`, a trailing slash, a stray scheme — all of
    /// it has to become one address or an honest refusal, and never a request to somewhere else.
    nonisolated static func normalise(_ address: String) -> URL? {
        let trimmed = address.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nil }

        let withScheme = trimmed.contains("://") ? trimmed : "\(assumedScheme(for: trimmed))://\(trimmed)"
        guard let components = URLComponents(string: withScheme),
              let host = components.host, !host.isEmpty,
              components.scheme == "http" || components.scheme == "https"
        else {
            return nil
        }

        var clean = URLComponents()
        clean.scheme = components.scheme
        clean.host = host
        clean.port = components.port
        // A path, a query or a fragment is not part of an address, and carrying one through would put
        // it in front of every route this client asks for.
        clean.path = ""
        return clean.url
    }

    /// What to assume when the viewer typed no scheme.
    ///
    /// `https` for anything with a name, because that is what a television should be asking for and
    /// nobody should have to type it on a remote control. But **a server on this network has no
    /// certificate and cannot get one** — there is no public name to issue it against — so an address
    /// here means `http`. Assuming otherwise made the ordinary self-hosted case simply unreachable:
    /// every attempt became a TLS handshake against a server speaking plain HTTP.
    private nonisolated static func assumedScheme(for address: String) -> String {
        // Parsed rather than split by hand. A stray port, path, query or fragment must not change
        // which network an address is on, and `//host` is the form that answers that in one step.
        let host = URLComponents(string: "//\(address)")?.host ?? address
        return isOnThisNetwork(host) ? "http" : "https"
    }

    /// Whether an address names something on the local network rather than out on the internet.
    ///
    /// The same question App Transport Security asks before it refuses a plain-HTTP connection, which is
    /// why the answers have to agree: an address this calls local is one the app is permitted to reach
    /// without TLS.
    nonisolated static func isOnThisNetwork(_ host: String) -> Bool {
        if host.lowercased().hasSuffix(".local") {
            return true
        }

        guard let octets = canonicalIPv4(host) else { return false }

        return switch (octets[0], octets[1]) {
        case (10, _), (127, _), (192, 168): true
        case (172, 16...31): true          // the private range nobody remembers the shape of
        case (169, 254): true              // link-local, for a host that never got an address
        default: false
        }
    }

    /// The four octets of an address written the one way everything agrees on, or nothing.
    ///
    /// Canonical decimal only, and that is a security property rather than tidiness. `010.0.0.1` is
    /// ten-dot-something to anything that parses it as a number, and **8.0.0.1** to the resolver that
    /// actually dials it, which reads a leading zero as octal. Believing the first reading would send
    /// this device's bearer token in the clear to a stranger's address while calling it local.
    ///
    /// So a spelling that is not plainly decimal is not classified at all — it falls through to
    /// `https`, which is the safe direction for a guess to fail in.
    private nonisolated static func canonicalIPv4(_ host: String) -> [Int]? {
        let parts = host.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 4 else { return nil }

        var octets: [Int] = []
        for part in parts {
            guard (1...3).contains(part.count),
                  part.allSatisfy({ $0.isASCII && $0.isNumber }),
                  part.count == 1 || part.first != "0",
                  let value = Int(part), value <= 255
            else {
                return nil
            }

            octets.append(value)
        }

        return octets
    }
}
