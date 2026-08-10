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

/// Paired or not. There is nothing else yet — browsing and playback are the next phases of
/// `docs/features/apple-client-core/plan.md`.
struct RootView: View {
    let session: PairingSession

    var body: some View {
        if case .paired(let paired) = session.state {
            PairedView(paired: paired, session: session)
        } else {
            PairingView(session: session)
        }
    }
}
