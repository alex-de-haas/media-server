# Apple Client

Created: 2026-08-10
Updated: 2026-08-10

The first-party client for Apple platforms. It exists because AVFoundation will not open
Matroska and this library is Matroska — the server answers that by
[repackaging](../remux-streaming/feature.md), and this is what asks.

Today it is a tvOS app that reports what the device it is running on can play. That is a
small thing to ship, and it is deliberately the *first* thing: the whole design turns on
whether a given box does Dolby Vision, and that is the one question no laptop can answer.
What remains is planned in [`apple-client-core`](../apple-client-core/plan.md) and tracked
by the [epic](plan.md).

## Layout

```text
src/apple/
├── MediaKit/            — Swift package: everything shared, nothing that draws
└── MediaServerTV/       — the tvOS app, alongside MediaServerTV.xcodeproj
```

`MediaKit` is a package rather than a framework target so it builds and tests on its own:
the logic worth testing is the logic with no screen, and `swift test` runs it in a second
without a simulator.

The Xcode project is written by hand and kept small by synchronized file groups — a file
added to `MediaServerTV/` joins the target without the project file changing. XcodeGen or
Tuist would be a tool everyone must install before they can build, which a project this
size does not earn.

## The capability profile

`CapabilityProfile.current()` fills in the five axes the server negotiates on — container,
video codec, audio codec, dynamic range, channel count — from what the hardware reports,
not from what the device is called. A model table would age every autumn, and the platform
split guarantees more than one device class.

| Asked | Answers |
| --- | --- |
| `VTIsHardwareDecodeSupported(kCMVideoCodecType_DolbyVisionHEVC)` | whether the box decodes Dolby Vision |
| `AVPlayer.eligibleForHDRPlayback` | whether the output chain is eligible for HDR at all |

**Dolby Vision is claimed only when both hold.** Decode support with no HDR-eligible output
would ask the server for a `dvh1` entry the display cannot show, and the spike established
that such a track does not degrade gracefully — it breaks.

Two things are never claimed:

- **Matroska**, because that it cannot be opened is the entire reason the server repackages.
- **AV1**, which recent hardware decodes and the server has no sample entry for. Claiming it
  would earn a refusal at the request instead of an honest `unsupported` at resolve time.

The device is reached through a `DeviceCapabilities` protocol, so every branch is testable
without hardware. On macOS `presentsHDR` is `false` rather than assumed: the honest answer
lives on the screen a window is on, which a synchronous property has no business reaching
for and which means nothing before there is a window. Under-claiming costs an SDR picture
that always works; over-claiming breaks one.

## The escape hatch

One thing genuinely cannot be detected. `VTIsHardwareDecodeSupported` reports what the
*box* decodes, not what the *panel* shows, and a 4K box behind a receiver that strips the
signalling still says yes. The symptom is a washed-out or dark picture and, without a
switch, a bug report with nothing to act on.

`PlaybackPreferences` offers `automatic / hdr10 / sdr`, and it **narrows the detected
profile before it is sent** — so the server's own negotiation does the work and there is no
second decision path to keep in step. An override can only ever narrow: a viewer choosing
HDR10 on a device that reports none still gets SDR.

The choice is persisted through `PlaybackPreferencesStore`, in `UserDefaults` rather than
the Keychain, because nothing here is a secret and a preference that survived a reinstall
would not be wanted. A switch that forgets is worse than no switch: someone who set SDR to
fix a dark picture would find it dark again next launch, having already tried the one
control offered.

There is no transport switch while there is one transport. A control that does nothing is
worse than an absent one, because it becomes the first thing a puzzled viewer changes.

## Building

```bash
cd src/apple/MediaKit && swift test
```

```bash
xcodebuild -project src/apple/MediaServerTV.xcodeproj -scheme MediaServerTV -destination 'generic/platform=tvOS Simulator' build
```

Requires Xcode 26 or later for the tvOS 26 SDK. Simulator builds sign themselves locally
and need no developer account; device builds and TestFlight do, and neither is set up.

The client is **not** built in CI, decided on 2026-08-09: macOS runners cost roughly ten
times the Linux ones already in use, and until there is a client worth protecting they
would break more often than they would catch anything. `MediaKit` is where the logic worth
protecting lives and it runs in a second locally. See [`src/apple/README.md`](../../../src/apple/README.md).

## Versioning

The clients ship through TestFlight rather than through Core, so `MARKETING_VERSION` in the
Xcode project is theirs and `manifest.json` is the server's. A change touching only
`src/apple/` leaves the manifest alone.

## Testing Expectations

- **The capability profile**, every branch, through the `DeviceCapabilities` protocol rather
  than on hardware: Dolby Vision claimed only when decode *and* HDR eligibility hold, HDR10
  alone on an older box, and neither on a device reporting no HDR.
- **What is never claimed** — Matroska and AV1 — asserted rather than assumed, since both
  are absences a later edit could silently undo.
- **The wire shape**, encoded and checked against the server's own field names, and an
  unstated channel limit absent from the body rather than sent as zero.
- **Overrides narrow and never widen**, for every case of the enum.
- **The store**: a choice survives a relaunch, a fresh install gets the automatic answer,
  and something written by a version with a different shape falls back rather than throwing.
- **The simulator cannot answer the Dolby Vision question** and never will, reporting no
  HDR-eligible output. Every claim about it is checked on an Apple TV 4K, which is how every
  measurement in the epic was taken.
