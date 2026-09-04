import Foundation

/// What a loading request still wants, as a concrete range of the resource.
///
/// AVFoundation states a request as an offset and a length — or "everything to the end" — and then
/// advances `currentOffset` as it is answered, so the range still owed is a moving thing. Getting it
/// wrong is not "plays worse": a byte past the end, or one short, is a request that never finishes.
public enum LoadRange {
    /// The bytes owed to a request that began at `requestedOffset`, has been answered up to
    /// `current`, and asked for `requestedLength` bytes or everything after it. Nil when nothing is
    /// owed — the request has been answered, or it began past the end of the resource.
    public static func owed(
        current: Int64,
        requestedOffset: Int64,
        requestedLength: Int,
        toEnd: Bool,
        total: Int64
    ) -> Range<Int64>? {
        let end = toEnd ? total : min(total, requestedOffset + Int64(requestedLength))
        guard current >= 0, current < end else { return nil }
        return current ..< end
    }
}
