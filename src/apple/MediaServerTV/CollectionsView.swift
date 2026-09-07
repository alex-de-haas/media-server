import MediaKit
import SwiftUI

struct CollectionsView: View {
    let session: ServerSession
    let library: LibraryStore
    @State private var store: CollectionStore
    @State private var notice: String?

    init(session: ServerSession, library: LibraryStore) {
        self.session = session
        self.library = library
        _store = State(initialValue: CollectionStore(session: session))
    }

    var body: some View {
        Group {
            switch store.state {
            case .loading: ProgressView("Reading collections")
            case .unsupported:
                VStack(spacing: 24) {
                    ContentUnavailableView("Update your server", systemImage: "arrow.up.circle",
                        description: Text("This server does not support native collections yet."))
                    Button("Try again") { Task { await store.load() } }
                }
            case .failed(let reason):
                VStack(spacing: 24) {
                    Text("Could not read collections").font(.title)
                    Text(reason).foregroundStyle(.secondary)
                    Button("Try again") { Task { await store.load() } }
                }
            case .loaded:
                if store.items.isEmpty {
                    ContentUnavailableView("No collections yet", systemImage: "square.stack",
                        description: Text("Collections appear when your library contains at least two movies in a franchise."))
                } else {
                    ScrollView {
                        VStack(alignment: .leading, spacing: 40) {
                            Text("Collections").font(.largeTitle.bold())
                            LazyVGrid(columns: CinemaStyle.columns, spacing: 54) {
                                ForEach(store.items) { collection in
                                    PosterCard(title: collection.name, subtitle: "\(collection.itemCount) movies") {
                                        ServerArtwork(url: collection.posterPath.flatMap {
                                            URL(string: $0, relativeTo: library.server)
                                        }, loader: session.artwork, symbol: "square.stack")
                                    } destination: {
                                        CollectionDetailView(id: collection.id, store: store,
                                            session: session, library: library) {
                                                notice = "That collection is no longer available."
                                                Task { await store.load() }
                                            }
                                    }
                                }
                            }
                        }.padding(CinemaStyle.inset)
                    }
                }
            }
        }
        .task { await store.load() }
        .alert("Collection unavailable", isPresented: Binding(
            get: { notice != nil }, set: { if !$0 { notice = nil } }
        )) {
            Button("OK") { notice = nil }
        } message: { Text(notice ?? "") }
    }
}

private struct CollectionDetailView: View {
    let id: String
    let store: CollectionStore
    let session: ServerSession
    let library: LibraryStore
    let onMissing: () -> Void
    @Environment(\.colorScheme) private var systemColorScheme
    @Environment(\.dismiss) private var dismiss
    @State private var detail: MovieCollectionDetail?
    @State private var failure: String?

    var body: some View {
        ScrollView {
            if let detail {
                VStack(alignment: .leading, spacing: 50) {
                    VStack(alignment: .leading, spacing: 16) {
                        Text(detail.name).font(.system(size: 64, weight: .bold))
                        Text("\(detail.items.count) movies · In release order").foregroundStyle(.secondary)
                    }.padding(.vertical, 70)
                    LibraryPosterGrid(items: detail.items, library: library, loader: session.artwork,
                                      playback: PlaybackService(session: session))
                }.padding(CinemaStyle.inset)
            } else if let failure {
                VStack(spacing: 24) {
                    Text("Could not open this collection").font(.title)
                    Text(failure).foregroundStyle(.secondary)
                    Button("Try again") { Task { await load() } }
                }.padding(CinemaStyle.inset)
            } else { ProgressView().padding(120) }
        }
        .background(alignment: .top) {
            CinemaBackdrop(url: detail?.backdropPath.flatMap { URL(string: $0, relativeTo: library.server) },
                           loader: session.artwork).frame(height: 700).ignoresSafeArea()
        }
        .background(detail?.backdropPath != nil ? Color.black : CinemaStyle.canvas)
        .environment(\.colorScheme, detail?.backdropPath != nil ? .dark : systemColorScheme)
        .task { await load() }
    }

    private func load() async {
        failure = nil
        do { detail = try await store.detail(id: id) }
        catch CollectionError.missing { onMissing(); dismiss() }
        catch { failure = error.localizedDescription }
    }
}
