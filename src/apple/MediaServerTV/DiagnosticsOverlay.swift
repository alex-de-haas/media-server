import MediaKit
import SwiftUI

/// The numbers that tell a freeze apart from its causes, on screen while it happens.
///
/// A television has no console to read and no file a viewer can reach, so a diagnostic that writes
/// somewhere clever is a diagnostic nobody uses. This is deliberately ugly and deliberately in the way:
/// it is turned on when something is wrong and off the rest of the time.
///
/// **Buffer ahead is the number to watch.** It falls at one second per second whenever the player has
/// stopped fetching — so a freeze preceded by a slow, steady fall is starvation, and one that arrives
/// with the buffer full is not.
struct DiagnosticsOverlay: View {
    let diagnostics: PlaybackDiagnostics

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            row("позиция", String(format: "%.0f с", diagnostics.position))
            row("буфер впереди", String(format: "%.1f с", diagnostics.bufferAhead),
                warn: diagnostics.bufferAhead < 15)
            row("замираний", "\(diagnostics.stalls)", warn: diagnostics.stalls > 0)
            row("скорость", String(format: "%.1f Мбит/с", diagnostics.observedMbps))
            row("поспевает", diagnostics.keepingUp ? "да" : "НЕТ", warn: !diagnostics.keepingUp)

            if diagnostics.lowestBuffer.isFinite {
                Divider().background(.white.opacity(0.3))
                row("минимум буфера",
                    String(format: "%.1f с на %.0f с", diagnostics.lowestBuffer, diagnostics.lowestAt),
                    warn: diagnostics.lowestBuffer < 15)
            }

            // The shape of the last minute, which is what says whether a dip was gradual or sudden.
            sparkline
        }
        .font(.system(size: 22, weight: .medium, design: .monospaced))
        .padding(20)
        .background(.black.opacity(0.65), in: RoundedRectangle(cornerRadius: 12))
        .foregroundStyle(.white)
        .padding(40)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .allowsHitTesting(false)
    }

    private func row(_ label: String, _ value: String, warn: Bool = false) -> some View {
        HStack {
            Text(label)
                .foregroundStyle(.white.opacity(0.65))
                .frame(width: 260, alignment: .leading)
            Text(value)
                .foregroundStyle(warn ? .orange : .white)
        }
    }

    private var sparkline: some View {
        let recent = diagnostics.samples.suffix(60)
        let peak = max(recent.map(\.bufferAhead).max() ?? 1, 1)

        return HStack(alignment: .bottom, spacing: 2) {
            ForEach(recent) { sample in
                RoundedRectangle(cornerRadius: 1)
                    .fill(sample.bufferAhead < 15 ? Color.orange : .white.opacity(0.8))
                    .frame(width: 4, height: max(2, 60 * sample.bufferAhead / peak))
            }
        }
        .frame(height: 60, alignment: .bottom)
        .padding(.top, 4)
    }
}
