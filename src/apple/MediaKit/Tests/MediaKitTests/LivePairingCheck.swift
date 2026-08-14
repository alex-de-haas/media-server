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
        let deadline = Date().addingTimeInterval(TimeInterval(min(grant.expiresInSeconds, 180)))
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
        do {
            let identity = try await client.exchange(
                core: core, appId: bootstrap.appId, redirectUri: server, coreToken: coreToken)
            say("  app token of \(identity.accessToken.count) characters, expires \(identity.expiresAt)")
        } catch {
            Issue.record("exchange failed: \(error)")
            return
        }

        say("")
        say("✅ the whole chain works against \(bootstrap.serverName)")
    }
}
