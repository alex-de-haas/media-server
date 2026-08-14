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
            PairedRoot(paired: paired, pairing: session)
        } else {
            PairingView(session: session)
        }
    }
}

/// Holds the authenticated session for as long as the pairing lasts, and gives up on it when the server
/// says the credential is gone for good.
///
/// The signal matters because the stored grant's absolute expiry can still be weeks away: without it the
/// next launch would restore the same dead credential and fail in exactly the same way, for ever.
private struct PairedRoot: View {
    let paired: PairedServer
    let pairing: PairingSession

    @State private var server: ServerSession

    init(paired: PairedServer, pairing: PairingSession) {
        self.paired = paired
        self.pairing = pairing
        _server = State(initialValue: ServerSession(paired: paired))
    }

    var body: some View {
        LibraryView(session: server, pairing: pairing)
            .onChange(of: server.credentialLost) { _, lost in
                if lost { pairing.unpair() }
            }
    }
}
