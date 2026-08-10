import Foundation

/// The HTTP the pairing chain needs, behind a protocol so the chain can be tested without a server.
public protocol HTTPTransport: Sendable {
    func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse)
}

/// `URLSession`, with cookies off.
///
/// Cookies are not isolated by port, so two Hosty hosts at one address on different ports would share a
/// jar and overwrite each other's sessions. Every credential here is attached by hand instead.
public struct URLSessionTransport: HTTPTransport {
    private let session: URLSession

    public init() {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.httpCookieStorage = nil
        configuration.httpShouldSetCookies = false
        configuration.timeoutIntervalForRequest = 15
        session = URLSession(configuration: configuration)
    }

    public func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else {
            throw PairingError.unreachable("The server did not answer over HTTP.")
        }

        return (data, http)
    }
}

/// The four calls that turn a typed-in address into a credential this app may use.
///
/// None of it is invented here. Core's device authorization flow issues the credential and the
/// app-identity exchange narrows it; the app writes no authentication of its own, which is the rule the
/// platform sets and the reason this is four calls rather than a login screen.
public struct PairingClient: Sendable {
    private let transport: any HTTPTransport

    public init(transport: any HTTPTransport = URLSessionTransport()) {
        self.transport = transport
    }

    // MARK: - 1. Who is answering

    /// Asks an address whether it is a Media Server, and where its Core is.
    public func bootstrap(server: URL) async throws -> ServerBootstrap {
        let request = URLRequest(url: server.appendingPathComponent("native/v1/server/public"))
        let (data, response) = try await send(request)

        guard response.statusCode == 200 else {
            // A 404 here is the ordinary shape of "some other web server lives at this address".
            throw PairingError.notAMediaServer
        }

        guard let bootstrap = try? JSONDecoder.pairing.decode(ServerBootstrap.self, from: data) else {
            throw PairingError.notAMediaServer
        }

        return bootstrap
    }

    // MARK: - 2. A code for a human to approve

    public func requestDeviceCode(core: URL, label: String) async throws -> DeviceCodeGrant {
        var request = URLRequest(url: core.appendingPathComponent("api/auth/device/code"))
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONEncoder().encode(["label": label])

        let (data, response) = try await send(request)

        // The device routes arrived in Core 0.73.0. Against an older host they are simply absent, and
        // saying "this host is too old" beats a bare 404 the viewer cannot act on.
        if response.statusCode == 404 {
            throw PairingError.coreTooOld
        }

        if response.statusCode == 429 {
            throw PairingError.throttled
        }

        guard response.statusCode == 200,
              let grant = try? JSONDecoder.pairing.decode(DeviceCodeGrant.self, from: data)
        else {
            throw error(from: data, status: response.statusCode)
        }

        return grant
    }

    // MARK: - 3. Waiting for it

    public func poll(core: URL, deviceCode: String) async throws -> DevicePollOutcome {
        var request = URLRequest(url: core.appendingPathComponent("api/auth/device/token"))
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONEncoder().encode(["deviceCode": deviceCode])

        let (data, response) = try await send(request)
        guard response.statusCode == 200,
              let answer = try? JSONDecoder.pairing.decode(DevicePollAnswer.self, from: data)
        else {
            throw error(from: data, status: response.statusCode)
        }

        switch answer.status {
        case "approved":
            // Approved with no token would be a Core bug, but a client that trusted it would loop for
            // ever on a request that has already been consumed.
            guard let token = answer.token else { throw PairingError.codeExpired }
            return .approved(token: token)
        case "pending": return .pending
        case "denied": return .denied
        default: return .expired
        }
    }

    // MARK: - 4. Narrowing it to this app

    /// Exchanges a Core access token for one audience-scoped to the app, in the two calls Core wants.
    ///
    /// The bearer form is what makes this work with no browser: a bearer-presented Core session is
    /// deliberately exempt from the CSRF check that would otherwise need a cookie and a header this
    /// client has no way to obtain.
    ///
    /// `redirectUri` is not followed and nothing navigates — the authorization code comes back in the
    /// body. It is still checked, and checked strictly: it must share an origin with one of the app's
    /// installed endpoints. The address the viewer typed is exactly that origin, which is why it is
    /// what gets sent.
    public func exchange(core: URL, appId: String, redirectUri: URL, coreToken: String) async throws -> AppIdentity {
        var authorize = URLRequest(url: core.appendingPathComponent("api/auth/apps/authorize"))
        authorize.httpMethod = "POST"
        authorize.setValue("application/json", forHTTPHeaderField: "Content-Type")
        authorize.setValue("Bearer \(coreToken)", forHTTPHeaderField: "Authorization")
        authorize.httpBody = try JSONEncoder().encode(
            ["appId": appId, "redirectUri": redirectUri.absoluteString])

        let (authorizeData, authorizeResponse) = try await send(authorize)
        guard authorizeResponse.statusCode == 200,
              let code = try? JSONDecoder.pairing.decode(AuthorizeAnswer.self, from: authorizeData).code
        else {
            throw error(from: authorizeData, status: authorizeResponse.statusCode)
        }

        var token = URLRequest(url: core.appendingPathComponent("api/auth/apps/token"))
        token.httpMethod = "POST"
        token.setValue("application/json", forHTTPHeaderField: "Content-Type")
        token.httpBody = try JSONEncoder().encode(["code": code])

        let (tokenData, tokenResponse) = try await send(token)
        guard tokenResponse.statusCode == 200,
              let identity = try? JSONDecoder.pairing.decode(TokenAnswer.self, from: tokenData)
        else {
            throw error(from: tokenData, status: tokenResponse.statusCode)
        }

        return AppIdentity(accessToken: identity.accessToken, expiresAt: identity.expiresAt)
    }

    // MARK: - Plumbing

    private func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        do {
            return try await transport.send(request)
        } catch let error as PairingError {
            throw error
        } catch {
            throw PairingError.unreachable(error.localizedDescription)
        }
    }

    /// Core answers failures with a code and a message. The two worth naming are named; the rest are
    /// carried through rather than flattened, because a reason a viewer can read beats "failed".
    private func error(from data: Data, status: Int) -> PairingError {
        guard let failure = try? JSONDecoder.pairing.decode(ErrorAnswer.self, from: data) else {
            return .server(code: "http_\(status)", message: "The server answered \(status).")
        }

        switch failure.error {
        case "user_not_assigned", "app_access_denied":
            return .notAssigned
        case "device_code_throttled":
            return .throttled
        default:
            return .server(code: failure.error, message: failure.message ?? "")
        }
    }

    private struct DevicePollAnswer: Decodable {
        let status: String
        let token: String?
    }

    private struct AuthorizeAnswer: Decodable {
        let code: String
    }

    private struct TokenAnswer: Decodable {
        let accessToken: String
        let expiresAt: Date
    }

    private struct ErrorAnswer: Decodable {
        let error: String
        let message: String?
    }
}

/// ISO-8601 as Core writes it, and as this reads it back.
///
/// A `FormatStyle` rather than an `ISO8601DateFormatter`: the formatter is a reference type that is not
/// `Sendable`, so sharing one is a concurrency error, and building one per call to avoid that would be
/// paying for a date parse with an allocation.
enum PairingDates {
    static let fractional = Date.ISO8601FormatStyle(includingFractionalSeconds: true)
    static let whole = Date.ISO8601FormatStyle()

    /// Core states fractional seconds; other things on the same wire do not. Accepting both costs one
    /// fallback and saves a class of failure that only shows up on a round second.
    static func parse(_ text: String) -> Date? {
        (try? fractional.parse(text)) ?? (try? whole.parse(text))
    }

    static func format(_ date: Date) -> String {
        fractional.format(date)
    }
}

extension JSONDecoder {
    /// Core writes camelCase and ISO-8601 with fractional seconds; `.iso8601` alone rejects those.
    static var pairing: JSONDecoder {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .custom { decoder in
            let container = try decoder.singleValueContainer()
            let text = try container.decode(String.self)
            guard let date = PairingDates.parse(text) else {
                throw DecodingError.dataCorruptedError(
                    in: container, debugDescription: "Not an ISO-8601 date: \(text)")
            }

            return date
        }

        return decoder
    }
}
