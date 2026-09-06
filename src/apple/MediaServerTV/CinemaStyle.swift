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
    var fallbackTitle: String?
    @State private var image: Image?

    var body: some View {
        GeometryReader { geometry in
            ZStack {
                Rectangle().fill(.primary.opacity(0.08))
                if let image {
                    image.resizable().scaledToFill()
                        .frame(width: geometry.size.width, height: geometry.size.height)
                } else if let fallbackTitle {
                    Text(fallbackTitle)
                        .font(.title3.weight(.semibold))
                        .multilineTextAlignment(.center)
                        .lineLimit(6)
                        .minimumScaleFactor(0.7)
                        .padding(24)
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
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
            if url != nil {
                Color.black
                ServerArtwork(url: url, loader: loader)
                LinearGradient(stops: [
                    .init(color: .black.opacity(0.85), location: 0),
                    .init(color: .black.opacity(0.65), location: 0.32),
                    .init(color: .black.opacity(0.15), location: 0.65),
                    .init(color: .clear, location: 1),
                ], startPoint: .leading, endPoint: .trailing)
                LinearGradient(stops: [
                    .init(color: .clear, location: 0),
                    .init(color: .clear, location: 0.4),
                    .init(color: .black.opacity(0.6), location: 0.75),
                    .init(color: .black, location: 1),
                ], startPoint: .top, endPoint: .bottom)
                if contrast == .increased { Color.black.opacity(0.25) }
            } else {
                CinemaStyle.canvas
            }
        }
        .accessibilityHidden(true)
    }
}

/// Artwork and metadata move together, while only the poster receives a shadow.
struct PosterCard<Artwork: View, Destination: View>: View {
    let title: String
    let subtitle: String
    var status = ""
    var showTitle = true
    var focus: FocusState<String?>.Binding? = nil
    var focusID = ""
    @ViewBuilder var artwork: () -> Artwork
    @ViewBuilder var destination: () -> Destination

    var body: some View {
        Group {
            if let focus {
                posterLink.focused(focus, equals: focusID)
            } else {
                posterLink
            }
        }.focusSection()
    }

    private var posterLink: some View {
        NavigationLink(destination: destination) {
            VStack(alignment: .leading, spacing: 10) {
                artwork()
                    .aspectRatio(2 / 3, contentMode: .fit)
                    .clipShape(RoundedRectangle(cornerRadius: 14))
                    .shadow(color: .black.opacity(0.15), radius: 8, y: 4)
                if showTitle { Text(title).font(.caption).lineLimit(2) }
                Text(subtitle.isEmpty ? " " : subtitle)
                    .font(.caption).foregroundStyle(.secondary).lineLimit(1)
                    .minimumScaleFactor(0.65)
                    .padding(.horizontal, 4)
            }
        }
        .buttonStyle(PosterFocusStyle())
        .accessibilityLabel([title, subtitle, status].filter { !$0.isEmpty }.joined(separator: ", "))
    }
}

private struct PosterFocusStyle: ButtonStyle {

    func makeBody(configuration: Configuration) -> some View {
        FocusedPoster(configuration: configuration)
    }

    private struct FocusedPoster: View {
        let configuration: ButtonStyleConfiguration
        @Environment(\.isFocused) private var isFocused
        @Environment(\.accessibilityReduceMotion) private var reduceMotion

        var body: some View {
            configuration.label
                .scaleEffect(isFocused ? 1.06 : 1)
                .opacity(configuration.isPressed ? 0.8 : 1)
                .animation(reduceMotion ? nil : .easeOut(duration: 0.18), value: isFocused)
        }
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
                   subtitle: showResumeTime ? "Resume from \(PlaybackPosition.label(item.resumeSeconds))" : item.gridSubtitle,
                   status: item.played ? "Watched" : item.resumeSeconds > 0 ? "In progress" : "",
                   showTitle: false, focus: focus, focusID: focusID) {
            ServerArtwork(url: item.hasArtwork ? item.artworkURL(on: library.server) : nil,
                          loader: loader, symbol: item.kind == .movie ? "film" : "tv", fallbackTitle: item.title)
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
        let currentItems = Dictionary(library.items.map { ($0.id, $0) },
                                      uniquingKeysWith: { first, _ in first })
        LazyVGrid(columns: CinemaStyle.columns, spacing: 40) {
            ForEach(items) { original in
                let item = currentItems[original.id] ?? original
                MoviePosterLink(item: item, library: library, loader: loader, playback: playback,
                                focus: focus, focusID: "all-\(item.id)")
            }
        }
        .focusSection()
    }
}
