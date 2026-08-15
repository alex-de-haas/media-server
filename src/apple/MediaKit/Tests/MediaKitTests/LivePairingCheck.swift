import Foundation
import Testing

/// `swift test` buffers stdout when it is not a terminal, so a code printed halfway through a run that
/// waits for a human would not appear until the waiting was over — which is too late to be approved.
/// Standard error is unbuffered.
private func say(_ line: String) {
    FileHandle.standardError.write(Data((line + "\n").utf8))

    // And to a file, because `swift test` runs the bundle through a helper that swallows both streams
    // until the run ends — which is after the code has expired unapproved.
    guard let path = ProcessInfo.processInfo.environment["MEDIASERVER_LIVE_LOG"] else { return }
    let text = Data((line + "\n").utf8)
    if let handle = FileHandle(forWritingAtPath: path) {
        handle.seekToEndOfFile()
        handle.write(text)
        try? handle.close()
    } else {
        try? text.write(to: URL(fileURLWithPath: path))
    }
}

@testable import MediaKit

/// Runs the real pairing chain against a real host.
///
/// Not a test, and deliberately kept rather than thrown away. It is skipped unless `MEDIASERVER_LIVE`
/// names an address, and it needs a human to approve a code halfway through, so it can never run in CI.
///
/// It exists because everything else in this suite is a stub, and a stub can only confirm what its
/// author already believed. Four of the six calls in this chain are written against Core's source rather
/// than against a published document — Core publishes none — so this is the only thing that can tell us
/// they are right. The `surfaceVersion` mistake passed forty stubbed tests and would have failed here on
/// the first line.
///
///     MEDIASERVER_LIVE=media.example.com \
///     MEDIASERVER_LIVE_LOG=/tmp/chain.log \
///     swift test --filter LivePairingCheck
///
/// The log path is worth setting: `swift test` runs the bundle through a helper that holds both streams
/// until the run ends, which is long after the code has to be read and approved.
@MainActor
struct LivePairingCheck {
    @Test("The whole chain, against a real Core")
    func run() async throws {
        guard let address = ProcessInfo.processInfo.environment["MEDIASERVER_LIVE"] else { return }

        let client = PairingClient()

        // 1 — is anything there, and is it ours
        guard let server = PairingSession.normalise(address) else {
            Issue.record("Not an address: \(address)")
            return
        }

        say("→ bootstrap \(server.absoluteString)")
        let bootstrap: ServerBootstrap
        do {
            bootstrap = try await client.bootstrap(server: server)
        } catch {
            Issue.record("bootstrap failed: \(error)")
            return
        }

        say("  server        \(bootstrap.serverName)")
        say("  appId         \(bootstrap.appId)")
        say("  surface       \(bootstrap.surfaceVersion)")
        say("  coreOrigin    \(bootstrap.coreOrigin ?? "«null»")")

        guard let origin = bootstrap.coreOrigin, let core = URL(string: origin) else {
            Issue.record("No coreOrigin, so there is nowhere to be approved.")
            return
        }

        // 2 — a code for a human
        say("→ device code at \(core.absoluteString)")
        let grant: DeviceCodeGrant
        do {
            grant = try await client.requestDeviceCode(core: core, label: "Pairing check")
        } catch {
            Issue.record("device code failed: \(error)")
            return
        }

        say("")
        say("  ┌──────────────────────────────────────────┐")
        say("    APPROVE THIS CODE:  \(grant.userCode)")
        say("    at \(grant.verificationUri ?? "Shell → Settings → Access tokens")")
        say("  └──────────────────────────────────────────┘")
        say("")
        say("  polling every \(grant.intervalSeconds)s, expires in \(grant.expiresInSeconds)s")

        // 3 — waiting
        // Core's own lifetime, not a shorter one of my own. Approving means picking up a phone and
        // finding a settings screen, and a cap of three minutes turned that into a race nobody was told
        // they were running.
        let deadline = Date().addingTimeInterval(TimeInterval(grant.expiresInSeconds))
        var coreToken: String?

        while Date() < deadline, coreToken == nil {
            try await Task.sleep(for: .seconds(max(1, grant.intervalSeconds)))
            switch try await client.poll(core: core, deviceCode: grant.deviceCode) {
            case .approved(let token):
                coreToken = token
            case .pending:
                continue
            case .denied:
                Issue.record("Declined in Shell.")
                return
            case .expired:
                Issue.record("The code expired before it was approved.")
                return
            }
        }

        guard let coreToken else {
            Issue.record("Nobody approved it in time.")
            return
        }

        // Length rather than value: a Core access token carries its holder's full Core role, and a
        // terminal is a place things get pasted from.
        say("  approved, Core token of \(coreToken.count) characters")

        // 4 — narrowing it to this app
        say("→ exchange for an app identity")
        let identity: AppIdentity
        do {
            identity = try await client.exchange(
                core: core, appId: bootstrap.appId, redirectUri: server, coreToken: coreToken)
            say("  app token of \(identity.accessToken.count) characters, expires \(identity.expiresAt)")
        } catch {
            Issue.record("exchange failed: \(error)")
            return
        }

        // 5 — and now the half that has only ever been exercised against stubs
        let paired = PairedServer(
            server: server,
            serverName: bootstrap.serverName,
            appId: bootstrap.appId,
            coreOrigin: core,
            coreToken: coreToken,
            identity: identity)

        // Deliberately not the Keychain: a diagnostic must not leave a credential behind.
        let session = ServerSession(paired: paired, store: InMemoryCredentialStore())
        let library = LibraryStore(session: session)

        say("→ draining the sync feed")
        await library.load()

        guard case .loaded = library.state else {
            Issue.record("library did not load: \(library.state)")
            return
        }

        say("  \(library.items.count) titles: \(library.movies.count) films, \(library.series.count) series")

        let started = library.items.filter { $0.resumeSeconds > 0 }.count
        let finished = library.items.filter(\.played).count
        // The field the generator used to drop. Zero for both would not prove it broken, but it is the
        // number worth reading first.
        say("  userData: \(started) started, \(finished) watched")

        // Prefer one with a poster, so the artwork check below is actually exercised — but a library
        // where nothing has one is not a failure, and the harness has to know the difference.
        guard let sample = library.movies.first(where: { $0.resumeSeconds > 0 && $0.hasArtwork })
            ?? library.movies.first(where: \.hasArtwork)
            ?? library.movies.first
        else {
            say("  no films to open")
            return
        }

        say("→ detail for \(sample.title)")
        let detail = try await library.detail(for: sample.id)
        say("  \(detail.versions.count) version(s), runtime \(Int((detail.runtimeSeconds ?? 0) / 60)) min")
        for version in detail.versions {
            say("    \(version.versionName ?? version.container.uppercased()) — "
                + "\(version.sizeDescription), "
                + "video=[\(version.videos.map { "\($0.codec ?? "?")/\($0.hdrFormat ?? "?")" }.joined(separator: ", "))], "
                + "\(version.audio.count) audio, \(version.subtitles.count) subtitle")
            for track in version.audio where track.isExternal {
                say("      beside the file: \(track.label)")
            }
        }

        // A title the provider had no poster for answers 404, which is correct. Recording that as a
        // failure would make this harness cry wolf, and its whole worth is that it does not.
        if !sample.hasArtwork {
            say("→ artwork skipped: no poster for \(sample.title)")
        } else if let url = sample.artworkURL(on: server) {
            say("→ artwork \(url.lastPathComponent)")
            if let data = await session.artwork.image(at: url) {
                say("  \(data.count) bytes")
            } else {
                Issue.record("artwork did not load from \(url)")
            }
        }

        // 6 — what the server would do if this were a television asking to play
        //
        // Stated, not detected. This runs on a Mac, where `presentsHDR` is deliberately false, so a
        // detected profile asks "what would a Mac with no HDR be offered" — which is a real question and
        // not the one this client exists to answer. An Apple TV 4K is the device the whole design turns
        // on, so that is what gets described.
        struct AppleTV4K: DeviceCapabilities {
            let decodesDolbyVision = true
            let presentsHDR = true
        }

        say("→ resolve playback for \(sample.title), as an Apple TV 4K would ask")
        let playback = PlaybackService(session: session, device: AppleTV4K())
        let plans = try await playback.plans(for: sample.id)
        say("  \(plans.count) copy(ies)")
        for plan in plans {
            switch plan {
            case .play(let stream):
                say("    \(stream.decision.rawValue): signalling \(stream.signalling ?? "none"), "
                    + "source is \(stream.sourceDynamicRange ?? "unstated")")
            case .refused(let refusal, _):
                say("    refused: \(refusal)")
            }
        }

        guard case .play(let stream)? = plans.first(where: \.isPlayable) else {
            // Not a client failure: the reasons above are the server's answer, and a library can hold a
            // copy nothing here can decode. But it is not a success either, and saying so is the whole
            // point of this file.
            // Recorded, not merely printed. Returning quietly leaves the run green, which is the same
            // false success this file has now produced twice in different disguises.
            say("")
            say("⚠️  pairing, browsing and detail work — but nothing here would play, for the reasons above")
            Issue.record("nothing in the library would play")
            return
        }

        // The strongest check available without a television. AVFoundation opens with a two-byte range
        // probe purely to learn whether the server honours ranges, and stops before showing anything if
        // it does not — so issue exactly that, and hold it to what the spike measured.
        // The whole URL, token and all. It is a credential, so this goes to the local log a human asked
        // for and nowhere else — but without it nothing else can be pointed at the same bytes.
        say("  url: \(stream.url.absoluteString)")
        say("  signalling: \(stream.signalling ?? "«none»"), decision: \(stream.decision)")

        say("→ range probe on the stream URL")
        var probe = URLRequest(url: stream.url)
        probe.setValue("bytes=0-1", forHTTPHeaderField: "Range")
        let (head, response) = try await URLSession.shared.data(for: probe)

        guard let http = response as? HTTPURLResponse else {
            Issue.record("the stream URL did not answer over HTTP")
            return
        }

        let range = http.value(forHTTPHeaderField: "Content-Range") ?? "«no Content-Range»"
        say("  HTTP \(http.statusCode), \(head.count) bytes, \(range)")

        guard http.statusCode == 206 else {
            // 206 or nothing plays: a 200 means ranges were ignored, and AVFoundation refuses that after
            // the two-byte probe rather than falling back to a whole-file read.
            Issue.record("expected 206 Partial Content, got \(http.statusCode)")
            return
        }

        guard range.contains("/"), !range.hasSuffix("/*") else {
            // The total must be stated. `*` is legal HTTP and AVFoundation rejects it outright.
            Issue.record("the total length was not declared: \(range)")
            return
        }

        say("")
        say("✅ pairing, browsing, detail and playback negotiation all work against \(bootstrap.serverName)")
    }
}
