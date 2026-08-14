import MediaKit
import SwiftUI

/// The library, in the two shapes a viewer thinks in.
///
/// Catalogs are deliberately mixed rather than shown as a level of their own: whether a film sits on the
/// SSD or the spinning disk is an operator's concern, not a viewer's. `catalogId` travels on every item,
/// so a filter can be laid over this later without touching how any of it is loaded.
struct LibraryView: View {
    let session: ServerSession
    @State private var library: LibraryStore

    init(session: ServerSession) {
        self.session = session
        _library = State(initialValue: LibraryStore(session: session))
    }

    var body: some View {
        TabView {
            Tab("Movies", systemImage: "film") {
                shelf(library.movies, empty: "No films yet.")
            }

            Tab("Series", systemImage: "tv") {
                shelf(library.series, empty: "No series yet.")
            }
        }
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
                PosterGrid(items: items, library: library, loader: session.artwork)
            }
        }
    }
}

/// A focus-driven grid, which is the only kind a television has.
private struct PosterGrid: View {
    let items: [LibraryTitle]
    let library: LibraryStore
    let loader: ArtworkLoader

    private let columns = [GridItem(.adaptive(minimum: 260, maximum: 320), spacing: 48)]

    var body: some View {
        ScrollView {
            LazyVGrid(columns: columns, spacing: 64) {
                ForEach(items) { item in
                    NavigationLink {
                        TitleView(title: item, library: library, loader: loader)
                    } label: {
                        PosterCell(item: item, server: library.server, loader: loader)
                    }
                    .buttonStyle(.card)
                }
            }
            .padding(60)
        }
    }
}

private struct PosterCell: View {
    let item: LibraryTitle
    let server: URL
    let loader: ArtworkLoader

    @State private var poster: Data?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            ZStack {
                if let poster, let image = UIImage(data: poster) {
                    Image(uiImage: image)
                        .resizable()
                        .aspectRatio(contentMode: .fill)
                } else {
                    // The same shape whether the artwork is still arriving or was never there, so the
                    // grid does not reflow under the viewer as posters land.
                    Rectangle()
                        .fill(.quaternary)
                        .overlay {
                            Image(systemName: item.kind == .movie ? "film" : "tv")
                                .font(.system(size: 48))
                                .foregroundStyle(.tertiary)
                        }
                }
            }
            .aspectRatio(2 / 3, contentMode: .fit)
            .clipShape(RoundedRectangle(cornerRadius: 12))
            .overlay(alignment: .bottom) { progress }

            Text(item.title)
                .font(.caption)
                .lineLimit(1)
            if let year = item.year {
                Text(String(year))
                    .font(.caption2)
                    .foregroundStyle(.secondary)
            }
        }
        .task {
            guard item.hasArtwork, let url = item.artworkURL(on: server) else { return }
            poster = await loader.image(at: url)
        }
    }

    /// Where the viewer got to. A finished title says so with a tick instead of a full bar, because a
    /// bar at 100 % reads as "nearly done".
    @ViewBuilder
    private var progress: some View {
        if item.played {
            Image(systemName: "checkmark.circle.fill")
                .font(.title2)
                .foregroundStyle(.white, .green)
                .padding(8)
                .frame(maxWidth: .infinity, alignment: .trailing)
        } else if item.resumeSeconds > 0 {
            Rectangle()
                .fill(.white)
                .frame(height: 6)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(.black.opacity(0.4))
        }
    }
}
