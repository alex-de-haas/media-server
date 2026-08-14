import Foundation
import HTTPTypes
import MediaServerAPI
import Observation
import OpenAPIRuntime

/// A paired server, with a credential that keeps itself alive.
///
/// The app grant states an `expiresAt` thirty days out — its *absolute* cap — but it also lapses after
/// seven days idle, and nothing in the token says which of the two will come first. So a television left
/// alone for a week holds a credential that looks fresh and is not. A clock cannot tell; only a request
/// can, and the answer is a `401`.
///
/// That is why the refresh lives in a middleware rather than in a check before each call: the retry is
/// then invisible to everything above it, and there is exactly one place that knows how to re-mint.
@MainActor
@Observable
public final class ServerSession {
    public private(set) var paired: PairedServer

    private let store: any CredentialStore
    private let pairing: PairingClient
    private let transport: (any ClientTransport)?

    /// Serialises re-minting, so five requests failing together produce one exchange rather than five.
    private var refreshing: Task<String?, Never>?

    /// One loader for the whole app, so the poster grid's cache survives moving between tabs. Not
    /// observed: it is a service, and nothing redraws because a cache filled.
    @ObservationIgnored
    public private(set) var artwork: ArtworkLoader!

    public init(
        paired: PairedServer,
        store: any CredentialStore = KeychainCredentialStore(),
        pairing: PairingClient = PairingClient(),
        transport: (any ClientTransport)? = nil
    ) {
        self.paired = paired
        self.store = store
        self.pairing = pairing
        self.transport = transport

        // Built here rather than lazily: every stored property is set, so capturing self is legal, and
        // `@Observable` has no lazy of its own.
        artwork = ArtworkLoader(token: { [weak self] in await self?.tokenForArtwork() })
    }

    nonisolated private func tokenForArtwork() async -> String? {
        await MainActor.run { self.paired.identity.accessToken }
    }

    /// The generated client, pointed at this server and carrying the current credential.
    public func api() -> Client {
        MediaServerAPIClient.make(
            server: paired.server,
            token: nil,
            transport: transport,
            middlewares: [RefreshingBearerMiddleware(
                token: { [weak self] in await self?.currentToken() },
                refresh: { [weak self] in await self?.refreshToken() })])
    }

    private func currentToken() -> String? {
        paired.identity.accessToken
    }

    /// Re-mints the app grant from the Core token, once, however many callers ask at the same moment.
    private func refreshToken() async -> String? {
        if let inFlight = refreshing {
            return await inFlight.value
        }

        let task = Task<String?, Never> { [paired, pairing, store] in
            do {
                let identity = try await pairing.exchange(
                    core: paired.coreOrigin,
                    appId: paired.appId,
                    redirectUri: paired.server,
                    coreToken: paired.coreToken)

                var refreshed = paired
                refreshed.identity = identity
                store.save(refreshed)
                return identity.accessToken
            } catch {
                // Core's own token has gone, or this account lost its assignment. Either way there is
                // nothing to retry with; the caller sees the 401 it already had.
                return nil
            }
        }

        refreshing = task
        let token = await task.value
        refreshing = nil

        if let token {
            var refreshed = paired
            refreshed.identity = AppIdentity(
                accessToken: token, expiresAt: paired.identity.expiresAt)
            paired = store.load() ?? refreshed
        }

        return token
    }
}

/// Attaches the app identity token, and re-mints it once when the server says it is no longer good.
struct RefreshingBearerMiddleware: ClientMiddleware {
    let token: @Sendable () async -> String?
    let refresh: @Sendable () async -> String?

    func intercept(
        _ request: HTTPRequest,
        body: HTTPBody?,
        baseURL: URL,
        operationID: String,
        next: (HTTPRequest, HTTPBody?, URL) async throws -> (HTTPResponse, HTTPBody?)
    ) async throws -> (HTTPResponse, HTTPBody?) {
        var authorised = request
        if let token = await token() {
            authorised.headerFields[.authorization] = "Bearer \(token)"
        }

        let (response, responseBody) = try await next(authorised, body, baseURL)

        // Only a request with nothing to send is retried. An `HTTPBody` is a stream and is consumed by
        // the attempt that failed, so replaying one would send an empty body and call it a retry.
        guard response.status == .unauthorized, body == nil, let fresh = await refresh() else {
            return (response, responseBody)
        }

        var retry = request
        retry.headerFields[.authorization] = "Bearer \(fresh)"
        return try await next(retry, nil, baseURL)
    }
}
