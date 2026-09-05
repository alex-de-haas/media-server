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
///
/// **Peak inflow is the number that settles what a flat buffer cannot.** A buffer parked at two seconds
/// means bytes arrive at exactly the rate they are spent, which is equally true of a path that cannot go
/// faster and of a player that has decided not to ask. A peak far above the film's own rate rules the
/// path out.
struct DiagnosticsOverlay: View {
    let diagnostics: PlaybackDiagnostics

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            row("клиент", Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "—")
            row("позиция", String(format: "%.0f с", diagnostics.position))
            row("буфер впереди", String(format: "%.1f с", diagnostics.bufferAhead),
                warn: diagnostics.bufferAhead < 15)
            row("замираний", "\(diagnostics.stalls)", warn: diagnostics.stalls > 0)

            if diagnostics.recoveries > 0 {
                row("растормошён", "\(diagnostics.recoveries)", warn: true)
            }
            row("поспевает", diagnostics.keepingUp ? "да" : "НЕТ", warn: !diagnostics.keepingUp)

            // The journal AVFoundation keeps and nothing here used to read. A player that stopped
            // asking for anything looks healthy from every other angle, so if it recorded a reason
            // this is where it is.
            if let error = diagnostics.lastError {
                row("ошибок плеера", "\(diagnostics.errors)", warn: true)
                Text(error)
                    .font(.system(size: 18, design: .monospaced))
                    .foregroundStyle(.orange)
                    .lineLimit(2)
                    .frame(maxWidth: 900, alignment: .leading)
            }

            Divider().background(.white.opacity(0.3))

            if let resolve = diagnostics.resolveSeconds {
                row("сервер ответил", String(format: "%.1f с", resolve))
            }

            if let open = diagnostics.openSeconds {
                row("первый кадр через", String(format: "%.1f с", open), warn: open > 5)
            }

            row("приток", String(format: "%.0f Мбит/с", diagnostics.inflow))
            row("пик притока", String(format: "%.0f Мбит/с", diagnostics.peakInflow))
            row("нужно фильму", needed)
            row("скачано", String(format: "%.2f ГБ", diagnostics.transferredGB))

            // What only the loader knows. Absent when the player fetches for itself.
            if let requests = diagnostics.serverRequests {
                row("окно", String(
                    format: "%.0f МБ, впереди %.0f МБ", diagnostics.windowMB ?? 0, diagnostics.aheadMB ?? 0))
                row("запросов к серверу", "\(requests)")
                if let details = diagnostics.loaderDetails {
                    // The readers the window keeps, and how far apart they are. Two readers tens of
                    // megabytes apart is what the third run found; one is a window following the
                    // wrong thing again.
                    row("читателей", String(
                        format: "%d, разнос %.0f МБ", details.readers, Double(details.readerSpread) / 1_000_000),
                        warn: details.readers < 2)
                    row("отдельно", "позади \(details.asideBehind), впереди \(details.asideAhead), ≤64К \(details.asideSmall)")
                    if details.asides > 0 {
                        row("средний отдельный запрос", String(format: "%.0f КиБ",
                            Double(details.asideRequestedBytes) / Double(details.asides) / 1024))
                    }
                    if let reset = details.lastRestart {
                        row("последний сброс окна", String(format: "%.0f–%.0f → %.0f МБ; %d Б%@",
                            Double(reset.windowStart) / 1_000_000,
                            Double(reset.windowEnd) / 1_000_000,
                            Double(reset.offset) / 1_000_000, reset.requestedLength,
                            reset.toEnd ? " до конца" : ""))
                    }
                }
                // The two ways the window can be fought over. Either climbing during steady playback
                // means it is following the wrong reader.
                row("окно двигалось", "\(diagnostics.windowRestarts) раз, отдельно \(diagnostics.asideFetches)",
                    warn: diagnostics.asideFetches > 20)
            }
            row("плеер считает", String(format: "%.0f Мбит/с", diagnostics.observedMbps))

            if diagnostics.lowestBuffer.isFinite {
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

    /// What a second of this film costs on the wire: everything fetched over everything watched.
    ///
    /// Watched, not the position — a resume starts an hour in, and dividing this session's bytes by an
    /// hour nobody fetched would report a fraction of the real cost and send the diagnosis the wrong way.
    ///
    /// Not the chosen tracks' bitrate either: the container hands over the untouched file, so a source
    /// with eleven dubs is paid for in full to hear one. Held back until enough has played to mean
    /// something, since the header alone is megabytes and would read as a wildly expensive first second.
    private var needed: String {
        guard diagnostics.watched > 20, diagnostics.transferredGB > 0 else { return "—" }
        let mbps = diagnostics.transferredGB * 8000 / diagnostics.watched
        return String(format: "%.0f Мбит/с", mbps)
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
