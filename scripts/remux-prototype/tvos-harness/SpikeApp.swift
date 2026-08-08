// A throwaway tvOS harness for judging playback by instrument rather than by eye.
//
// It exists because an earlier version of this spike ran every case in one launch and
// reported "PLAYS" for a case that showed a badge and no picture — its hold phase was
// never logged at all. So: one case per cold launch, a full-screen AVPlayerViewController
// with nothing composited above it (an embedded player draws into the app's SDR layer and
// the television then reports the interface's format, not the content's), and everything
// that separates "the pipeline is running" from "the screen has pixels" written to the
// console.
//
// Point `base` at the machine running serve.py, set `sequence` to the path to play, then:
//
//   xcodebuild -project TVSpike.xcodeproj -scheme TVSpike -configuration Debug \
//     -destination 'platform=tvOS,id=<device-udid>' -derivedDataPath dd \
//     -allowProvisioningUpdates TVSPIKE_TEAM=<your-team-id> build
//   xcrun devicectl device install app --device <device-udid> \
//     dd/Build/Products/Debug-appletvos/TVSpike.app
//   xcrun devicectl device process launch --device <device-udid> --console \
//     --terminate-existing com.haas.mediaserver.tvspike
//
// The log cannot tell you the dynamic range. Only the television's own info panel can,
// which is why the run ends by holding the picture on screen.
import AVFoundation
import AVKit
import UIKit

// Where serve.py is running, and what to play. Both are overridable at launch, so the
// harness does not have to be edited for every run:
//
//   xcrun devicectl device process launch --device <udid> --console --terminate-existing \
//     --environment-variables '{"SPIKE_BASE":"http://10.0.0.2:8975","SPIKE_PATH":"movie.mp4"}' \
//     com.haas.mediaserver.tvspike
let env = ProcessInfo.processInfo.environment
let base = env["SPIKE_BASE"] ?? "http://127.0.0.1:8975"
let sequence = [env["SPIKE_PATH"] ?? "movie.mp4"]   // one case per launch, deliberately

@main
final class AppDelegate: UIResponder, UIApplicationDelegate {
    var window: UIWindow?
    let player = AVPlayer()

    func application(
        _ application: UIApplication,
        didFinishLaunchingWithOptions options: [UIApplication.LaunchOptionsKey: Any]? = nil
    ) -> Bool {
        let controller = AVPlayerViewController()
        controller.player = player
        controller.showsPlaybackControls = false

        let window = UIWindow(frame: UIScreen.main.bounds)
        window.rootViewController = controller
        window.makeKeyAndVisible()
        self.window = window

        Task { @MainActor in await run() }
        return true
    }

    private func log(_ s: String) { print(s) }

    @MainActor
    private func run() async {
        log("device: \(ProcessInfo.processInfo.operatingSystemVersionString)")
        for casePath in sequence {
            log("")
            log("=== \(casePath) ===")
            await play(casePath)
        }
    }

    @MainActor
    private func play(_ casePath: String) async {
        let item = AVPlayerItem(url: URL(string: "\(base)/\(casePath)")!)
        player.replaceCurrentItem(with: item)
        player.play()

        for tick in 0..<20 {
            try? await Task.sleep(for: .milliseconds(500))

            let status: String
            switch item.status {
            case .readyToPlay: status = "ready"
            case .failed:      status = "FAILED"
            default:           status = "unknown"
            }

            // Three things separate a running decoder from a lit screen: the rate and clock advancing,
            // a video track having actually been selected, and that track being enabled.
            let videoTracks = item.tracks.filter { $0.assetTrack?.mediaType == .video }
            let enabled = videoTracks.filter(\.isEnabled).count
            let size = item.presentationSize

            if tick % 4 == 0 || item.status == .failed {
                log(String(
                    format: "t=%4.1fs status=%@ rate=%.1f time=%6.2f size=%dx%d video=%d(on %d) keepUp=%@",
                    Double(tick) * 0.5, status, player.rate,
                    CMTimeGetSeconds(item.currentTime()),
                    Int(size.width), Int(size.height),
                    videoTracks.count, enabled,
                    item.isPlaybackLikelyToKeepUp ? "y" : "n"))
            }

            if item.status == .failed {
                log("error: \(item.error?.localizedDescription ?? "unknown")")
                break
            }
        }

        let targets: [Double] = [3600, 600, 7900]
        for target in targets {
            let began = Date()
            await player.seek(to: CMTime(seconds: target, preferredTimescale: 600))
            player.play()
            var resumed = false
            for _ in 0..<80 {
                try? await Task.sleep(for: .milliseconds(100))
                let now = CMTimeGetSeconds(item.currentTime())
                if player.rate > 0 && now > target + 0.15 {
                    log(String(format: "seek->%6.0fs resumed in %.2fs at %.1f", target,
                               Date().timeIntervalSince(began), now))
                    resumed = true
                    break
                }
            }
            if !resumed {
                log(String(format: "seek->%6.0fs DID NOT RESUME in 8s (rate=%.1f pos=%.1f)",
                           target, player.rate, CMTimeGetSeconds(item.currentTime())))
            }
        }

        if let event = item.accessLog()?.events.last {
            log("")
            log("access: observed \(String(format: "%.1f", event.observedBitrate / 1_000_000)) Mbit/s, "
                + "stalls \(event.numberOfStalls), dropped \(event.numberOfDroppedVideoFrames), "
                + "segments \(event.numberOfSegmentsDownloaded)")
        }
        for error in item.errorLog()?.events ?? [] {
            log("errorlog: \(error.errorStatusCode) \(error.errorComment ?? "")")
        }


        log("")
        log(">>> HOLDING — read the TV's info panel now.")
        while true { try? await Task.sleep(for: .seconds(5)) }
    }
}
