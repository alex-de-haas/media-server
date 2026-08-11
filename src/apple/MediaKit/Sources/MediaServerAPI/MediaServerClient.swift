import Foundation
import HTTPTypes
import OpenAPIRuntime
import OpenAPIURLSession

/// Builds the generated client against a server, with the app identity token attached.
///
/// Everything else in this target is generated from `src/api/openapi/MediaServer.Api_native.json` and
/// committed beside it, so a change to the server's surface is a compile error here rather than a
/// decoding failure on a television.
public enum MediaServerAPIClient {
    public static func make(
        server: URL,
        token: String?,
        transport: (any ClientTransport)? = nil
    ) -> Client {
        Client(
            serverURL: server,
            transport: transport ?? URLSessionTransport(configuration: .init(session: session())),
            middlewares: token.map { [BearerMiddleware(token: $0)] } ?? [])
    }

    /// Cookies off, for the same reason the pairing client turns them off: they are not isolated by
    /// port, so two Hosty hosts at one address would share a jar.
    private static func session() -> URLSession {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.httpCookieStorage = nil
        configuration.httpShouldSetCookies = false
        // Bounded, because the first call this client ever makes is against an address a viewer just
        // typed. A host that silently drops traffic would otherwise hold the screen on "Looking for a
        // server" for the platform default of a minute.
        configuration.timeoutIntervalForRequest = 15
        return URLSession(configuration: configuration)
    }
}

/// Attaches the app identity token, which is the only credential that ever reaches the media server.
struct BearerMiddleware: ClientMiddleware {
    let token: String

    func intercept(
        _ request: HTTPRequest,
        body: HTTPBody?,
        baseURL: URL,
        operationID: String,
        next: (HTTPRequest, HTTPBody?, URL) async throws -> (HTTPResponse, HTTPBody?)
    ) async throws -> (HTTPResponse, HTTPBody?) {
        var authorised = request
        authorised.headerFields[.authorization] = "Bearer \(token)"
        return try await next(authorised, body, baseURL)
    }
}
