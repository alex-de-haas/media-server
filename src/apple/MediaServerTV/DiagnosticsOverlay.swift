import MediaKit
import SwiftUI

/// Common playback/network observations stay visible with every cache setting. Cache internals
/// occupy a separate column; neither a zero buffer estimate nor a low rate alone implies a stall.
struct DiagnosticsOverlay: View {
    let diagnostics: PlaybackDiagnostics

    var body: some View {
        HStack(alignment: .top, spacing: 16) {
            panel { playerPanel }
            if let details = diagnostics.loaderDetails {
                panel { cachePanel(details) }
            }
        }
        .padding(32)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .allowsHitTesting(false)
    }

    private func panel<Content: View>(@ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 5, content: content)
            .font(.system(size: 18, weight: .medium, design: .monospaced))
            .foregroundStyle(.white)
            .padding(16)
            .frame(width: 600, alignment: .leading)
            .background(.black.opacity(0.72), in: RoundedRectangle(cornerRadius: 12))
    }

    private var playerPanel: some View {
        Group {
            HStack {
                Text("PLAYBACK · \(mode)").fontWeight(.bold)
                Spacer()
                Text(Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "—")
                    .foregroundStyle(.white.opacity(0.65))
            }
            row("Position / watched", String(format: "%.0f / %.0f s", diagnostics.position, diagnostics.watched))
            row("Player buffer", String(format: "%.1f s · %@", diagnostics.bufferAhead,
                                        diagnostics.keepingUp ? "keeping up" : "not keeping up"),
                warn: !diagnostics.keepingUp)
            if diagnostics.lowestBuffer.isFinite {
                row("Minimum estimate", String(format: "%.1f s at %.0f s", diagnostics.lowestBuffer, diagnostics.lowestAt))
            }
            row("Stalls / recoveries", "\(diagnostics.stalls) / \(diagnostics.recoveries)",
                warn: diagnostics.stalls > 0 || diagnostics.recoveries > 0)
            row("Dropped / errors", "\(diagnostics.droppedFrames.map(String.init) ?? "—") / \(diagnostics.errors)")
            row("Resident RAM", String(format: "%.0f MB", diagnostics.residentMB))
            row("Resolve / open", "\(seconds(diagnostics.resolveSeconds)) / \(seconds(diagnostics.openSeconds))")
            Divider().overlay(.white.opacity(0.2))
            Text(diagnostics.loaderDetails == nil ? "NETWORK · AVPlayer access log" : "NETWORK · Loader counters")
                .fontWeight(.bold)
            row("Media GETs", diagnostics.serverRequests.map(String.init) ?? "—")
            row("GETs / second", diagnostics.requestsPerSecond.map { String(format: "%.2f (recent)", $0) } ?? "—")
            row("Bytes / GET", diagnostics.meanRequestBytes.map { String(format: "~%.0f KiB", $0 / 1024) } ?? "—")
            row("Received", diagnostics.networkBytes.map { String(format: "%.2f GiB", Double($0) / 1_073_741_824) } ?? "—")
            row("Receive / peak", "\(rate(diagnostics.networkMbps)) / \(rate(diagnostics.peakInflow)) Mbps")
            row("Player rate estimate", diagnostics.playerObservedMbps.map { String(format: "%.1f Mbps", $0) } ?? "—")
            Text(diagnostics.loaderDetails == nil
                 ? "Current item · log updates may lag · — unavailable"
                 : "Current loader · started GETs, including cancelled")
                .foregroundStyle(.white.opacity(0.6))
                .font(.system(size: 16, design: .monospaced))
                .lineLimit(2)
            Text("Rates: last ≤10 s · bytes/GET: aggregate estimate")
                .foregroundStyle(.white.opacity(0.6))
                .font(.system(size: 16, design: .monospaced))
            sparkline
            if let moment = diagnostics.lastStall {
                row("Last stall", describe(moment), warn: true)
            }
            if let moment = diagnostics.lastRecovery {
                row("Last recovery", describe(moment), warn: true)
            }
            if let error = diagnostics.lastError {
                Text("Errors \(diagnostics.errors) · \(error)")
                    .foregroundStyle(.orange)
                    .lineLimit(2)
                    .truncationMode(.tail)
            }
        }
    }

    private var mode: String {
        guard let details = diagnostics.loaderDetails else { return "NO CACHE" }
        return details.diskCache ? "DISK" : (details.cacheFailures > 0 ? "RAM FALLBACK" : "RAM")
    }

    private func cachePanel(_ details: RemuxLoader.Snapshot) -> some View {
        Group {
            Text("CACHE · \(mode)").fontWeight(.bold)
            row("Size / target", String(format: "%.0f / %.0f MiB", Double(details.windowBytes) / 1_048_576,
                                        Double(details.cacheBudget) / 1_048_576))
            row("Ahead / behind", String(format: "%.0f / %.0f MiB", Double(details.aheadBytes) / 1_048_576,
                                         Double(details.behindBytes) / 1_048_576))
            if let seconds = details.estimatedAheadSeconds {
                row("Ahead estimate", String(format: "~%.0f s (from bytes)", seconds))
            }
            row("Served from cache", String(format: "%.2f GiB", Double(details.cacheReadBytes) / 1_073_741_824))
            row("Pending / cached", "\(details.outstanding) / \(details.cachedRequests) next byte")
            row("Oldest / delivery", String(format: "%.1f s / %@", details.oldestRequestSeconds,
                                           details.deliveryPaused ? "throttled" : "allowed"))
            row("Readers / spread", String(format: "%d / %.0f MiB", details.readers, Double(details.readerSpread) / 1_048_576))
            row("Resets / aside GETs", "\(details.restarts) / \(details.asides)")
            row("Aside back / ahead", "\(details.asideBehind) / \(details.asideAhead)")
            row("Aside ≤64 KiB", "\(details.asideSmall)")
            if details.asides > 0 {
                row("Mean aside request", String(format: "%.0f KiB", Double(details.asideRequestedBytes) / Double(details.asides) / 1024))
            }
            if let reset = details.lastRestart {
                row("Last reset (MiB)", String(format: "%.0f–%.0f → %.0f", Double(reset.windowStart) / 1_048_576,
                                              Double(reset.windowEnd) / 1_048_576, Double(reset.offset) / 1_048_576))
            }
            if details.diskCache || details.cacheFailures > 0 {
                row("Disk max read/write", String(format: "%.1f / %.1f ms", details.maximumDiskReadMS, details.maximumDiskWriteMS))
                row("Cache failures", "\(details.cacheFailures)", warn: details.cacheFailures > 0)
            }
            if let moment = diagnostics.lastStall {
                cacheMoment("AT LAST STALL", moment)
            }
            if let moment = diagnostics.lastRecovery {
                cacheMoment("AT LAST RECOVERY", moment)
            }
            if let moment = diagnostics.lowestMoment {
                cacheMoment("AT MINIMUM BUFFER", moment)
            }
        }
    }

    private func cacheMoment(_ label: String, _ moment: PlaybackDiagnostics.Moment) -> some View {
        Group {
            if let cache = moment.window {
                Divider().overlay(.white.opacity(0.2))
                Text("\(label) · \(cache.diskCache ? "Disk" : "RAM")").fontWeight(.bold)
                Text(String(format: "Ahead %.0f MiB · pending %d/%d cached",
                            Double(cache.aheadBytes) / 1_048_576, cache.outstanding, cache.cachedRequests))
                Text(String(format: "Oldest %.1f s · errors %d · %@", cache.oldestRequestSeconds,
                            cache.cacheFailures, cache.deliveryPaused ? "throttled" : "allowed"))
            }
        }
    }

    private func describe(_ moment: PlaybackDiagnostics.Moment) -> String {
        String(format: "%.0f s · buffer %.1f s", moment.position, moment.bufferAhead)
    }

    private func seconds(_ value: Double?) -> String {
        value.map { String(format: "%.1f s", $0) } ?? "—"
    }

    private func rate(_ value: Double?) -> String {
        value.map { String(format: "%.1f", $0) } ?? "—"
    }

    private func row(_ label: String, _ value: String, warn: Bool = false) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            Text(label)
                .foregroundStyle(.white.opacity(0.65))
                .frame(width: 225, alignment: .leading)
            Text(value)
                .foregroundStyle(warn ? .orange : .white)
                .lineLimit(1)
                .minimumScaleFactor(0.85)
        }
    }

    private var sparkline: some View {
        let recent = diagnostics.samples.suffix(60)
        let peak = max(recent.map(\.bufferAhead).max() ?? 1, 1)
        return VStack(alignment: .leading, spacing: 4) {
            Text(String(format: "Player buffer estimate · 60 s · scale 0–%.1f s", peak))
                .font(.system(size: 16, design: .monospaced))
                .foregroundStyle(.white.opacity(0.65))
            HStack(alignment: .bottom, spacing: 2) {
                ForEach(recent) { sample in
                    RoundedRectangle(cornerRadius: 1)
                        .fill(sample.keepingUp ? .white.opacity(0.8) : Color.orange)
                        .frame(width: 7, height: max(2, 36 * sample.bufferAhead / peak))
                }
            }
            .frame(height: 36, alignment: .bottom)
        }
        .padding(.vertical, 4)
    }
}
