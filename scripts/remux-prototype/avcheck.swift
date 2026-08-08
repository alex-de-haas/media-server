// Phase 0 spike: does AVFoundation actually accept the stream-copied HLS output?
// Loads the playlist exactly as an AVPlayer-based client would, waits for the
// item to become playable, and reports what the pipeline resolved.
import AVFoundation
import Foundation

let url = URL(string: CommandLine.arguments[1])!
let item = AVPlayerItem(url: url)
let player = AVPlayer(playerItem: item)
player.isMuted = true

let started = Date()
var done = false
let deadline = Date().addingTimeInterval(400)

// Actually start playback: HLS resolves its variant lazily, so an item that is
// merely instantiated proves nothing.
player.play()

while !done && Date() < deadline {
    RunLoop.current.run(mode: .default, before: Date().addingTimeInterval(0.25))
    switch item.status {
    case .readyToPlay:
        let size = item.presentationSize
        print("status:      readyToPlay")
        print(String(format: "ready after: %.2fs", Date().timeIntervalSince(started)))
        print("resolution:  \(Int(size.width))x\(Int(size.height))")
        print("duration:    \(String(format: "%.2f", CMTimeGetSeconds(item.duration)))s")
        for track in item.tracks {
            guard let desc = track.assetTrack?.formatDescriptions.first else { continue }
            let fd = desc as! CMFormatDescription
            let type = CMFormatDescriptionGetMediaType(fd)
            let sub = CMFormatDescriptionGetMediaSubType(fd)
            func fourCC(_ v: FourCharCode) -> String {
                String(bytes: [UInt8(v >> 24 & 255), UInt8(v >> 16 & 255),
                               UInt8(v >> 8 & 255), UInt8(v & 255)], encoding: .ascii) ?? "?"
            }
            var extra = ""
            if type == kCMMediaType_Video,
               let ext = CMFormatDescriptionGetExtensions(fd) as? [String: Any] {
                let transfer = ext["TransferFunction"] as? String ?? "-"
                let primaries = ext["ColorPrimaries"] as? String ?? "-"
                extra = "  transfer=\(transfer) primaries=\(primaries)"
            }
            print("track:       \(fourCC(type))/\(fourCC(sub))\(extra)")
        }
        // Prove frames are really moving, not just that the item reported ready.
        Thread.sleep(forTimeInterval: 1.5)
        let played = CMTimeGetSeconds(item.currentTime())
        print("played:      \(String(format: "%.2f", played))s after 1.5s of playback")
        print(played > 0.3 ? "RESULT:      PLAYS" : "RESULT:      READY BUT NOT ADVANCING")
        done = true
    case .failed:
        print("status:      failed")
        print("error:       \(item.error?.localizedDescription ?? "unknown")")
        if let underlying = (item.error as NSError?)?.userInfo[NSUnderlyingErrorKey] {
            print("underlying:  \(underlying)")
        }
        print("RESULT:      REJECTED")
        done = true
    default:
        break
    }
}

if !done {
    print("RESULT:      TIMED OUT (still .unknown after 25s)")
    exit(2)
}
