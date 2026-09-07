import AVFoundation

/// Applies the server's subtitle choice to a remux item, which carries at most one text track.
public enum RemuxSubtitles {
    /// Explicit selection overrides automatic language preferences, including system subtitles Off.
    @MainActor
    public static func apply(to item: AVPlayerItem, enabled: Bool) async throws {
        let group = try await item.asset.loadMediaSelectionGroup(for: .legible)
        try Task.checkCancellation()
        guard let group else { return }
        item.select(enabled ? group.options.first : nil, in: group)
    }
}
