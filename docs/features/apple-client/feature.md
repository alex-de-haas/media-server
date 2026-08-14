# Apple Client

Created: 2026-08-10
Updated: 2026-08-14

The first-party client for Apple platforms. It exists because AVFoundation will not open
Matroska and this library is Matroska — the server answers that by
[repackaging](../remux-streaming/feature.md), and this is what asks.

Today it is a tvOS app that **pairs with a server** and reports what the device it is
running on can play. What remains — browsing and playback — is planned in
[`apple-client-core`](../apple-client-core/plan.md) and tracked by the [epic](plan.md).

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

## Pairing

A television has no keyboard worth the name, no browser, and no business showing a password.
So it shows a code, and a human approves it somewhere else.

The app writes **no** authentication of its own. Hosty Core already gives a browserless
client a full path into any runtime app, and this walks it:

1. The viewer types an address. `GET /native/v1/server/public` — the one anonymous route,
   because needing a token to discover where tokens come from is a loop — answers with the
   server's name, the app id and the Core origin, in a field spelled `coreOrigin`.
2. `POST {core}/api/auth/device/code` returns an eight-character code in a lookalike-free
   alphabet. It goes on the television at 120pt, with the address that approves it beside
   it — or, when the host runs no Shell and Core returns none, where to look instead.
3. Someone approves it in Shell → Settings → Access tokens, from anything with a browser.
4. `POST {core}/api/auth/device/token`, polled no faster than Core asks and no longer than
   the code lives, collects a Core access token.
5. `POST {core}/api/auth/apps/authorize` with that token as a **bearer** — a
   bearer-presented Core session is deliberately CSRF-exempt, which is exactly what lets a
   device with no cookie jar do this — then `POST {core}/api/auth/apps/token` narrows it to
   an app identity token.

One constraint had to be read out of Core rather than guessed: `redirectUri` on step 5 is
checked against the app's installed endpoint origins, even though nothing navigates and the
authorization code comes back in the body. The address the viewer typed *is* that origin,
so it is what gets sent.

The Core a device was approved against is **pinned at pairing time**. Refreshing never re-asks
an anonymous route where Core lives: the token about to be presented is the full-privilege
one, and an endpoint that could name its own origin could be handed a credential reaching
Core and every other app on the host. An origin that changes is a re-pairing, not a redirect.

### The credential worth being careful with is Core's own

Core has no scopes, so its access token carries its holder's full Core role — it can reach
Core itself and every other app on the host. The app identity token cannot: it is
audience-scoped, and it is the only one that ever leaves the device on a request to us.

Both live in the Keychain as **one** item, because a device holding one and not the other
is a state nothing knows how to resume from. `kSecAttrAccessibleAfterFirstUnlock`: a
television unlocks itself, and anything stricter would leave the app unable to read its own
credential on a cold boot.

The two lifetimes decide what a viewer sees. The app grant is seven days idle and thirty
absolute; Core's token is ninety days idle with no absolute expiry. So a lapsed app grant is
re-minted silently on launch, and the pairing screen comes back only when Core's own token
has gone — or when the account behind it stopped being assigned to this app, which is a
different sentence on screen.

### Every failure says which one it is

An address that answers but is not a Media Server, a server that cannot say where its Core
is, a Core too old for the device routes (they arrived in 0.73.0), a host already holding
too many pending requests, a code nobody approved in time, an approval declined, an account
with no access, a credential the host now refuses. "Could not sign in" is the answer that
helps nobody, so it is not one of them.

**Only a refusal forgets the pairing.** A revoked credential or an unassigned account is
over; a server that was asleep when the television woke up is not, and treating the second
like the first would mean re-pairing a device every time the network is slow at breakfast.
So a transient failure keeps the credential and stays paired.

## The server's surface is generated, not transcribed

Everything under `/native/v1` reaches this client through code generated from
[the committed OpenAPI document](../../../src/api/openapi/MediaServer.Api_native.json) by
Apple's `swift-openapi-generator`. `MediaKit`'s own `ServerBootstrap` is *built from* the
generated type rather than decoded by hand, so a surface that changes shape stops the client
compiling instead of failing quietly on a television.

This exists because the hand-written version got it wrong in the only way that mattered:
`surfaceVersion` was modelled as a number where the contract says string, which meant no
real server could be decoded at all — and the test fixture that should have caught it had
been written from the model rather than from the document. Both halves of the mistake are
things generation removes.

The Swift target holds a **symlink** to the document, not a copy, so the repository has one
and they cannot disagree. What can go stale is the generated code, so
`scripts/generate-apple-client.sh` records the document's hash beside it and CI compares
that on Linux — the generator needs a Mac, which is why the check is a hash rather than a
regeneration.

A generated client wraps both "nothing answered" and "something answered wrongly" in one
error type, and they are different sentences on screen — so the cause is unwrapped rather
than collapsed. A `URLError` underneath is a server that is asleep; anything else is a
surface this client does not recognise.

**Core's API is not generated**, because Core publishes no document. The pairing chain's four
calls are read by hand against Core's sources, which is a real difference in confidence
between the two halves and is worth remembering when either changes.

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

## Verified

The whole chain was run against production on 2026-08-14 — `media.zayats.io`, its Core at
`core.zayats.io`, approved in Shell — and every step answered: the bootstrap, the device
code, the poll that collected a 64-character Core token, and the exchange that narrowed it
to a 50-character app identity.

That mattered more than the usual "it works". Four of the six calls are written against
Core's source rather than a published document, because Core publishes none, so the stubbed
tests could only ever confirm what their author already believed. The `surfaceVersion`
mistake — modelled as a number, delivered as the string `"1"` — passed forty of them and
would have failed here on the first line. `LivePairingCheck` in the test target is kept for
exactly that, gated behind an environment variable so it can never run in CI.

## Browsing

Two tabs, Movies and Series, over a poster grid. Catalogs are mixed rather than shown as a
level of their own — whether a film sits on the SSD or the spinning disk is an operator's
concern, not a viewer's — and `catalogId` travels on every title so a filter can be laid
over this later.

**There is no route that lists a library.** `items/{id}` fetches one title and everything
else goes through `sync`, so browsing drains that feed into memory at launch. Nothing is
persisted: the schema, the cursor kept between launches, the reset when a cursor goes stale
and the tombstones are the expensive half of a local mirror, and none of it is built. A few
hundred titles cost a couple of requests.

**Artwork comes from this instance**, not from the provider's CDN that `posterUrl` names —
a client on the same network keeps working with no internet at all, and browsing stops being
visible to TMDb. That route is bearer-authenticated, which is why `AsyncImage` cannot be
used and there is a loader of our own.

A title's own screen is fetched when it opens: versions ordered so the default leads, audio
and subtitle tracks, and a mark against the ones beside the file rather than inside it.

A started title is marked as started and **not** with a progress bar. The feed carries a
resume position but no runtime, so there is no fraction to draw, and a full-width bar for
something stopped after a minute would be a worse lie than saying nothing.

Sign out and the dynamic-range override are a third tab. Both are answers to a symptom — the
wrong server, or a dark picture — and something that fixes a symptom has to be findable
while looking at it.

### The credential refreshes on a refusal, not on a clock

The app grant states an `expiresAt` thirty days out — its *absolute* cap — but it also
lapses after seven days idle, and nothing in the token says which comes first. A television
left alone for a week holds a credential that looks fresh and is not. Only a request can
tell, so a `401` re-mints the grant and retries once, in a middleware where the retry is
invisible to everything above it. Concurrent failures produce one exchange, not one each,
and a request carrying a body is never retried — an `HTTPBody` is a stream consumed by the
attempt that failed.

### The generated client was quietly dropping fields

`swift-openapi-generator` does not support a bare `null` schema, and ASP.NET describes a
nullable reference as `oneOf: [{ "type": "null" }, { "$ref": … }]`. The generator **skipped
those properties entirely**, with a warning nobody read — eight of them, including
`LibraryItemDto.userData`, which carries resume position and watched state, and
`NativePlaybackResolution.transport`, which decides how playback is delivered.

That is worse than either failure generating a client was meant to prevent: not a compile
error, not a decoding failure, just a field that silently is not there.
`NullableRefSchemaTransformer` on the server now rewrites those unions into the plain
reference every generator reads — the wire is unchanged, only its description — and
`scripts/generate-apple-client.sh` fails on any remaining "skipping" so the next one cannot
pass unnoticed.

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

- **The pairing chain**, against a transport of canned answers — because every failure it
  has to handle is a state a real Core will not produce on request. Each poll answer maps to
  its own outcome; `approved` with no token is treated as expired rather than looped on; the
  Core token is presented as a bearer; the redirect is the server's own origin; a missing
  device route reads as "this host is too old" rather than a bare 404.
- **The session**, as a state machine: a code reaches the screen before anyone is asked to
  wait, pending is waited through, cancelling stops the poll rather than leaving it asking
  in the background, a lapsed grant is re-minted silently, and a pairing that can no longer
  be refreshed is forgotten rather than half-kept.
- **What a viewer types**, which is not a URL: a bare host, an explicit scheme, a stray
  path, and nonsense that must be refused before anything reaches the network.
- **The bootstrap against the committed contract**, not against the model: `surfaceVersion`
  is a string and `coreOrigin` is nullable. A fixture written to match the model instead
  passed while the client could not decode a single real server. It now goes through the
  generated client, so the fixture is checked against the document's own shape.
- **That a refresh cannot be redirected**: a bootstrap naming a different Core must not
  receive the stored full-privilege token.
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
- **The credential refresh**: a `401` re-mints and retries with the new token, the result is
  stored, concurrent failures cause one exchange rather than several, and a refusal Core
  will not fix is passed through instead of retried.
- **Draining the feed**: every page is read, a feed claiming more without moving its cursor
  stops *before* taking the repeat rather than showing it twice, kinds this client does not
  list are dropped, and `userData` arrives — the last because it is exactly what the
  generator used to remove.
- **Which refusals end a pairing**: a revoked credential or a lost assignment forgets it, a
  server having a bad day does not. The distinction has to hold in both places that refresh,
  because the stored grant's absolute expiry outlives its idle window.
- **The simulator cannot answer the Dolby Vision question** and never will, reporting no
  HDR-eligible output. Every claim about it is checked on an Apple TV 4K, which is how every
  measurement in the epic was taken.
