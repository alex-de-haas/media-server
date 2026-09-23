import MediaKit
import SwiftUI

/// Server identity, playback preferences and optional diagnostics for this television.
struct SettingsView: View {
  let paired: PairedServer
  let pairing: PairingSession

  private let store = PlaybackPreferencesStore()

  @State private var preferences: PlaybackPreferences
  @State private var showCapabilities = false
  @State private var showDiagnostics = false

  init(paired: PairedServer, pairing: PairingSession) {
    self.paired = paired
    self.pairing = pairing
    _preferences = State(initialValue: PlaybackPreferencesStore().load())
  }

  private var profile: CapabilityProfile { preferences.profile() }

  var body: some View {
    ScrollView {
      VStack(alignment: .leading, spacing: 32) {
        VStack(alignment: .leading, spacing: 8) {
          Text(paired.serverName)
            .font(.largeTitle)
          Text(paired.server.absoluteString)
            .font(.title3)
            .foregroundStyle(.secondary)
        }

        Text("Playback").font(.title2.bold())
        Button(showCapabilities ? "Hide device capabilities" : "Device capabilities") {
          showCapabilities.toggle()
        }
        if showCapabilities {
          Grid(alignment: .leading, horizontalSpacing: 40, verticalSpacing: 16) {
            row("Containers", profile.containers)
            row("Video", profile.videoCodecs)
            row("Audio", profile.audioCodecs)
            row("Dynamic range", profile.hdrFormats)
          }

        }

        Picker("Dynamic range", selection: $preferences.dynamicRange) {
          ForEach(DynamicRangeOverride.allCases, id: \.self) { override in
            Text(override.rawValue.uppercased()).tag(override)
          }
        }
        .pickerStyle(.segmented)

        Toggle("Enable cache", isOn: $preferences.usesOwnLoader)

        if preferences.usesOwnLoader {
          Picker("Cache storage", selection: $preferences.cacheStorage) {
            Text("Memory").tag(PlaybackCacheStorage.memory)
            Text("Disk").tag(PlaybackCacheStorage.disk)
          }
          .pickerStyle(.segmented)

          Text(preferences.cacheStorage == .memory
            ? "Keeps a small read-ahead buffer in memory."
            : "Stores a larger read-ahead buffer on disk. Uses memory if disk storage is unavailable.")
            .font(.caption)
            .foregroundStyle(.secondary)
            .frame(maxWidth: 900, alignment: .leading)
        } else {
          Text("Uses the player's built-in buffering without an additional cache.")
            .font(.caption)
            .foregroundStyle(.secondary)
        }

        Text("Applies when you next start playback.")
          .font(.caption)
          .foregroundStyle(.secondary)

        Text("Diagnostics").font(.title2.bold()).padding(.top, 20)
        Button(showDiagnostics ? "Hide diagnostics settings" : "Diagnostics settings") {
          showDiagnostics.toggle()
        }
        if showDiagnostics {
          Toggle("Show playback diagnostics", isOn: $preferences.showDiagnostics)

          Text(
            "Shows playback progress, buffer estimates, stalls and cache activity over the picture. "
              + "A zero buffer estimate alone does not mean playback has stalled."
          )
          .font(.caption)
          .foregroundStyle(.secondary)
          .frame(maxWidth: 900, alignment: .leading)

        }

        Button { pairing.unpair() } label: {
          Label("Sign out", systemImage: "rectangle.portrait.and.arrow.right")
        }
          .buttonStyle(.bordered)
          .padding(.top, 24)
      }
      .onChange(of: preferences) { _, updated in store.save(updated) }
      .padding(80)
      .frame(maxWidth: .infinity, alignment: .topLeading)
    }
  }

  private func row(_ label: String, _ values: [String]) -> some View {
    GridRow {
      Text(label).foregroundStyle(.secondary)
      Text(values.joined(separator: ", ")).monospaced()
    }
  }
}
