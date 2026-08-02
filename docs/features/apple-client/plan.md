# Apple Client — plan

Status: Draft
Created: 2026-08-02
Updated: 2026-08-02

> **Umbrella epic.** This document owns the decisions, the platform split, and the
> playback spike that everything else depends on. The features it spans keep their
> own folders and their own PRs; this plan links to them and never restates their
> deliverables.

## Goal

A first-party client for Apple TV, macOS, iPadOS and iOS that replaces Infuse and
exposes what this app actually holds, over an API of our own instead of the
Jellyfin projection.

The [Jellyfin surface](../jellyfin-compatibility/feature.md) **stays** — unchanged
and supported — as the alternative for Infuse and any other third-party client.
Nothing in this epic removes or narrows it.

## Why a client of our own

The Jellyfin projection is a lowest-common-denominator protocol: folders, items,
streams. This app's domain is wider, and the parts that do not fit are either
invisible or expressed as workarounds:

| What the server holds | What the Jellyfin projection can carry |
| --- | --- |
| [Sidecar audio tracks](../external-track-sidecars/feature.md) (`.mka` dubs) | Nothing — Infuse cannot use external audio by any route |
| Sidecar subtitles | The stream is announced with no delivery URL (see [external-subtitle-delivery](../external-subtitle-delivery/plan.md)) |
| Named editions (`MediaSource.VersionName`) | A `MediaSources` array Infuse plays position-0 of; the default-version pin is a reordering workaround |
| [People](../metadata/feature.md) with biography, photos, filmography | A flat `/Persons` list, no pages |
| [Recommendations](../recommendation-providers/feature.md) with per-title provenance | A synthetic `CollectionFolder` with a null `CollectionType` and a flat list |
| [Release tracking](../release-tracking/feature.md), reminders, watchlist | Nothing |
| [Watch-history diary](../watch-history-calendar/feature.md) (per-play `PlaybackHistoryEntries`) | One boolean per item |
| Ingest review queue, torrents, [conversions](../convert-dialog/feature.md) | Nothing |
| Catalogs as a first-class concept | `CollectionFolder`, semantics lost |
| Probe provenance ([probe providers](../media-probe-providers/feature.md)) | Nothing |
| [Tombstones](../library-item-tombstones/feature.md) — a ready delta-sync feed | Full rescans |

## Decisions

Settled in discussion on 2026-08-02; recorded here so they are not re-litigated.

### 1. Playback engine: AVPlayer only

No second decoder stack (VLCKit, libmpv). The client plays what AVFoundation
decodes, and everything else is solved on the server side or not at all.

What this buys: Dolby Vision and HDR10, E-AC-3/Atmos passthrough, the native tvOS
transport UI, AirPlay, PiP, Now Playing, and power efficiency that a software
decoder on an Apple TV cannot match.

What it costs, accepted deliberately:

- **DTS, DTS-HD and TrueHD are not supported in v1.** They are rare in this
  library, and neither AVFoundation nor HLS carries them. A source whose *only*
  audio is one of those is not playable by this client; it stays playable in
  Infuse. The cheap escape hatch, if it turns out to matter, is an audio-only
  re-encode to E-AC-3 while the video is copied — one cheap pass, no HDR risk —
  but it is out of scope until a real file demands it.
- **PGS and VobSub subtitles cannot be rendered.** HLS carries WebVTT and IMSC1;
  bitmap subtitles need either an OCR conversion or a custom overlay above
  `AVPlayerLayer`. Out of scope for v1: text subtitles (SRT/ASS→WebVTT) cover the
  sidecars this library actually has.

### 2. The container gap is closed by remux, not by a second decoder

Most of this library is MKV holding H.264/HEVC plus AC-3/E-AC-3/AAC — codecs
AVFoundation decodes, in a container it refuses. That is a **packaging** problem,
not a transcoding one, and a stream copy solves it.

- Files already in `.mp4` / `.m4v` / `.mov` with supported codecs keep the current
  path: byte-range direct play from the existing stream endpoint. No packaging.
- Everything else is served as **HLS with fMP4 (CMAF) segments**, produced by
  stream copy on demand.
- Packaging runs in the **`transcode-engine` app**, not in `api`: `api` has
  deliberately shipped without ffmpeg since [external track
  sidecars](../external-track-sidecars/feature.md), and the engine already has
  ffmpeg, the shared-mount contract, a job/SSE API, and cross-app discovery via
  `HOSTY_DEPENDENCY_TRANSCODE_ENGINE_URL`. This extends the engine with a
  *session* model beside its existing *job* model — the "live transcode" epic its
  own idea document deferred, narrowed to stream copy.
- The engine is not publicly exposed, so `api` proxies playlist and segment
  requests to it. Authorization stays where it already is: item id → catalog
  sandbox → user access.

This is the highest-risk decision in the epic, which is why the spike below comes
before anything else is built.

### 3. Distribution: local builds and TestFlight

No App Store submission. Consequences worth stating once: a paid Apple Developer
Program membership is required for TestFlight and for installs that outlive
free provisioning's 7 days; and no App Store review means no licensing constraint
on dependencies — which the AVPlayer-only decision makes moot anyway.

### 4. The client lives in this repository

Under `src/apple/`, beside `src/api` and `src/web`. It is **not** a service in
`manifest.json` — it ships through Xcode, not through Hosty Core — so it carries
its own marketing version and build number, independent of the app version the
repository's versioning rule governs.

### 5. Jellyfin compatibility is kept as an alternative

Every server-side change in this epic is additive. A regression in the Jellyfin
surface is a bug in this epic, not an accepted cost.

### 6. Platform split

Acquisition and encoding are operator work and stay on the desktop. The consuming
platforms see conversion only as "download this in a smaller quality".

| Capability | macOS | iOS / iPadOS | tvOS |
| --- | --- | --- | --- |
| Browse, search, people, collections | ✅ | ✅ | ✅ |
| Playback (direct + packaged) | ✅ | ✅ | ✅ |
| Discovery: recommendations, calendar, reminders, diary | ✅ | ✅ | ✅ (read-mostly) |
| Download a device-profile copy (user picks quality) | ✅ | ✅ | ❌ |
| Add torrents, pick catalog, watch download progress | ✅ | ❌ | ❌ |
| Convert / merge, transcode queue, storage view | ✅ | ❌ | ❌ |
| Ingest review queue (`NeedsReview` matching) | ✅ | ❌ | ❌ |

**Why tvOS has no downloads.** Apple TV gives an app no dependable persistent
storage for multi-gigabyte files — the caches directory is purgeable at any time
and on-demand resources are capped. A "download" there would be a copy the system
may delete before the flight. Apple TV streams; it is the one platform that is
always on the same network as the server anyway.

## Constituent features

Each gets its own folder, its own `plan.md`, and its own PR under the
one-PR-per-feature rule. They are listed here with their boundary, not their
deliverables.

1. **`native-client-api`** *(server)* — the `/native/v1` surface: Core's own
   device authorization flow in place of the Jellyfin PIN credential; cursor-based
   delta sync over items and tombstones; a DTO that carries catalogs, named
   editions, sidecar tracks *with delivery URLs*, chapters, segments, people ids,
   collection membership and probe provenance; client capability negotiation
   (replacing the `EnableDirectPlay` flags the Jellyfin surface parses and
   ignores); per-user track preferences scoped to an item or a series; and richer
   playback sessions feeding `PlaybackHistoryEntries`. Generated OpenAPI, so the
   Swift client cannot drift from the server.
2. **`remux-streaming`** *(server + `transcode-engine`)* — the packaging session
   described in decision 2.
3. **`apple-client-core`** — the shared `MediaKit` package (domain, networking,
   local SQLite mirror, playback), pairing and profile storage in the Keychain,
   library browsing, the version picker, playback with track selection and
   resume. The first shippable app on all four platforms.
4. **`apple-client-discovery`** — recommendations with provenance, release
   calendar and reminders, people pages, the watch diary, and the title-preview
   surface for titles the instance does not hold.
5. **`apple-client-offline`** *(iOS/iPadOS)* — pick a quality, the server submits
   a transcode job, a notification when it is ready, background download, smart
   season retention, offline progress that syncs back.
6. **`apple-client-macos-operator`** — torrents, the convert/merge dialog, the
   transcode queue, the ingest review queue, and the storage view.
7. **`apple-client-platform`** — Top Shelf, widgets, Live Activities, App
   Intents/Shortcuts, Spotlight, Handoff, SharePlay, and the Watch remote.

## Deliverables

The umbrella's own work. Everything else belongs to the features above.

### Phase 0 — the playback spike

The whole epic rests on decision 2 being true. This phase answers it with a
throwaway spike, on real hardware and real files, before any surface is designed.

- [ ] **Packaging prototype** — a script (not the engine, not yet) that turns a
      real 4K HDR MKV remux from this library into an HLS/fMP4 playlist by stream
      copy, and a second one for a 1080p H.264 + AC-3 file.
- [ ] **Playback on an Apple TV 4K** of both, confirming: picture (Dolby Vision
      and HDR10 flagged correctly, not tone-mapped to SDR), E-AC-3/Atmos reaching
      the receiver as passthrough, seeking that lands where it is asked, and no
      stutter on a 60–80 Mbit remux over the real network.
- [ ] **Segment-boundary answer** — the hard part. Segments must start on
      keyframes, so the playlist needs a keyframe index; measure what building one
      costs per file, whether it can be derived at probe time and persisted beside
      the other probe data, and what a file with sparse or irregular keyframes
      does to segment duration.
- [ ] **Session lifecycle answer** — what a seek costs, what is cached and for how
      long, what cleans up after a client that vanishes mid-file, and how many
      concurrent sessions one engine container sustains.
- [ ] **Written outcome** in this document: the design that survived, or the
      decision to fall back (candidates, in order: pre-packaged fMP4 as a second
      `MediaSource` at the cost of disk; a second decoder stack after all;
      accepting that MKV plays only in Infuse).

### Phase 0.1 — foundations

- [ ] **Repository layout** — `src/apple/` with the `MediaKit` package and the
      Xcode project, plus a README covering signing and the TestFlight lane.
- [ ] **CI decision** — whether the client builds in GitHub Actions (macOS
      runners cost more than the Linux ones this repo uses today) or stays a local
      build until it stabilises. Whatever is chosen, the existing `api`/`web`
      workflows must not slow down.
- [ ] **Versioning note in `AGENTS.md`** — the client versions independently of
      `manifest.json`; the rule as written assumes everything ships through the
      manifest.
- [ ] **Constituent plans** — `native-client-api` and `remux-streaming` written
      and approved before either is built; the client-side plans follow once the
      API shape is settled.

### Closing the plan

- [ ] **`feature.md` for the umbrella** describing the client as a whole, created
      when the first client behaviour ships.
- [ ] **Index** — `node scripts/docs-index.mjs --fix`.
- [ ] **Version** — this document alone is documentation-only: no version bump.
      Each constituent feature bumps `manifest.json` only if it changes the server
      app; a PR touching nothing but `src/apple/` does not.

## Sequencing

```text
Phase 0  spike ─────────────► decision on remux
                                   │
Phase 1  native-client-api ────────┴──► remux-streaming        (server, parallel-ish)
Phase 2  apple-client-core          (tvOS + iOS + iPadOS + macOS: browse & play)
Phase 3  apple-client-discovery
Phase 4  apple-client-offline       (iOS/iPadOS)
Phase 5  apple-client-macos-operator
Phase 6  apple-client-platform
```

Phase 2 is the first release worth using. Phases 3–6 are independent of each other
and can be reordered by appetite.

## Open questions

- **Does the packaging path hold for a 60–80 Mbit 4K remux over Wi-Fi?** Phase 0
  answers it. If segment production cannot keep ahead of playback, the fallback
  order in Phase 0's last deliverable applies.
- **Where does the keyframe index live?** Derived per session (simple, repeated
  cost) or persisted beside the probe data (needs a migration and a story for
  files probed by the header reader, which never sees packets).
- **Do sidecar audio tracks survive packaging?** They are separate files; ffmpeg
  can take them as extra inputs in the same copy pass, which would make "watch
  with the Russian dub, no merge" work on Apple TV — the single most valuable
  thing Infuse cannot do. Whether that composes cleanly with on-demand segmenting
  is unverified.
- ~~**Pairing UX on tvOS.**~~ Settled: Core already ships a device authorization
  flow with approval in Shell, and the app-identity exchange composes on top of
  it, so this app writes no authentication code. See
  [native-client-api](../native-client-api/plan.md#authentication-hostys-device-flow-not-one-of-our-own).
- **Push notifications** — reminders and "your download is ready" want APNs, which
  needs a push certificate and a sender. Whether that belongs to this app or to
  Hosty Core is a platform question, not a client one; see [Hosty platform
  requests](../hosty-platform-requests/feature.md).
- **Ingest review on iPad** — excluded above because it is operator work, but it
  is the one operator surface that is genuinely pleasant on a couch. Reconsider
  after Phase 5.
- **How much of `web` gets re-litigated.** The client and the web UI will disagree
  about details (sorting, filters, what a detail page shows). The plan assumes the
  client follows `web`'s decisions rather than inventing its own.

## Verification steps

1. Phase 0 is verified on hardware, not in tests: an Apple TV 4K, a real remux,
   a receiver that reports the incoming audio format, and a TV that reports
   Dolby Vision.
2. Server-side features carry xUnit coverage as usual (Imposter for the engine
   client, the sandbox, and the token store), plus a live check that the Jellyfin
   surface still answers identically — Infuse must keep working through every
   phase.
3. Client-side: unit tests over `MediaKit` (sync cursor handling, tombstone
   application, capability negotiation, preference resolution), and manual
   verification per platform, because playback correctness is not testable in CI.
4. Every phase ends with the same live check: Infuse and the native client
   pointed at the same library, playing the same title, agreeing on resume
   position and watched state.
