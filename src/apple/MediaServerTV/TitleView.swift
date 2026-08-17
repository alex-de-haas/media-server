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
    let playback: PlaybackService

    @State private var detail: TitleDetail?
    @State private var failure: String?
    @State private var chosenVersion: String?

    @State private var plan: PlaybackPlan?
    @State private var playing: PlayableStream?
    @State private var session: String?
    @State private var resolving = false

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
        .fullScreenCover(item: $playing) { stream in
            PlayerView(
                stream: stream,
                startAt: detail?.resumeSeconds ?? 0,
                diagnostics: PlaybackPreferencesStore().load().showDiagnostics
                    ? PlaybackDiagnostics() : nil,
                onProgress: { position in
                    guard let session else { return }
                    Task { await playback.report(
                        itemId: title.id, playSessionId: session, positionSeconds: position) }
                },
                onFinished: { position in
                    guard let session else { return }
                    Task { await playback.stop(
                        itemId: title.id, playSessionId: session, positionSeconds: position) }
                    self.session = nil
                })
            .ignoresSafeArea()
        }
    }

    @ViewBuilder
    private func playButton(_ detail: TitleDetail) -> some View {
        Button {
            Task { await play(detail) }
        } label: {
            if resolving {
                ProgressView()
            } else {
                Label(detail.resumeSeconds > 0 ? "Resume" : "Play", systemImage: "play.fill")
            }
        }
        .disabled(resolving)
    }

    private func play(_ detail: TitleDetail) async {
        resolving = true
        defer { resolving = false }

        do {
            // The version the viewer picked, not whichever the server listed first. A picker that
            // changes what is listed and not what happens is worse than no picker at all.
            let answer = try await playback.plan(for: title.id, preferring: chosenVersion)
            plan = answer

            guard case .play(let stream) = answer else { return }

            // Best effort, and deliberately so. Opening it first means a viewer who stops after ten
            // seconds still leaves a record of having started — but a server that will not open one is
            // no reason to refuse to play a film. When this fails there is simply no session, and
            // nothing is reported.
            session = try? await playback.start(
                itemId: title.id,
                mediaSourceId: stream.mediaSourceId,
                positionSeconds: detail.resumeSeconds)
            playing = stream
        } catch {
            // Shown on a television, so the sentence Foundation writes rather than the type's whole
            // description — a viewer can do nothing with a decoding path.
            plan = .refused(.unknown(error.localizedDescription), source: chosenVersion ?? "")
        }
    }

    /// Each refusal is shown as itself. The server answers with a machine-readable reason precisely so a
    /// client need not say "cannot play this", and `packaging_pending` is not even a refusal — it means
    /// the walk has not reached this file yet, which on a spinning disk is minutes.
    @ViewBuilder
    private func refusalNotice(_ refusal: PlaybackRefusal) -> some View {
        HStack(spacing: 16) {
            Image(systemName: refusal.isPending ? "clock" : "exclamationmark.triangle")
            VStack(alignment: .leading, spacing: 4) {
                Text(refusalTitle(refusal)).font(.headline)
                Text(refusalDetail(refusal)).font(.callout).foregroundStyle(.secondary)
            }
        }
        .padding(24)
        .background(.thinMaterial, in: RoundedRectangle(cornerRadius: 12))
    }

    private func refusalTitle(_ refusal: PlaybackRefusal) -> String {
        switch refusal {
        case .packagingPending: "Still preparing"
        case .unsupportedVideoCodec, .packagingUnsupportedVideo: "This picture cannot be played here"
        case .unsupportedAudioCodec, .packagingUnsupportedAudio: "This soundtrack cannot be played here"
        case .unsupportedDynamicRange: "This needs a display this one is not"
        case .noAudioTrack: "This copy has no sound"
        case .noFile: "The file is missing"
        case .packagingUnavailable, .unknown: "This cannot be played"
        }
    }

    private func refusalDetail(_ refusal: PlaybackRefusal) -> String {
        switch refusal {
        case .packagingPending:
            "The server is still reading this file. It takes a few minutes for a film, and only happens once — try again shortly."
        case .unsupportedVideoCodec, .packagingUnsupportedVideo:
            "The video is in a format this device cannot decode and the server cannot repackage."
        case .unsupportedAudioCodec, .packagingUnsupportedAudio:
            "The only soundtrack is in a format that cannot be sent to this device. A copy with AC-3, E-AC-3 or AAC would play."
        case .unsupportedDynamicRange:
            "Forcing SDR in Settings may help."
        case .noAudioTrack:
            "Nothing to hear, so nothing was offered."
        case .noFile:
            "The server knows about this title but cannot find the file."
        case .packagingUnavailable:
            "The server cannot repackage this container at the moment."
        case .unknown(let code):
            code
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

            playButton(detail)

            if case .refused(let refusal, _) = plan {
                refusalNotice(refusal)
            }

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
