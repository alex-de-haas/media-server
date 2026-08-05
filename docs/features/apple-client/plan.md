# Apple Client — plan

Status: In Progress
Created: 2026-08-02
Updated: 2026-08-05

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

- **DTS, DTS-HD and TrueHD are not supported in v1.** Neither AVFoundation nor
  HLS carries them, and the spike found none: every audio track across both
  sampled files is AC-3 or E-AC-3. A source whose *only*
  audio is one of those is not playable by this client; it stays playable in
  Infuse. The cheap escape hatch, if it turns out to matter, is an audio-only
  re-encode to E-AC-3 while the video is copied — one cheap pass, no HDR risk —
  but it is out of scope until a real file demands it.
- **PGS and VobSub subtitles cannot be rendered.** HLS carries WebVTT and IMSC1;
  bitmap subtitles need either an OCR conversion or a custom overlay above
  `AVPlayerLayer`. Out of scope for v1: text subtitles (SRT/ASS→WebVTT) cover the
  sidecars this library actually has, and the spike found every subtitle track in
  both sampled files to be SubRip.

### 2. The container gap is closed by remux, not by a second decoder

Most of this library is MKV holding H.264/HEVC plus AC-3/E-AC-3/AAC — codecs
AVFoundation decodes, in a container it refuses. That is a **packaging** problem,
not a transcoding one, and a stream copy solves it.

- Files already in `.mp4` / `.m4v` / `.mov` with supported codecs keep the current
  path: byte-range direct play from the existing stream endpoint. No packaging.
- Everything else is remuxed by stream copy into a **progressive MP4, served over
  byte ranges** — the same shape the existing stream endpoint already serves, not
  a second delivery mechanism.

  "Progressive" here means precisely what the spike tested: a **non-fragmented**
  MP4 whose `moov` sits before the media data, so a player can seek by range
  without reading the file end to end. Fragmented MP4 is a different artifact with
  a different index, and nothing here has tested it; the two must not be used as
  synonyms when the packaging feature is written.
- Packaging runs in the **`transcode-engine` app**, not in `api`: `api` has
  deliberately shipped without ffmpeg since [external track
  sidecars](../external-track-sidecars/feature.md), and the engine already has the
  tooling, the shared-mount contract, a job/SSE API, and cross-app discovery via
  `HOSTY_DEPENDENCY_TRANSCODE_ENGINE_URL`.
- The engine is not publicly exposed, so `api` proxies the byte-range requests.
  Authorization stays where it already is: item id → catalog sandbox → user access.

**HLS is not used — but not for the reason first recorded here.** This section
originally said that every master playlist tvOS was offered failed to open and that
dynamic range was therefore unreachable over HLS. A second pass on 2026-08-05
disproved both halves: a hand-written master does open, and Apple's own published
stream plays in Dolby Vision on this same television. See [the second HLS
pass](#second-hls-pass-2026-08-05).

What survives is the narrower engineering argument. A progressive MP4 reaches Dolby
Vision with no playlist to get wrong, no segmenting, and no session lifecycle — the
dynamic-range signalling rides in the `moov`. HLS earns its complexity when there
are bitrate ladders to switch between, and there are none here because nothing is
re-encoded. And HLS does not reach the goal for this content anyway: five
configurations were measured and **none delivered this library's profile 8.1 as
Dolby Vision** — HDR10 is all HLS gives here.

**Confirmed end to end on an Apple TV 4K.** 4K HEVC at 26.5 Mbit/s plays with no
stalls, the display switches, and the picture is Dolby Vision. See [the
results](#results-of-the-local-pass-2026-08-03) and [the device
pass](#device-pass-on-an-apple-tv-4k-2026-08-03).

### 3. Distribution: local builds and TestFlight

No App Store submission. Consequences worth stating once, because TestFlight is a
beta channel and not a quiet way to keep an app installed:

- A paid Apple Developer Program membership is required for TestFlight and for
  installs that outlive free provisioning's seven days.
- **A TestFlight build expires after 90 days**, and the first build distributed to
  external testers goes through App Review. So this is a release cadence with CI
  behind it, not a one-time upload.
- Dependency licences bind regardless of the distribution channel — skipping App
  Review does not relax them. (Moot in practice, since the AVPlayer-only decision
  leaves no third-party decoder to license.)

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
may delete before the flight. Apple TV streams.

## Constituent features

Each gets its own folder, its own `plan.md`, and its own PR under the
one-PR-per-feature rule. They are listed here with their boundary, not their
deliverables.

The split below is deliberately finer than the first draft, which put the whole
server surface in one feature and all four platforms in the next. Each slice here
still delivers something observable on its own — the rule is one PR per feature,
not one PR per layer.

1. **`native-client-api`** *(server)* — everything up to **browsing**: Core's own
   device authorization flow in place of the Jellyfin PIN credential, the public
   binding's allowlist, delta sync over a monotonic change log, the item DTO
   (catalogs, named editions, sidecar tracks *with delivery URLs*, chapters, people
   ids, collection membership, probe provenance), and thin routes for
   recommendations, the calendar, reminders, people, history and the realtime
   stream. Generated OpenAPI, so the Swift client cannot drift from the server.
2. **`native-playback`** *(server)* — capability negotiation, per-user track
   preferences, and playback sessions feeding `PlaybackHistoryEntries`. Split from
   the above because together they were one unreviewable PR.
3. **`remux-streaming`** *(server + `transcode-engine`)* — the packaging described
   in decision 2, with the acceptance gate below.
4. **`apple-client-core`** — the shared `MediaKit` package (domain, networking,
   local SQLite mirror, playback) and the **first tvOS app**: pairing, browsing,
   the version picker, track selection, play/resume/watched. The first release
   worth using.
   It also owns proving the generated Swift client compiles against the committed
   OpenAPI document, since it owns the package that consumes it; the server half —
   a document CI diffs on every build — is done.
5. **`apple-client-shells`** — the macOS, iOS and iPadOS apps over the same
   `MediaKit`.
6. **`apple-client-discovery`** — recommendations with provenance, release
   calendar and reminders, people pages, the watch diary, and the title-preview
   surface for titles the instance does not hold.
7. **`apple-client-offline`** *(iOS/iPadOS)* — pick a quality, the server submits
   a transcode job, a notification when it is ready, background download, smart
   season retention, offline progress that syncs back.
8. **`apple-client-macos-operator`** — torrents, the convert/merge dialog, the
   transcode queue, the ingest review queue, and the storage view.
9. **`apple-client-platform`** — Top Shelf, widgets, Live Activities, App
   Intents/Shortcuts, Spotlight, Handoff, SharePlay, and the Watch remote.

## Deliverables

The umbrella's own work. Everything else belongs to the features above.

### Phase 0 — the playback spike

The whole epic rests on decision 2 being true. This phase answers it with a
throwaway spike, on real hardware and real files, before any surface is designed.

- [x] **Packaging prototype** — a script (not the engine, not yet) that turns real
      MKV remuxes from this library into an HLS/fMP4 playlist by stream copy.
      Done on a 4K Dolby Vision remux and a 1080p HEVC HDR10 one; see the results
      below. No H.264 source was exercised — this library's samples are HEVC, and
      H.264 is the case least at risk.
- [x] **Playback on an Apple TV 4K** — both packages played on the device
      (tvOS 26.5) with zero stalls and zero dropped frames, including 4K HEVC at
      26.5 Mbit/s. See the device pass below. Seeking was verified only on the HLS
      packages; the seek test was dropped when the harness was rebuilt, so **no
      seek was ever measured on a progressive MP4** — it is part of the gate below.
- [x] **HDR and Dolby Vision on the device** — reached by dropping HLS for a
      progressive MP4 over byte ranges: `hvc1` + `dvvC` gives HDR10, and forcing
      the `dvh1` sample entry gives Dolby Vision, both bright and correct on the
      television.
- [ ] **Playback presentation belongs to `AVPlayerViewController`** — record it
      wherever the client's playback surface is specified. The spike proved one
      concrete failure, not a universal law: a SwiftUI `VideoPlayer` inside a
      `ZStack` under an overlay composited into the SDR layer, giving a dark
      picture and no display switch. The rule that follows is narrower and keeps
      the system transport UI, chapters and track selection: **`AVPlayerViewController`
      owns the presentation, and custom UI is added only through its supported
      overlay APIs** (`contentOverlayView`, `customOverlayViewController`), never
      by compositing a player into an app-drawn view hierarchy.
- [ ] **Decide how the progressive file is produced** — the question that replaces
      segment boundaries. A progressive MP4 needs its `moov` up front, so either
      it is generated on demand (and the whole index must be known before the first
      byte is served) or pre-generated beside the source at the cost of disk.
      **This is an acceptance gate, not a note**: the answer is only credible when
      measured on a whole film rather than a 30-second slice, covering time to
      first frame from cold, seeking into a part not yet produced, 60–80 Mbit/s,
      several concurrent clients, cancel/restart/cleanup, multi-audio and sidecar
      audio and subtitles, DV profiles 5 and 8, E-AC-3/Atmos, and the same tooling
      running inside the Linux `transcode-engine` container rather than on macOS.
      One requirement is easy to miss: **Dolby Vision must be reproduced without
      the elementary-stream detour**. The spike got DV only by extracting a raw
      `.hevc` and re-importing it with a hand-set frame rate — the very path it
      then recorded as a liability — so a result obtained that way does not
      validate a pipeline that will not use it.
- [ ] **Audio passthrough on the receiver** — the pass packaged a single AC-3
      track and never exercised E-AC-3/Atmos passthrough, which is the half of
      "picture and sound" still unanswered.
- [ ] **Higher-bitrate headroom** — the 4K sample is 26.5 Mbit/s and played with
      room to spare, but the 60–80 Mbit/s remux this deliverable originally named
      was never tried.
> **Dropped with HLS.** The master-playlist, segment-boundary and HLS session
> deliverables this phase originally carried no longer describe any work: without
> segments there is no keyframe index to build, no playlist to declare, and no
> streaming session to expire. They are recorded here rather than deleted because
> the reasoning that removed them is worth keeping.

- [x] **Dolby Vision: find a muxer that signals it.** GPAC `MP4Box` writes `dvvC`,
      preserves every RPU, and can force the `dvh1` sample entry that actually
      engages DV. ffmpeg does none of it. See below for the timing trap that comes
      with GPAC.
- [ ] **Decide which sample entry to serve, and to whom.** `dvh1` engages Dolby
      Vision but is DV-only signalling; `hvc1` + `dvvC` is cross-compatible and
      reads as HDR10. A client that reports DV support should get the first and
      everything else the second, which makes this the first real consumer of the
      capability negotiation in
      [native-client-api](../native-client-api/plan.md#playback-resolution).
- [ ] **Audit the library for Dolby Vision profile 5**, where a source served as
      cross-compatible would not degrade gracefully the way the 8.1 sample does.
- [ ] **Settle the GPAC elementary-stream detour.** DV signalling currently costs a
      round trip through a raw `.hevc`, which silently loses frame timing. Find
      whether `MP4Box` can take the MKV directly and still write `dvvC`, or make the
      detour safe by always passing the source's exact frame rate.
- [ ] **Multi-track packaging** — the local pass packaged one audio track. A
      progressive file carries the rest as ordinary tracks, which is simpler than
      the HLS renditions this deliverable first assumed, but it is still unproven,
      as is SubRip → WebVTT (or leaving subtitles as tracks in the file).
- [x] **Written outcome** in this document: remux by stream copy into a progressive
      fMP4 served over byte ranges, `dvh1` for Dolby Vision clients. No fallback
      was needed — HLS was the thing dropped, not the approach.

#### Results of the local pass (2026-08-03)

Run on macOS against two files from the dev library: `The Mandalorian and Grogu
(2026).mkv` (25 GB, 2160p HEVC, 26.5 Mbit/s, 6 audio + 7 subtitle tracks) and
`TRON Legacy (2010).mkv` (6.7 GB, 1080p HEVC HDR10, 7.7 Mbit/s).

**The approach holds.** Both packaged to HLS/fMP4 by stream copy and both played
through AVFoundation: `AVPlayerItem` reached `readyToPlay`, resolved 3840×2160 and
1920×1080, and the playhead advanced 1.46 s over 1.5 s of wall time — frames
moving, not merely an item reporting itself ready.

**Packaging is effectively free**, as a stream copy should be: 30 s of the 4K
26.5 Mbit/s source packaged in 0.11 s, 30 s of the 1080p one in 0.39 s. Nothing
decodes.

**What the probes settled**, each removing a risk this plan had only assumed:

- The 4K source is **Dolby Vision profile 8 with `bl_compat=1`** — single-layer,
  HDR10-compatible base. Not profile 7, which Apple cannot play at all.
- Every audio track across both files is **AC-3 or E-AC-3**. No DTS, no TrueHD, so
  the codec exclusion in decision 1 costs nothing on this content.
- Every subtitle track is **SubRip**. No PGS, so the bitmap-subtitle exclusion
  costs nothing either.
- Correct output tagging matters and was verified in the boxes: video lands as
  `hvc1`/`hvcC` (Apple rejects HEVC tagged `hev1`) and audio as `ac-3`/`dac3`.

**Two traps found, both worth keeping:**

- The 4K file's MJPEG cover-art track is **not flagged `attached_pic`**, so a bare
  `-map 0:v` packages the cover as a second video rendition. The video stream must
  be mapped explicitly as `0:v:0`.
- **ffmpeg does not carry Dolby Vision signalling through a copy.** Its output has
  no `dvvC`/`dvcC` box and `ffprobe` reports no DOVI side data on the copied
  stream. It is not the HLS muxer — a plain fMP4 copy loses it too — and ffmpeg
  8.1.2 exposes no muxer option for it. **GPAC does write it** (below), so this is
  a property of the tool, not of the approach.

**What this pass did not establish.** It ran on macOS AVFoundation, not on an
Apple TV: container and codec acceptance carry over, but DV engagement, Atmos
passthrough, and 4K decode headroom do not. It used 30-second slices from a
keyframe, so it says nothing about full-length playback or about producing a
`moov` for a whole film — the deliverables above that remain unchecked.

#### Device pass on an Apple TV 4K (2026-08-03)

Same packages, played on an Apple TV 4K (2nd generation, `AppleTV11,1`, tvOS
26.5) from the dev Mac over the LAN, driven by a throwaway tvOS harness that
reports `AVPlayerItemAccessLog` rather than asking anyone to judge smoothness by
eye.

| | TRON 1080p HDR10 | Mandalorian 2160p DV |
| --- | --- | --- |
| Resolved | 1920×1080 | 3840×2160 |
| Played in 20 s of wall clock | 18.80 s | 19.92 s |
| Stalls | **0** | **0** |
| Dropped frames | **0** | **0** |
| Segments | 6 in 1.87 s | 6 in 0.87 s |

**Decision 2 holds on the target hardware.** A stream copy plays 4K HEVC at
26.5 Mbit/s with nothing dropped and nothing stalling. Seeking is accurate: the
apparent 1.5–1.9 s overshoot in the raw numbers was the harness sleeping two
seconds before reading the clock, not the player missing.

`observedBitrate` (154 and 686 Mbit/s) is easy to misread and is recorded here so
it is not: it measures **delivery throughput**, not media bitrate. It says the LAN
had enormous headroom over the content's 26.5 Mbit/s, nothing more.

Audio was not answered: one AC-3 track was packaged and no receiver was checked,
so E-AC-3/Atmos passthrough remains open.

##### The dynamic range half, and a retracted claim

An earlier revision of this document said the device pass confirmed the picture as
HDR10. **That was wrong, and the mistake is worth keeping.** The harness drew the
video with `VideoPlayer` inside a `ZStack` under a log overlay; an embedded player
composites its frames into the app's SDR UI layer, so the picture was tone-mapped
dark and the display never switched at all. The television was reporting the
interface's format, not the content's. Every measurement above survives — they come
from `AVPlayerItemAccessLog` and do not depend on presentation — but the eye-read
conclusion did not.

Rebuilding the harness around a full-screen `AVPlayerViewController` with nothing
above it removed the dark picture. It did **not**, on its own, make the display
switch. What actually drives the switch is the **master playlist**:

All three rows below are **HLS**, and all three serve identical segments:

| How the HLS was served | What the television did |
| --- | --- |
| master declaring `VIDEO-RANGE=PQ` + `SUPPLEMENTAL-CODECS="dvh1.08.06/db1p"` | switched to **Dolby Vision** |
| master declaring `VIDEO-RANGE=PQ` alone | switched to **HDR10** |
| variant fetched directly, with no master above it | stayed **SDR** |

The segments always carried correct PQ signalling (`colr`, `mdcv`, `clli`), so
**within HLS, dynamic range is negotiated in the playlist rather than discovered in
the media** — and the switch happens on parsing the declaration, before playback
succeeds or fails. (Progressive fMP4, further down, has no playlist and reaches the
same result from the `moov`.)

Which matters, because the master playlist appeared to be exactly what does not
work. **The three findings below were measured by a harness that ran every case in
one launch and reported "plays" for cases that showed a badge and no picture; its
hold phase was never logged.** All three were disproven on 2026-08-05 — see [the
second HLS pass](#second-hls-pass-2026-08-05). They are kept because the way they
were reached is the lesson:

- ~~**Any master playlist yields `Cannot open` on tvOS.**~~ A master at
  `#EXT-X-VERSION:10` plays, and Apple's own published `v6` master plays in Dolby
  Vision on this television.
- ~~It is not `SUPPLEMENTAL-CODECS` — removing it changes the badge and nothing
  else.~~ Removing it is the difference between failing and playing.
- macOS AVFoundation plays the same master-mediated package without complaint. This
  one still holds, and it did point at the truth: the strictness is tvOS-specific.

**So HDR appeared unreachable over HLS**, and that apparent dead end is what sent
the spike to progressive MP4 below. The destination was right; the reasoning was
not.

Untried at the time, and since done: `#EXT-X-PLAYLIST-TYPE:VOD` and
`#EXT-X-MEDIA-SEQUENCE:0` on the variant, and Apple's `mediastreamvalidator`.

##### The answer: drop HLS

Asked why any of this needed HLS at all — AVPlayer decodes these codecs natively —
the spike tried the obvious alternative and it worked immediately:

| What was served | Opens on tvOS | Television reports |
| --- | --- | --- |
| HLS via master playlist | **no** | (switched, then failed) |
| HLS variant fetched directly | yes | SDR |
| **Progressive fMP4, `hvc1` + `dvvC`, byte ranges** | **yes** | **HDR10**, bright |
| **Progressive fMP4, forced `dvh1` sample entry** | **yes** | **Dolby Vision** |

Both progressive files play 4K HEVC at 26.5 Mbit/s with zero stalls. The last row
is the whole goal of decision 2, reached with no playlist of any kind.

The final missing piece was one field. `hvc1` + `dvvC` is the *cross-compatible*
form, and a player is entitled to read it as HDR10 — which is exactly what
AVFoundation did. Forcing the Dolby Vision codec type so the sample entry reads
`dvh1` (GPAC's `dvp=f8.hdr10`) is what engages DV. Everything else about the two
files is identical.

An earlier progressive attempt in this spike appeared to fail; that was the test
server having no `Range` support, not the format. Serving byte ranges properly is a
precondition, and the existing stream endpoint already does it.

Consequences worth carrying into the design: there is no master playlist, no
segmenting, no keyframe index for segment boundaries, and no HLS session lifecycle
to manage — several of the deliverables this phase opened simply stop existing.
What replaces them is narrower: a progressive file needs its `moov` up front (the
spike's did), so the open question is whether that is produced on demand or
pre-generated, and what seeking costs when it is.

##### What GPAC settled about Dolby Vision

- **`dvvC` is written**, next to `hvcC`, `colr`, `mdcv` and `clli` in the init
  segment — the cross-compatible profile 8.1 form with an `hvc1` sample entry.
- **The RPU metadata survives**: 756 DV RPU NAL units in the source elementary
  stream and 756 after the import, one per frame.
- **GPAC's own master playlist is internally inconsistent** — it declares
  `CODECS="dvh1.08.06"` while the sample entry it just wrote is `hvc1`, and it
  omits `VIDEO-RANGE` entirely.
- **A round trip through a raw elementary stream loses frame timing.** MP4Box
  defaults to 25 fps, silently, which on this 24 fps source drifts the audio by
  1.09 s per 30 s — nearly five minutes across the film. `:fps=` is mandatory, and
  the wider lesson is that the elementary-stream detour is a liability.

#### Second HLS pass (2026-08-05)

The first pass rejected HLS on a false premise, so the question was reopened and
measured properly: **one case per cold launch**, verdict read from
`AVPlayerItemAccessLog` and `AVPlayerItemErrorLog` rather than from a badge, with
each case in its own directory because AVFoundation caches playlists by URL.

All rows below serve the same 30 s 2160p HEVC slice (Dolby Vision profile 8.1,
`bl_compat` 1, AC-3) unless stated otherwise.

| Case | Master | Media | Result |
| --- | --- | --- | --- |
| A | none — variant fetched directly | GPAC | plays, 3840×2160, **SDR** |
| D | `v6`, `CODECS` + `SUPPLEMENTAL-CODECS` + `VIDEO-RANGE` | GPAC | fails |
| E | `v6`, no `CODECS` | GPAC | fails |
| L | `v10`, `CODECS` + `SUPPLEMENTAL-CODECS="dvh1.08.06/db1p"` | GPAC (`bl_compat` 6) | fails |
| **N** | `v10`, `CODECS="hvc1…"` only | GPAC (`bl_compat` 6) | **plays, 3840×2160, 0 stalls, HDR10** |
| P | `v10`, `CODECS` + `SUPPLEMENTAL-CODECS`, honest peak `BANDWIDTH` | GPAC `dvp=f8.hdr10` (`bl_compat` 1) | fails |
| R | `v10`, `CODECS="hvc1…"` only | GPAC `dvp=f8.hdr10` (`bl_compat` 1) | fails |
| V | `v10`, `CODECS="dvh1.08.06"`, sample entry patched to `dvh1` | GPAC | fails (`-16044`) |
| — | Apple's published `v6` master, from Apple's CDN | Apple profile-5 4K | **plays, Dolby Vision, picture** |

**A master playlist opens.** Case N settles it: a hand-written master, served from
the dev Mac, over locally packaged media, plays 4K HEVC with zero stalls and drives
the television to HDR10. The first pass's central claim was wrong.

**This Apple TV does Dolby Vision over HLS.** Apple's unchanged reference stream
produces both a moving 3840×2160 picture and the Dolby Vision badge. The hardware
chain and tvOS path are not the limit.

**Nothing delivers *this library's* profile 8.1 as Dolby Vision over HLS.** Five
configurations were tried — cross-compatible `hvc1` with and without
`SUPPLEMENTAL-CODECS`, over both `bl_compat` 6 and `bl_compat` 1 media, and a
`dvh1`-declared variant with the sample entry patched to match. All fail. The only
thing that plays is HDR10.

Two structural facts explain why this is not merely an authoring slip:

- **Apple's own stream is profile 5**, declared directly as `CODECS="dvh1.05.06"`
  with no `SUPPLEMENTAL-CODECS` anywhere in the manifest. Profile 5 is not reachable
  from 8.1 by repackaging — it has a different, non-backward-compatible base layer —
  so the working reference is in a form this library cannot be converted into
  without re-encoding.
- **`SUPPLEMENTAL-CODECS` is the mechanism designed for cross-compatible 8.1**, and
  it has never opened here in any configuration.

One negative result is *not* evidence and is recorded so it is not re-used: a
locally served master referencing Apple's remote media fails (`-16044`) under both
mixed and uniform schemes, with matching MIME types. That is a cross-origin artifact
of the test rig, which production never reproduces — master and media share an
origin there, exactly as in case N.

`mediastreamvalidator` 1.26.143 was run against the failing cases. It reports no
Dolby Vision or `SUPPLEMENTAL-CODECS` error — only generic authoring findings (a
frame-rate change at one segment, missing `CLOSED-CAPTIONS=NONE`, a deprecated
playlist MIME type from the test server) that do not distinguish the failing cases
from the playing one.

**Consequence for decision 2.** The destination is unchanged — progressive MP4 is
what delivers Dolby Vision, and it is still simpler. What changes is the reason and
the fallback: HLS is a **working HDR10 path**, not a broken one, so if streaming
ever becomes preferable to byte ranges, it costs dynamic range rather than being
impossible. Should Dolby Vision over HLS ever be needed, the only known route is a
profile-5 rendition, which means re-encoding and is out of scope.

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
  answered it only at 26.5 Mbit/s over the wire, where there was ample headroom.
- **Is the progressive file produced on demand or pre-generated?** A progressive
  fMP4 needs its `moov` up front, which is the one real constraint the approach
  carries. Producing it per playback means reading the source's index first;
  pre-generating means a second copy on disk. This replaces the keyframe-index
  question the HLS design had.
- **Do sidecar audio tracks survive packaging?** They are separate files and can
  be taken as extra inputs in the same copy pass, which would make "watch with the
  Russian dub, no merge" work on Apple TV — the single most valuable thing Infuse
  cannot do. With a progressive file they become ordinary tracks in the output
  rather than alternate renditions, which is simpler, but it is still unverified.
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
