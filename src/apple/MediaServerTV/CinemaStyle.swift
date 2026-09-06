import MediaKit
import SwiftUI

enum CinemaStyle {
    static let canvas = Color(uiColor: UIColor { traits in
        traits.userInterfaceStyle == .dark
            ? UIColor(red: 0.055, green: 0.063, blue: 0.078, alpha: 1)
            : UIColor(red: 0.96, green: 0.965, blue: 0.975, alpha: 1)
    })
    static let inset: CGFloat = 70
    static let columns = [GridItem(.adaptive(minimum: 250, maximum: 300), spacing: 48)]
}

/// Decodes once per URL; placeholders keep the layout stable while the image loads.
struct ServerArtwork: View {
    let url: URL?
    let loader: ArtworkLoader
    var symbol = "film"
    @State private var image: Image?

    var body: some View {
        GeometryReader { geometry in
            ZStack {
                Rectangle().fill(.primary.opacity(0.08))
                if let image {
                    image.resizable().scaledToFill()
                        .frame(width: geometry.size.width, height: geometry.size.height)
                } else {
                    Image(systemName: symbol).font(.system(size: 48)).foregroundStyle(.secondary)
                }
            }
            .frame(width: geometry.size.width, height: geometry.size.height)
            .clipped()
        }
        .accessibilityHidden(true)
        .task(id: url) {
            image = nil
            guard let url, let data = await loader.image(at: url), !Task.isCancelled,
                  let decoded = UIImage(data: data) else { return }
            image = Image(uiImage: decoded)
        }
    }
}

struct CinemaBackdrop: View {
    let url: URL?
    let loader: ArtworkLoader
    @Environment(\.colorSchemeContrast) private var contrast

    var body: some View {
        ZStack {
            CinemaStyle.canvas
            if url != nil {
                ServerArtwork(url: url, loader: loader)
                LinearGradient(colors: [CinemaStyle.canvas.opacity(0.96), CinemaStyle.canvas.opacity(0.2)],
                               startPoint: .leading, endPoint: .trailing)
                LinearGradient(colors: [.clear, CinemaStyle.canvas], startPoint: .top, endPoint: .bottom)
                if contrast == .increased { CinemaStyle.canvas.opacity(0.35) }
            }
        }
        .accessibilityHidden(true)
    }
}

/// Only the artwork is the focusable card. Captions sit outside its border and shadow.
struct PosterCard<Artwork: View, Destination: View>: View {
    let title: String
    let subtitle: String
    var status = ""
    var focus: FocusState<String?>.Binding? = nil
    var focusID = ""
    @ViewBuilder var artwork: () -> Artwork
    @ViewBuilder var destination: () -> Destination

    var body: some View {
        VStack(alignment: .leading, spacing: 34) {
            if let focus {
                posterLink.focused(focus, equals: focusID)
            } else {
                posterLink
            }

            VStack(alignment: .leading, spacing: 8) {
                Text(title).font(.headline).lineLimit(2, reservesSpace: true)
                Text(subtitle.isEmpty ? " " : subtitle)
                    .font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
            .padding(.horizontal, 10)
            .padding(.bottom, 8)
            .accessibilityHidden(true)
        }
        // Include the caption's space in directional navigation without decorating or focusing it.
        .focusSection()
    }

    private var posterLink: some View {
        NavigationLink(destination: destination) {
            artwork()
                .aspectRatio(2 / 3, contentMode: .fit)
                .clipShape(RoundedRectangle(cornerRadius: 14))
        }
        .buttonStyle(.card)
        .accessibilityLabel([title, subtitle, status].filter { !$0.isEmpty }.joined(separator: ", "))
    }
}

struct MoviePosterLink: View {
    let item: LibraryTitle
    let library: LibraryStore
    let loader: ArtworkLoader
    let playback: PlaybackService
    var showResumeTime = false
    var focus: FocusState<String?>.Binding? = nil
    var focusID = ""

    var body: some View {
        PosterCard(title: item.title,
                   subtitle: showResumeTime ? "Resume from \(PlaybackPosition.label(item.resumeSeconds))" : item.year.map(String.init) ?? "",
                   status: item.played ? "Watched" : item.resumeSeconds > 0 ? "In progress" : "",
                   focus: focus, focusID: focusID) {
            ServerArtwork(url: item.hasArtwork ? item.artworkURL(on: library.server) : nil,
                          loader: loader, symbol: item.kind == .movie ? "film" : "tv")
                .overlay(alignment: .bottomTrailing) {
                    if item.played || item.resumeSeconds > 0 {
                        Image(systemName: item.played ? "checkmark" : "play.fill")
                            .font(.caption.weight(.bold)).foregroundStyle(.white).padding(12)
                            .background(.black.opacity(0.8), in: Circle()).padding(12)
                            .accessibilityHidden(true)
                    }
                }
        } destination: {
            TitleView(title: item, library: library, loader: loader, playback: playback)
        }
    }
}

struct LibraryPosterGrid: View {
    let items: [LibraryTitle]
    let library: LibraryStore
    let loader: ArtworkLoader
    let playback: PlaybackService
    var focus: FocusState<String?>.Binding? = nil

    var body: some View {
        LazyVGrid(columns: CinemaStyle.columns, spacing: 54) {
            ForEach(items) { original in
                let item = library.items.first { $0.id == original.id } ?? original
                MoviePosterLink(item: item, library: library, loader: loader, playback: playback,
                                focus: focus, focusID: "all-\(item.id)")
            }
        }
        .focusSection()
    }
}
