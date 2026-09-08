import MediaKit
import SwiftUI

/// A place to choose what to watch now; the other tabs remain complete browsing grids.
struct HomeView: View {
    let session: ServerSession
    let library: LibraryStore
    @State private var home: HomeStore
    @Environment(\.scenePhase) private var scenePhase
    @Namespace private var homeFocus

    init(session: ServerSession, library: LibraryStore) {
        self.session = session
        self.library = library
        _home = State(initialValue: HomeStore(session: session))
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 44) {
                ForEach(HomeSection.allCases, id: \.self) { section in
                    rail(section)
                }
            }
            .padding(CinemaStyle.inset)
        }
        .focusScope(homeFocus)
        .task { await home.load() }
        .onChange(of: scenePhase) { _, phase in
            if phase == .active { Task { await home.load() } }
        }
    }

    @ViewBuilder
    private func rail(_ section: HomeSection) -> some View {
        let rail = home.rails[section] ?? HomeRail()
        VStack(alignment: .leading, spacing: 20) {
            Text(section.title).font(.title2.bold())
            if !rail.items.isEmpty {
                ScrollView(.horizontal) {
                    LazyHStack(alignment: .top, spacing: 48) {
                        ForEach(rail.items) { card in
                            PosterCard(title: card.destination.title, subtitle: card.subtitle, showTitle: false) {
                                ServerArtwork(url: card.artworkURL(on: library.server),
                                    loader: session.artwork,
                                    symbol: card.destination.kind == .movie ? "film" : "tv",
                                    fallbackTitle: card.destination.title)
                            } destination: {
                                TitleView(title: card.destination, library: library,
                                          loader: session.artwork, playback: PlaybackService(session: session))
                            }
                            .frame(width: 250)
                            .prefersDefaultFocus(section == .continueWatching && card.id == rail.items.first?.id,
                                                 in: homeFocus)
                        }
                    }.padding(.horizontal, 20).padding(.vertical, 24)
                }
                .scrollClipDisabled()
            }
            switch rail.state {
            case .idle, .loading:
                if rail.items.isEmpty { ProgressView("Loading") }
            case .failed(let message):
                HStack(spacing: 24) {
                    Text(message).foregroundStyle(.secondary)
                    Button("Try again") { Task { await home.load(section) } }
                }
            case .unsupported:
                Text("Update the server to use this row.").foregroundStyle(.secondary)
            case .loaded:
                if rail.items.isEmpty { Text(emptyMessage(section)).foregroundStyle(.secondary) }
            }
        }
        .focusSection()
    }

    private func emptyMessage(_ section: HomeSection) -> String {
        switch section {
        case .continueWatching: "Start a film or episode from Movies or Series to continue it here."
        case .nextUp: "Your next episode appears here after you start watching a series."
        case .recommendations: "No recommendations from your library yet."
        }
    }
}
