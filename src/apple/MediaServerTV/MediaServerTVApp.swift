import MediaKit
import SwiftUI

@main
struct MediaServerTVApp: App {
    @State private var session = PairingSession()

    var body: some Scene {
        WindowGroup {
            RootView(session: session)
                .task { await session.restore() }
        }
    }
}

/// Paired or not. Playback is the next phase of `docs/features/apple-client-core/plan.md`.
struct RootView: View {
    let session: PairingSession

    var body: some View {
        if case .paired(let paired) = session.state {
            LibraryView(session: ServerSession(paired: paired))
        } else {
            PairingView(session: session)
        }
    }
}
