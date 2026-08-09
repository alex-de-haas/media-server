# Apple clients

The first-party client for tvOS, and later macOS, iPadOS and iOS. It exists because
AVFoundation will not open Matroska and this library is Matroska — see
[`docs/features/apple-client/plan.md`](../../docs/features/apple-client/plan.md).

```text
src/apple/
├── MediaKit/            — Swift package: everything shared, nothing that draws
└── MediaServerTV/       — the tvOS app, alongside MediaServerTV.xcodeproj
```

`MediaKit` is a package rather than a framework target so it builds and tests on its own.
The logic worth testing is the logic with no screen, and `swift test` runs it in a second
without a simulator.

## Building

```bash
cd src/apple/MediaKit && swift test
```

```bash
xcodebuild -project src/apple/MediaServerTV.xcodeproj -scheme MediaServerTV -destination 'generic/platform=tvOS Simulator' build CODE_SIGNING_ALLOWED=NO
```

Or open `MediaServerTV.xcodeproj` in Xcode; the package resolves from the folder beside it,
so there is nothing to fetch.

The project file is written by hand and kept small by Xcode's synchronized file groups — a
file added to `MediaServerTV/` appears in the target without the project file changing.
That is deliberate: XcodeGen or Tuist would be a tool everyone has to install before they
can build, and this project is small enough not to need one.

Requires Xcode 26 or later for the tvOS 26 SDK.

## Running on the simulator

```bash
xcrun simctl boot 'Apple TV 4K (3rd generation)'
```

```bash
xcrun simctl install booted path/to/MediaServerTV.app && xcrun simctl launch booted com.haas.mediaserver.tv
```

**The simulator cannot answer the question this client is built around.** It reports no
HDR-eligible output, so `CapabilityProfile.current()` returns `["SDR"]` there, and Dolby
Vision cannot be seen, engaged or ruled out. That is correct behaviour and not a bug — but
it means every Dolby Vision claim has to be checked on real hardware, which is how every
measurement in the plan was taken.

## CI

The client is **not** built in CI, decided on 2026-08-09. macOS runners cost roughly ten
times what the Linux ones this repository already uses cost, and until there is a client
worth protecting they would break more often than they would catch anything. The existing
`api` and `web` workflows are untouched by this directory.

Revisit when the app has users other than its author.

## Signing and TestFlight

Nothing is signed yet. Local simulator builds pass `CODE_SIGNING_ALLOWED=NO` and need no
account. Device builds and a TestFlight lane need an Apple Developer membership, which the
distribution decision in the plan assumes but which has not been set up — when it is, the
team identifier and the lane belong in this section.

## Versioning

The client versions independently of `manifest.json`, which describes the Hosty runtime app
and nothing else. `MARKETING_VERSION` in the Xcode project is the client's own version. See
`AGENTS.md`.
