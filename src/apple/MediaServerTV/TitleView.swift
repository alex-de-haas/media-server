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

    @Environment(\.colorScheme) private var systemColorScheme
    @State private var detail: TitleDetail?
    @State private var failure: String?
    @State private var chosenVersion: String?
    @State private var showsTechnicalDetails = false
    @FocusState private var focusedPlayPosition: Double?

    @State private var plan: PlaybackPlan?
    @State private var playing: PlayableStream?
    @State private var session: String?

    /// Where the viewing about to open begins — the resume point, or zero for a film started over —
    /// and, while the server is being asked, which button is waiting on it.
    @State private var startAt: Double = 0
    @State private var resolvingFrom: Double?

    /// Decided once when a film starts, and held for as long as it plays.
    ///
    /// It used to be read inside the cover's builder, which runs again on every redraw. The controller
    /// is built once, so a second diagnostics object would either replace the one collecting the run
    /// being watched or — worse, because it looks like nothing — be created and quietly dropped every
    /// second for the length of a film.
    @State private var diagnostics: PlaybackDiagnostics?
    @State private var ownLoader = true

    /// Which viewing this is. The session opens beside the player now, so its answer can land after the
    /// viewer has left — and assigning it then would leave a session id belonging to a film nobody is
    /// watching, for the next film's progress to be filed against.
    @State private var viewing = 0

    /// Where a viewing ended, when it ended before its session had opened. The session is still open on
    /// the server by then — the request went out — so the honest thing is to close it as soon as its id
    /// arrives, rather than leave it open for ever and lose the position with it.
    @State private var endedAt: (viewing: Int, position: Double)?

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
        .background(alignment: .top) {
            CinemaBackdrop(url: detail?.backdropURL(on: library.server), loader: loader)
                .frame(height: 850).ignoresSafeArea()
        }
        .background(detail?.backdropPath != nil ? Color.black : CinemaStyle.canvas)
        .environment(\.colorScheme, detail?.backdropPath != nil ? .dark : systemColorScheme)
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
            let version = detail?.versions.first { $0.id == stream.mediaSourceId }
            PlayerView(
                stream: stream,
                startAt: startAt,
                diagnostics: diagnostics,
                ownLoader: ownLoader,
                audioTracks: version?.audio ?? [],
                subtitleTracks: version?.subtitles ?? [],
                switchTracks: { audioId, subtitleId, off in
                    await switchTracks(to: stream, audio: audioId, subtitle: subtitleId, off: off)
                },
                onProgress: { position in
                    guard let session else { return }
                    Task { await playback.report(
                        itemId: title.id, playSessionId: session, positionSeconds: position) }
                },
                onFinished: { position in
                    let ended = session
                    session = nil
                    diagnostics = nil

                    // Noted whether or not a session exists: when one is still being opened, this is
                    // the position it will be closed at the moment its id arrives.
                    endedAt = (viewing, position)

                    guard let ended else { return }
                    Task {
                        await playback.stop(itemId: title.id, playSessionId: ended, positionSeconds: position)
                        await refresh()
                    }
                })
            .ignoresSafeArea()
        }
    }

    /// Reads the title again once a viewing has been reported, so the screen says what the server now
    /// knows: a film left halfway offers to resume, one watched to the end is marked so. The screen was
    /// fetched once when it opened and kept saying "Play" about a film the viewer had just left.
    private func refresh() async {
        guard let loaded = try? await library.detail(for: title.id) else { return }
        detail = loaded
        if !loaded.versions.contains(where: { $0.id == chosenVersion }) {
            chosenVersion = loaded.versions.first?.id
        }
    }

    /// Resume where the viewer left, or start over — both, for a film that was started, because a
    /// viewer who wants the beginning again had no way to ask for it. One button otherwise, and a
    /// mark beside it for a film watched to the end: the server resets its resume point to zero,
    /// which would leave it looking exactly like one never started.
    @ViewBuilder
    private func playButtons(_ detail: TitleDetail) -> some View {
        HStack(spacing: 24) {
            if detail.resumeSeconds > 0 {
                playButton("Resume from \(PlaybackPosition.label(detail.resumeSeconds))", detail, from: detail.resumeSeconds)
                playButton("From the beginning", detail, from: 0)
            } else {
                playButton("Play", detail, from: 0)
            }

            if detail.played {
                // Beside the button rather than in it: watched is a fact about the film, and the
                // button says what pressing it does.
                Label("Watched", systemImage: "checkmark.circle.fill")
                    .foregroundStyle(.secondary)
            }
        }
    }

    @ViewBuilder
    private func playButton(_ name: LocalizedStringKey, _ detail: TitleDetail, from position: Double) -> some View {
        let button = Button {
            Task { await play(detail, from: position) }
        } label: {
            if resolvingFrom == position {
                ProgressView()
            } else {
                Label(name, systemImage: "play.fill")
            }
        }
        .disabled(resolvingFrom != nil)
        .focused($focusedPlayPosition, equals: position)

        if position == detail.resumeSeconds {
            button.buttonStyle(.borderedProminent)
        } else {
            button.buttonStyle(.bordered)
        }
    }

    /// The same film with different tracks, or nil when the server would not give it.
    ///
    /// Resolved rather than assembled here: which tracks a container can carry is the server's
    /// question, and a client that edited the URL itself would be answering it from the wrong side.
    /// The edition is pinned, so switching a dub cannot quietly move a viewer to another copy.
    private func switchTracks(
        to stream: PlayableStream, audio: String?, subtitle: String?, off: Bool
    ) async -> PlayableStream? {
        guard case .play(let replacement) = try? await playback.plan(
            for: title.id,
            preferring: stream.mediaSourceId,
            audioStreamId: audio,
            subtitleStreamId: subtitle,
            subtitlesOff: off),
            replacement.mediaSourceId == stream.mediaSourceId
        else {
            return nil
        }

        return replacement
    }

    private func play(_ detail: TitleDetail, from position: Double) async {
        resolvingFrom = position
        defer { resolvingFrom = nil }

        let asked = Date()

        do {
            // The version the viewer picked, not whichever the server listed first. A picker that
            // changes what is listed and not what happens is worse than no picker at all.
            let answer = try await playback.plan(for: title.id, preferring: chosenVersion)
            plan = answer

            guard case .play(let stream) = answer else { return }

            // Made here rather than in the cover's builder, so the run being watched is collected by
            // one object from beginning to end.
            let watching = PlaybackPreferencesStore().load().showDiagnostics
                ? PlaybackDiagnostics() : nil
            watching?.resolved(after: Date().timeIntervalSince(asked))
            diagnostics = watching
            ownLoader = PlaybackPreferencesStore().load().usesOwnLoader

            // The film opens now. Everything a viewer is waiting for is in hand, and the only thing
            // still outstanding — the session a progress report is filed against — was always best
            // effort: a server that will not open one is no reason to keep somebody watching a
            // spinner. It was on the critical path for no reason but the order it was written in.
            startAt = position
            playing = stream

            // Alongside, not before. Opening it at all is what leaves a record for a viewer who
            // stops after ten seconds; a report filed a moment late is a report filed.
            viewing += 1
            let opening = viewing
            Task { @MainActor in
                let opened = try? await playback.start(
                    itemId: title.id,
                    mediaSourceId: stream.mediaSourceId,
                    positionSeconds: position)

                guard let opened else { return }

                // Only if this is still the viewing that asked. Counted rather than compared against
                // the stream, because leaving a film and starting the same one again is two viewings
                // and the first one's session must not be filed against the second.
                if opening == viewing, endedAt?.viewing != opening {
                    session = opened
                    return
                }

                // It ended while this was in flight. The server has an open session either way, so it
                // is closed here at the position the viewer actually reached instead of being dropped.
                await playback.stop(
                    itemId: title.id,
                    playSessionId: opened,
                    positionSeconds: endedAt?.viewing == opening ? endedAt?.position ?? 0 : 0)
                await refresh()
            }
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
        VStack(alignment: .leading, spacing: 32) {
            VStack(alignment: .leading, spacing: 16) {
                Text(detail.title).font(.system(size: 64, weight: .bold))
                    .frame(maxWidth: 1000, alignment: .leading)
                Text(facts(detail)).font(.callout).foregroundStyle(.secondary)
            }
            playButtons(detail).padding(.vertical, 12)

            if let overview = detail.overview, !overview.isEmpty {
                Text(overview).font(.body)
                    .fixedSize(horizontal: false, vertical: true)
                    .frame(maxWidth: 950, alignment: .leading)
            }
            if case .refused(let refusal, _) = plan { refusalNotice(refusal) }

            if let version = detail.versions.first(where: { $0.id == chosenVersion }) ?? detail.versions.first {
                VStack(alignment: .leading, spacing: 20) {
                    versions(detail.versions)
                    Button("Audio, subtitles & file details") {
                        showsTechnicalDetails = true
                    }
                    .sheet(isPresented: $showsTechnicalDetails) {
                        tracks(version)
                    }
                }
                .padding(.top, 20)
            }
        }
        .padding(CinemaStyle.inset)
        .frame(maxWidth: .infinity, alignment: .leading)
        .defaultFocus($focusedPlayPosition, detail.resumeSeconds)
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
                    HStack(spacing: 20) {
                        Image(systemName: version.id == chosenVersion ? "checkmark.circle.fill" : "circle")
                        VStack(alignment: .leading, spacing: 10) {
                            Text(version.versionName.flatMap { $0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? nil : $0 } ?? "Original")
                            Text([version.container.uppercased(), version.video?.codec?.uppercased(), version.sizeDescription]
                                .compactMap { $0 }.joined(separator: " · "))
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }
                        Spacer(minLength: 32)
                        if let picture = version.video {
                            dynamicRange(picture, alignment: .trailing)
                                .multilineTextAlignment(.trailing)
                        }
                    }
                    .padding(.vertical, 8)
                }
                .buttonStyle(.bordered)
                .accessibilityValue(version.id == chosenVersion ? "Selected for playback" : "")
            }
        }
    }

    @ViewBuilder
    private func tracks(_ version: TitleVersion) -> some View {
        VStack(alignment: .leading, spacing: 24) {
            HStack {
                VStack(alignment: .leading, spacing: 8) {
                    Text("Audio, subtitles & file details").font(.title2.bold())
                    Text(version.versionName ?? "Original").foregroundStyle(.secondary)
                }
                Spacer()
                Button("Close", systemImage: "xmark") { showsTechnicalDetails = false }
            }
            List {
                Section("File") {
                    TechnicalDetailRow {
                        VStack(alignment: .leading, spacing: 12) {
                            Text("\(version.container.uppercased()) · \(version.sizeDescription)")
                            if let picture = version.video { dynamicRange(picture) }
                        }
                    }
                }
                trackList("Audio", version.audio)
                trackList("Subtitles", version.subtitles)
            }
        }
        .padding(48)
        .frame(width: 1280, height: 820)
    }

    /// The picture's dynamic range as capsules — "Dolby Vision 8.1", "HDR10" — with the one note a dual-layer
    /// profile 7 earns on this device. Text marks rather than logos: the Dolby Vision mark is licensed, and a
    /// capsule reads the same.
    @ViewBuilder
    private func dynamicRange(_ picture: TitleTrack, alignment: HorizontalAlignment = .leading) -> some View {
        let badges = picture.dynamicRangeBadges
        if !badges.isEmpty {
            VStack(alignment: alignment, spacing: 8) {
                HStack(spacing: 12) {
                    ForEach(badges, id: \.self) { badge in
                        Text(badge)
                            .font(.caption.weight(.semibold))
                            .padding(.horizontal, 14)
                            .padding(.vertical, 6)
                            .background(.secondary.opacity(0.25), in: Capsule())
                    }
                }

                if let note = picture.dolbyVisionNote {
                    Text(note)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
        }
    }

    @ViewBuilder
    private func trackList(_ heading: String, _ tracks: [TitleTrack]) -> some View {
        Section(heading) {
            if tracks.isEmpty {
                TechnicalDetailRow { Text("None").foregroundStyle(.secondary) }
            } else {
                ForEach(tracks) { track in
                    TechnicalDetailRow {
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
}

/// Information rows need a visible focus target even though selecting them performs no action.
private struct TechnicalDetailRow<Content: View>: View {
    @ViewBuilder var content: () -> Content
    @FocusState private var isFocused: Bool

    var body: some View {
        content()
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(12)
            .background(.primary.opacity(isFocused ? 0.12 : 0), in: RoundedRectangle(cornerRadius: 10))
            .overlay {
                RoundedRectangle(cornerRadius: 10)
                    .strokeBorder(.primary.opacity(isFocused ? 0.6 : 0), lineWidth: 2)
            }
            .focusable()
            .focused($isFocused)
    }
}
