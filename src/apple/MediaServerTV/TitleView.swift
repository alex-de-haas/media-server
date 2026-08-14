import MediaKit
import SwiftUI

/// One title, with everything a viewer needs before pressing play.
///
/// Versions come first among the details because they are the only choice that changes what is played
/// rather than how — a 4K copy and a 1080p one are different films as far as an evening is concerned.
struct TitleView: View {
    let title: LibraryTitle
    let library: LibraryStore
    let loader: ArtworkLoader

    @State private var detail: TitleDetail?
    @State private var failure: String?
    @State private var chosenVersion: String?

    var body: some View {
        ScrollView {
            if let detail {
                loaded(detail)
            } else if let failure {
                VStack(spacing: 16) {
                    Text("Could not open this title").font(.title)
                    Text(failure).font(.callout).foregroundStyle(.secondary)
                }
                .padding(80)
            } else {
                ProgressView().padding(120)
            }
        }
        .task {
            do {
                let loaded = try await library.detail(for: title.id)
                detail = loaded
                chosenVersion = loaded.versions.first?.id
            } catch {
                failure = String(describing: error)
            }
        }
    }

    @ViewBuilder
    private func loaded(_ detail: TitleDetail) -> some View {
        VStack(alignment: .leading, spacing: 40) {
            VStack(alignment: .leading, spacing: 12) {
                Text(detail.title).font(.largeTitle)

                if let tagline = detail.tagline, !tagline.isEmpty {
                    Text(tagline).font(.title3).foregroundStyle(.secondary).italic()
                }

                Text(facts(detail))
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }

            if let overview = detail.overview, !overview.isEmpty {
                Text(overview)
                    .font(.body)
                    .frame(maxWidth: 1400, alignment: .leading)
            }

            // Playback is the next phase. The button is here because its absence would read as a bug,
            // and it says plainly that it does nothing yet rather than failing when pressed.
            Button {
            } label: {
                Label(detail.resumeSeconds > 0 ? "Resume" : "Play", systemImage: "play.fill")
            }
            .disabled(true)

            if detail.versions.count > 1 {
                versions(detail.versions)
            }

            if let version = detail.versions.first(where: { $0.id == chosenVersion }) ?? detail.versions.first {
                tracks(version)
            }
        }
        .padding(80)
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func facts(_ detail: TitleDetail) -> String {
        var parts: [String] = []
        if let year = detail.year { parts.append(String(year)) }
        if let runtime = detail.runtimeSeconds, runtime > 0 {
            parts.append("\(Int(runtime / 60)) min")
        }
        if let rating = detail.officialRating { parts.append(rating) }
        if let community = detail.communityRating {
            parts.append(String(format: "★ %.1f", community))
        }

        parts.append(contentsOf: detail.genres.prefix(3))
        return parts.joined(separator: " · ")
    }

    @ViewBuilder
    private func versions(_ versions: [TitleVersion]) -> some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Versions").font(.title2)

            ForEach(versions) { version in
                Button {
                    chosenVersion = version.id
                } label: {
                    HStack {
                        Image(systemName: version.id == chosenVersion ? "checkmark.circle.fill" : "circle")
                        VStack(alignment: .leading) {
                            Text(version.versionName ?? version.container.uppercased())
                            Text("\(version.container.uppercased()) · \(version.sizeDescription)")
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }
                        Spacer()
                    }
                }
            }
        }
    }

    @ViewBuilder
    private func tracks(_ version: TitleVersion) -> some View {
        HStack(alignment: .top, spacing: 80) {
            trackList("Audio", version.audio)
            trackList("Subtitles", version.subtitles)
        }
    }

    @ViewBuilder
    private func trackList(_ heading: String, _ tracks: [TitleTrack]) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(heading).font(.title3)

            if tracks.isEmpty {
                Text("None").foregroundStyle(.secondary)
            } else {
                ForEach(tracks) { track in
                    HStack(spacing: 8) {
                        Text(track.label.isEmpty ? "Track" : track.label)
                        // A dub or a subtitle file beside the video is the thing this library holds and
                        // no other client of it can play, so it is worth pointing at.
                        if track.isExternal {
                            Image(systemName: "doc.badge.plus")
                                .foregroundStyle(.secondary)
                                .help("Beside the file")
                        }
                    }
                    .font(.callout)
                }
            }
        }
    }
}
