import MediaKit
import SwiftUI

/// The library, in the two shapes a viewer thinks in.
///
/// Catalogs are deliberately mixed rather than shown as a level of their own: whether a film sits on the
/// SSD or the spinning disk is an operator's concern, not a viewer's. `catalogId` travels on every item,
/// so a filter can be laid over this later without touching how any of it is loaded.
struct LibraryView: View {
    let session: ServerSession
    let pairing: PairingSession
    @State private var library: LibraryStore
    @Namespace private var libraryFocus
    @FocusState private var focusedMovie: String?

    init(session: ServerSession, pairing: PairingSession) {
        self.session = session
        self.pairing = pairing
        _library = State(initialValue: LibraryStore(session: session))
    }

    var body: some View {
        TabView {
            Tab("Home", systemImage: "house") {
                NavigationStack { HomeView(session: session, library: library) }
            }

            Tab("Movies", systemImage: "film") {
                shelf(library.movies, empty: "No films yet.")
            }

            Tab {
                shelf(library.series, empty: "No series yet.")
            } label: {
                Label {
                    Text("Series")
                } icon: {
                    // Let the tab bar tint every part of the symbol for appearance and focus.
                    Image(systemName: "tv")
                        .renderingMode(.template)
                        .symbolRenderingMode(.monochrome)
                }
            }

            Tab("Groups", systemImage: "folder") {
                NavigationStack { GroupsView(session: session, library: library) }
            }

            Tab("Collections", systemImage: "square.stack") {
                NavigationStack {
                    CollectionsView(session: session, library: library)
                }
            }

            // Sign out and the dynamic-range override live here. They were on the screen this replaced,
            // and a viewer with a dark picture and no way to change server or force SDR is worse off
            // than one who could never browse.
            Tab("Settings", systemImage: "gearshape") {
                SettingsView(paired: session.paired, pairing: pairing)
            }
        }
        .background(CinemaStyle.canvas)
        .task { await library.load() }
    }

    @ViewBuilder
    private func shelf(_ items: [LibraryTitle], empty: String) -> some View {
        switch library.state {
        case .idle, .loading:
            ProgressView("Reading the library")
        case .failed(let reason):
            VStack(spacing: 24) {
                Text("Could not read the library").font(.title)
                Text(reason)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .frame(maxWidth: 1200)
                Button("Try again") { Task { await library.load() } }
            }
        case .loaded where items.isEmpty:
            Text(empty).font(.title2).foregroundStyle(.secondary)
        case .loaded:
            NavigationStack {
                ScrollView {
                    VStack(alignment: .leading, spacing: 44) {
                        LibraryPosterGrid(items: items, library: library, loader: session.artwork,
                                          playback: PlaybackService(session: session), focus: $focusedMovie)
                    }.padding(CinemaStyle.inset)
                }
                .focusScope(libraryFocus)
            }
        }
    }
}
