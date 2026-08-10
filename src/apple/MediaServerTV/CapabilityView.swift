import MediaKit
import SwiftUI

/// What this device reports about itself, shown rather than logged.
///
/// The first screen deliberately answers the question the whole feature turns on — does *this* box do
/// Dolby Vision — because it is the one thing that cannot be checked from a laptop, and because a
/// foundation that renders nothing has not been shown to work.
struct CapabilityView: View {
    private let store = PlaybackPreferencesStore()

    @State private var preferences: PlaybackPreferences

    init() {
        _preferences = State(initialValue: PlaybackPreferencesStore().load())
    }

    private var profile: CapabilityProfile { preferences.profile() }

    var body: some View {
        VStack(alignment: .leading, spacing: 32) {
            Text("Media Server")
                .font(.largeTitle)

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
            .onChange(of: preferences) { _, updated in
                store.save(updated)
            }
        }
        .padding(80)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }

    private func row(_ label: String, _ values: [String]) -> some View {
        GridRow {
            Text(label)
                .foregroundStyle(.secondary)
            Text(values.joined(separator: ", "))
                .monospaced()
        }
    }
}

#Preview {
    CapabilityView()
}
