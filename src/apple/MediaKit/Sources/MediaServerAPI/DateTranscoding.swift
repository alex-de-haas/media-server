import Foundation
import OpenAPIRuntime

/// Reads the dates this server actually sends.
///
/// The generated client's default transcoder is `ISO8601DateFormatter` with `.withInternetDateTime` and
/// nothing else, which rejects any fractional seconds at all. .NET's `DateTimeOffset` serialises with up
/// to **seven** fractional digits — `2026-08-14T14:38:22.1234567+00:00` — so every response carrying a
/// date failed to decode, whole, with a `dataCorrupted` that named no field.
///
/// This is the same shape of mistake as the nullable references `NullableRefSchemaTransformer` fixes:
/// the generator's defaults and what .NET emits are not the same, and nothing in the pipeline compares
/// them. Neither showed up in a stubbed test, because the stubs were written to match the client.
///
/// Fixed here rather than on the server. The format .NET emits is valid ISO-8601 and valid JSON Schema
/// `date-time`; it is the reader that is too narrow, and narrowing what the server sends to suit one
/// client would be the wrong way round — the Jellyfin surface and the web UI read these dates too.
public struct LenientDateTranscoder: DateTranscoder {
    /// Three digits is what every ISO-8601 reader accepts, so that is what is sent.
    private static let outgoing = Date.ISO8601FormatStyle(includingFractionalSeconds: true)
    private static let incoming = Date.ISO8601FormatStyle(includingFractionalSeconds: true)
    private static let withoutFraction = Date.ISO8601FormatStyle()

    public init() {}

    public func encode(_ date: Date) throws -> String {
        Self.outgoing.format(date)
    }

    public func decode(_ string: String) throws -> Date {
        if let date = try? Self.incoming.parse(Self.normalised(string)) {
            return date
        }

        if let date = try? Self.withoutFraction.parse(string) {
            return date
        }

        throw DecodingError.dataCorrupted(.init(
            codingPath: [], debugDescription: "Not a date this client can read: \(string)"))
    }

    /// Trims the fractional part to three digits, which is the most any Foundation parser will take.
    ///
    /// Done by hand rather than with a formatter option because there is no option for it: the choice is
    /// between three digits and none, and a server sending seven would otherwise be unreadable.
    private static func normalised(_ string: String) -> String {
        guard let dot = string.firstIndex(of: ".") else { return string }

        let afterDot = string.index(after: dot)
        guard let end = string[afterDot...].firstIndex(where: { !$0.isNumber }) else {
            // No zone suffix: fractional digits run to the end.
            let digits = string[afterDot...].prefix(3)
            return String(string[..<afterDot]) + String(digits)
        }

        let digits = string[afterDot..<end].prefix(3)
        return String(string[..<afterDot]) + String(digits) + String(string[end...])
    }
}
