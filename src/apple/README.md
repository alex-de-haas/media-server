# Apple clients

The first-party client for tvOS, and later macOS, iPadOS and iOS. It exists because
AVFoundation will not open Matroska and this library is Matroska — see
[`docs/features/apple-client/plan.md`](../../docs/features/apple-client/plan.md).

```text
src/apple/
├── MediaKit/
│   ├── Sources/MediaServerAPI/   — generated from the server's OpenAPI document
│   └── Sources/MediaKit/         — everything shared, nothing that draws
└── MediaServerTV/                — the tvOS app, alongside MediaServerTV.xcodeproj
```

`MediaKit` is a package rather than a framework target so it builds and tests on its own.
The logic worth testing is the logic with no screen, and `swift test` runs it in a second
without a simulator.

## Building

```bash
cd src/apple/MediaKit && swift test
```

```bash
xcodebuild -project src/apple/MediaServerTV.xcodeproj -scheme MediaServerTV -destination 'generic/platform=tvOS Simulator' build
```

Or open `MediaServerTV.xcodeproj` in Xcode; the package resolves from the folder beside it,
so there is nothing to fetch.

The project file is written by hand and kept small by Xcode's synchronized file groups — a
file added to `MediaServerTV/` appears in the target without the project file changing.
That is deliberate: XcodeGen or Tuist would be a tool everyone has to install before they
can build, and this project is small enough not to need one.

Requires Xcode 26 or later for the tvOS 26 SDK.

## Regenerating the API client

Everything under `/native/v1` reaches the client through code generated from the server's
OpenAPI document. Rerun this whenever that surface changes shape:

```bash
scripts/generate-apple-client.sh
```

`Sources/MediaServerAPI/openapi.json` is a **symlink** to `src/api/openapi/`, so there is
one document in the repository and the two cannot disagree. What can go stale is the
generated code, so the script records the document's hash beside it and CI compares that —
the generator needs a Mac, which is why the check is a hash rather than a regeneration.

Core's API is not generated: Core publishes no document, so the pairing chain is read by
hand against Core's sources.

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

Device builds use `Config/Signing.xcconfig` in both Debug and Release. It optionally
includes `Config/Signing.local.xcconfig`, which is ignored by Git and belongs to the
developer's checkout. Set up this file once to build directly from Xcode:

```bash
cp src/apple/Config/Signing.local.xcconfig.example src/apple/Config/Signing.local.xcconfig
```

Replace `YOUR_TEAM_ID` in the local file with your Apple Developer team identifier.
Xcode reads the file as a build setting; no shell profile or macOS environment setup
is required. Add your Apple account in Xcode Settings and ensure it has access to that
team. Change the local file to switch teams instead of selecting a different team in
the target editor, which can write an override into `project.pbxproj`.

For command-line builds, a build-setting argument overrides the local value:

```bash
xcodebuild -project src/apple/MediaServerTV.xcodeproj -scheme MediaServerTV \
  -destination 'platform=tvOS,name=<your Apple TV>' \
  MEDIASERVER_TEAM=XXXXXXXXXX -allowProvisioningUpdates build
```

`-allowProvisioningUpdates` can register the bundle identifier and update provisioning
with the developer account. It is not needed for unsigned compilation checks:

```bash
xcodebuild -project src/apple/MediaServerTV.xcodeproj -scheme MediaServerTV \
  -destination 'generic/platform=tvOS' CODE_SIGNING_ALLOWED=NO build
```

The optional include lets simulator/unsigned builds run without a local team file.
A signed device build or archive still requires an appropriate certificate, provisioning
profile and team access. TestFlight distribution requires Apple Developer membership.

## Versioning

The client versions independently of `manifest.json`, which describes the Hosty runtime app
and nothing else. `MARKETING_VERSION` in the Xcode project is the client's own version. See
`AGENTS.md`.
