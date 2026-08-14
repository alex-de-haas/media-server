import MediaKit
import SwiftUI

/// Which server this television is signed in to, what it told that server it can play, and the two
/// controls a viewer may actually need: force the dynamic range, or sign out.
///
/// A tab rather than a screen of its own, because both controls are the answer to a symptom — a dark
/// picture, or the wrong server — and something that fixes a symptom has to be findable while looking
/// at it.
struct SettingsView: View {
    let paired: PairedServer
    let pairing: PairingSession

    private let store = PlaybackPreferencesStore()

    @State private var preferences: PlaybackPreferences

    init(paired: PairedServer, pairing: PairingSession) {
        self.paired = paired
        self.pairing = pairing
        _preferences = State(initialValue: PlaybackPreferencesStore().load())
    }

    private var profile: CapabilityProfile { preferences.profile() }

    var body: some View {
        VStack(alignment: .leading, spacing: 32) {
            VStack(alignment: .leading, spacing: 8) {
                Text(paired.serverName)
                    .font(.largeTitle)
                Text(paired.server.absoluteString)
                    .font(.title3)
                    .foregroundStyle(.secondary)
            }

            Grid(alignment: .leading, horizontalSpacing: 40, verticalSpacing: 16) {
                row("Containers", profile.containers)
                row("Video", profile.videoCodecs)
                row("Audio", profile.audioCodecs)
                row("Dynamic range", profile.hdrFormats)
            }

            Picker("Dynamic range", selection: $preferences.dynamicRange) {
                ForEach(DynamicRangeOverride.allCases, id: \.self) { override in
                    Text(override.rawValue.uppercased()).tag(override)
                }
            }
            .pickerStyle(.segmented)
            // A switch that forgets is worse than no switch: a viewer who set SDR to fix a dark picture
            // would find it dark again on the next launch, having already tried the one control offered.
            .onChange(of: preferences) { _, updated in store.save(updated) }

            Button("Sign out", role: .destructive) { pairing.unpair() }
                .padding(.top, 24)
        }
        .padding(80)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }

    private func row(_ label: String, _ values: [String]) -> some View {
        GridRow {
            Text(label).foregroundStyle(.secondary)
            Text(values.joined(separator: ", ")).monospaced()
        }
    }
}
