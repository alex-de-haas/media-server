import Foundation

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
            let bootstrap = try await client.bootstrap(server: paired.server)
            guard let core = URL(string: bootstrap.coreOrigin) else { throw PairingError.notAMediaServer }

            let identity = try await client.exchange(
                core: core, appId: paired.appId, redirectUri: paired.server, coreToken: paired.coreToken)

            var refreshed = paired
            refreshed.identity = identity
            store.save(refreshed)
            state = .paired(refreshed)
        } catch {
            // Core's token has gone too, or the user was unassigned. Either way this device is no longer
            // paired, and holding a credential that cannot be exchanged helps nobody.
            store.clear()
            state = .failed(error as? PairingError ?? .unreachable("\(error)"))
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
    public func cancel() {
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
            guard let core = URL(string: bootstrap.coreOrigin) else { throw PairingError.notAMediaServer }

            let grant = try await client.requestDeviceCode(core: core, label: label)
            state = .awaitingApproval(grant, serverName: bootstrap.serverName)

            let coreToken = try await awaitApproval(core: core, grant: grant)
            let identity = try await client.exchange(
                core: core, appId: bootstrap.appId, redirectUri: server, coreToken: coreToken)

            let paired = PairedServer(
                server: server,
                serverName: bootstrap.serverName,
                appId: bootstrap.appId,
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

        let withScheme = trimmed.contains("://") ? trimmed : "https://\(trimmed)"
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
}
