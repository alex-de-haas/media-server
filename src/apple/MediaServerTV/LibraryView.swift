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
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    init(session: ServerSession, pairing: PairingSession) {
        self.session = session
        self.pairing = pairing
        _library = State(initialValue: LibraryStore(session: session))
    }

    var body: some View {
        TabView {
            Tab("Movies", systemImage: "film") {
                shelf(library.movies, continues: true, empty: "No films yet.")
            }

            Tab("Series", systemImage: "tv") {
                shelf(library.series, empty: "No series yet.")
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
    private func shelf(_ items: [LibraryTitle], continues: Bool = false, empty: String) -> some View {
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
                ScrollViewReader { scroll in
                    ScrollView {
                        VStack(alignment: .leading, spacing: 44) {
                            if continues && !library.continueWatching.isEmpty {
                                Text("Continue Watching").font(.title2.bold())
                                ScrollView(.horizontal) {
                                    LazyHStack(spacing: 48) {
                                        ForEach(library.continueWatching) { item in
                                            MoviePosterLink(item: item, library: library, loader: session.artwork,
                                                            playback: PlaybackService(session: session), showResumeTime: true)
                                                .frame(width: 250)
                                                .prefersDefaultFocus(item.id == library.continueWatching.first?.id, in: libraryFocus)
                                        }
                                    }.padding(.vertical, 30).padding(.horizontal, 20)
                                }
                                .scrollClipDisabled()
                                .focusSection()
                                .onMoveCommand { direction in
                                    guard direction == .down, let first = items.first else { return }
                                    // The lazy grid can be entirely below the viewport. Reveal its first
                                    // row before asking the focus engine to select an actual poster.
                                    withAnimation(reduceMotion ? nil : .easeInOut(duration: 0.2)) {
                                        scroll.scrollTo("all-titles", anchor: .top)
                                    } completion: {
                                        focusedMovie = "all-\(first.id)"
                                    }
                                }
                            }
                            Text(continues ? "All Movies" : "All Series").font(.title2.bold())
                                .id("all-titles")
                            LibraryPosterGrid(items: items, library: library, loader: session.artwork,
                                              playback: PlaybackService(session: session), focus: $focusedMovie)
                        }.padding(CinemaStyle.inset)
                    }
                    .focusScope(libraryFocus)
                }
            }
        }
    }
}
