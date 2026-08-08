# Remux Streaming — plan

Status: Draft
Created: 2026-08-05
Updated: 2026-08-08

> Part of the [Apple client](../apple-client/plan.md) epic, and the last server
> piece before a client can play the library.
> [`native-playback`](../native-playback/feature.md) already answers `remux` for a
> source whose codecs a client can decode and whose container it cannot open — but
> only when packaging is available, which today it never is. This makes it
> available.

**Why this is still `Draft` although Phase 0 is done.** Phase 0 is a *gate*, not
implementation: a throwaway prototype whose only job was to find out whether the
design is possible before anything is committed to. Gates run before `Ready` by
construction — that is what they are for — and nothing in Phases 1–4 has been built.

Three things still stand between this and `Ready`: the transport-selection policy,
the cost of the index walk on the slow disk (unmeasurable on the dev machine), and
whether folding a sidecar in can avoid the engine. `Ready` also needs explicit
approval in chat, which has not been given.

## Goal

Serve a file a client cannot open, by **repackaging it and nothing else** — without
storing a second copy of it, and without making the viewer wait for the film to
start.

Most of this library is MKV holding HEVC or H.264 with AC-3/E-AC-3/AAC — codecs
AVFoundation decodes, in a container it refuses. That is a packaging problem, and a
stream copy solves it: no frame is decoded and no quality is lost.

## The constraints this plan is written against

Stated in discussion and treated as requirements, not preferences:

1. **No duplicate on disk.** A second full copy of every title a client cannot
   direct-play is not acceptable.
2. **No multi-minute wait before playback.** Pressing play must not begin with a
   progress bar.
3. **The sources are on slow spinning disks.** This is what turns constraint 2 into
   a design constraint rather than an implementation detail: any shape that must
   read a 40 GB file end to end before serving its first byte costs minutes here,
   not seconds.

## What AVFoundation demands

Measured on 2026-08-07, on macOS AVFoundation against a controlled HTTP server, with
one variable per run. These are the rules the delivery design has to obey, and each
was reached by isolating it:

| Server behaviour | Result |
| --- | --- |
| `200` for everything, no `Accept-Ranges` | `Operation Stopped` — refused after a two-byte probe |
| `206` with `Content-Range: bytes X-Y/*` (length undeclared) | `REJECTED`, `NSOSStatus -12939` |
| `206` with the real total length | **plays** — same server, same file, same 8 MB windows |

- **Range support is mandatory, and it is not about seeking.** The player opens with
  `Range: bytes=0-1` purely to find out whether the server honours ranges, and stops
  before showing anything if it does not. Giving up seeking would not have avoided
  this.
- **The total length must be declared.** `*` is legal HTTP and AVFoundation refuses
  it. Output produced on the fly therefore has to state its final size before the
  first byte, or be padded to a declared upper bound.

### The measurement that decided the shape

A full-length fragmented MP4 was produced from the 2 h 12 m, 24.6 GB source (stream
copy, 22.43 GB out, 3310 fragments) and played through a server capped at 25 MB/s —
a rate far above the content's 26.5 Mbit/s but far below a local disk, so that
*scanning* and *reading ahead* could be told apart:

```
ready after:      15.83 s
requests:         3309
highest offset:   99.99 % of the file
```

At 25 MB/s, transferring the file would have taken 963 s. It took 16. So the player
did not read the file — it **walked it**, opening a range at each fragment, taking
roughly 120 KB of box headers, and abandoning the connection. **AVFoundation builds
its own fragment index across the entire file before it shows a frame.**

Writing a `sidx` does not prevent this. One was produced (three boxes, ~3313 entries,
full coverage) and the behaviour was identical.

**This kills producing the output on the fly.** To answer a request at byte
24,000,000,000 the server must know what lies there, and the player asks before
playback starts. The fragmented shape does not escape this: a `moof` is derived from
the same sample table a non-fragmented `moov` carries, so **both forms need the same
work done in advance.** The claim in the previous revision of this plan — that a
fragmented MP4 can be produced as the client consumes it — is wrong and is retracted.

The good news is the *size* of what must be known in advance. The player needed
~3300 header reads, not gigabytes. What has to exist before playback is an **index**,
measured in megabytes, not a copy of the film.

### What still holds from the earlier passes

- **Forcing the `dvh1` sample entry is what engages Dolby Vision**, and it survives
  fragmentation: a fragmented MP4 with `dvh1` + `dvvC` played 3840×2160 on the Apple
  TV 4K with 0 stalls, seeking forward and back in under 2.5 s with the badge held
  throughout. Whatever shape is synthesised, Dolby Vision is reachable.
- **MP4Box normalises the sample entry back to `hvc1` when it fragments**, exactly as
  its HLS segmenter does. The forced `dvh1` must be written after fragmentation.
- **HEVC must be tagged `hvc1`.** Apple rejects `hev1` outright.
- **GPAC writes `dvvC`; ffmpeg does not** — an ffmpeg stream copy drops the
  signalling silently, and ffmpeg 8.1.2 exposes no option for it.
- **AC-3 needs `+delay_moov`** in a fragmented ffmpeg remux, or the header cannot be
  written at all.
- **Cover art can masquerade as video.** The 4K sample's MJPEG track is not flagged
  `attached_pic`, so a bare `-map 0:v` packages the cover as a second video
  rendition.
- **HLS works, and delivers HDR10** — see
  [the second HLS pass](../apple-client/plan.md#second-hls-pass-2026-08-05). Across
  five configurations it did **not** deliver Dolby Vision for this library's profile
  8.1 content.

## The shape: one index, two transports

Settled in discussion on 2026-08-07. **Both** delivery transports are built, over a
**single index** — because the expensive part is the index, and once a sample table
exists, rendering HLS segments from it is nearly the same code as rendering MP4 byte
ranges. This is not two paths at twice the cost; it is one engine with two outputs.

| Transport | Dolby Vision | Needs the index | Ships |
| --- | --- | --- | --- |
| **HLS** | no — HDR10 only | **no** | first |
| **MP4 over byte ranges** | **yes** | yes | with the index |

### Why HLS first

Not because it is better — it is not. Its familiar advantages (adaptive bitrate,
segment caching, per-segment retry) all come from a **bitrate ladder**, and a ladder
comes from re-encoding. With a stream copy and one variant there is nothing to adapt
between. What HLS genuinely gives here is narrower and enough: it needs no declared
total length, no pre-built index, and it seeks by time rather than by byte.

That is exactly the set of things the index has to solve for MP4 — so HLS can ship
**before** the indexer exists, and unblock every other part of the client (navigation,
authentication, watch history, resume) while the index is still being written. Dolby
Vision arrives with the index, not before it.

**The risk in that ordering, stated so it stays a decision rather than a drift:**
once HLS works and nobody is complaining, the index path can quietly never get built
and Dolby Vision never arrives. If that becomes the outcome it should be chosen out
loud, not discovered later.

### The index, and what it feeds

Walk each MKV once, in the background, at scan or ingest time, and record its sample
table: per frame, the track, timestamp, size, offset in the source, and keyframe
flag. Store that — megabytes, not gigabytes.

At play time nothing is repackaged in the usual sense. The container is **computed**:
its header comes from the index, and every byte range the player asks for is resolved
by working out which samples fall in it and reading those bytes from the original
MKV. The same table renders HLS segments, which are the same samples cut at keyframe
boundaries.

#### Byte identity, measured 2026-08-07

This rests on samples being *referenced* rather than converted, which needs a
Matroska block payload to be the MP4 sample byte for byte. Measured by reading both
containers directly and comparing the bytes, over five sources and two video codecs
(HEVC Dolby Vision, HEVC HDR10, three H.264): block counts equal sample counts, byte
totals match exactly, and every sampled frame is identical.

**That test was confounded, and the confound mattered.** Its slices were written by
ffmpeg, which does not lace; the library's own files are muxed by something that
does. Checked against the untouched originals:

| Source | Blocks | Lacing |
| --- | --- | --- |
| TRON Legacy | 414,554 | none |
| The Mandalorian and Grogu | 426,562 | **fixed**, on six AC-3/E-AC-3 tracks |
| Zootopia | 212,664 | **fixed**, on two AC-3 tracks |
| Escape from New York | 315,224 | **fixed** and **EBML**, incl. DTS |
| Mercy | 314,670 | **fixed**, on six tracks |

**Video is never laced** — every laced track is audio — so one block is one sample
for video, and that half is settled.

Audio is not, and the requirement is now concrete rather than hypothetical: **the
indexer must de-lace**. Under fixed lacing a payload is N equal-sized frames in a
row; under EBML lacing the sizes are coded in a header and the frames follow. Either
way **the frames stay contiguous byte ranges inside the source**, so they are still
referenceable — the index carries one entry per frame instead of one per block, and
skips the lacing header. Xiph lacing is the third form and must be handled too,
though nothing in this library uses it yet.

The walk collected a first cost datum while it was there: **2–26 s per film** to read
every block header, on the dev SSD. The slow disk is still unmeasured.

What this buys, against the constraints: nothing duplicated (1), nothing produced at
play time so nothing to wait for (2), and the one full pass happens in the background
where nobody is watching (3). Seeking works because any offset is computable. And
because each range request is answered independently from stored data, **no session
model is needed** — which retires an open question an earlier revision carried.

### Choosing between them

By capability, with an override. The client already declares what it can open
through `NativeCapabilityProfile`, so the server picks: a client that reports Dolby
Vision support, playing a source that carries it, is served MP4 over byte ranges;
everything else is served HLS. A manual setting overrides the choice, which turns
"the picture is not what I expected" from a bug report into a switch — the pattern
[Swiftfin](https://github.com/jellyfin/Swiftfin) uses with `forceDVTranscode` and its
`auto / mostCompatible / directPlay / custom` compatibility mode.

### The contract needs a second axis

`NativePlaybackDecision` today is `DirectPlay | Remux | Unsupported`, which describes
*what* is done to the streams. HLS is not a fourth kind of that — it is a different
way to **deliver** a repackage. Conflating them ages badly.

So `Decision` keeps its meaning and a `Transport` axis joins it —
`byteRange | hls` — carried on `NativePlaybackResolution` and expressible as a client
preference. The client does not exist yet, so this costs nothing now and would be a
breaking change later.

### What is not being built

- **A second player.** [Swiftfin](https://github.com/jellyfin/Swiftfin) ships VLC
  beside AVPlayer, and MKV then plays untouched — along with PGS, DTS-HD and TrueHD.
  It is the reason they never had to solve this. Reversing
  [decision 1](../apple-client/plan.md#1-playback-engine-avplayer-only) belongs to
  the epic, not here, and it is not proposed.
- **Dolby Vision over HLS.** Five configurations were measured and none delivered it
  for profile 8.1 content; the only known route is a profile-5 rendition, which means
  re-encoding.

### Where this leaves the engine

**In `api`, on the evidence of the prototype**, which served a 26 GB source without
invoking ffmpeg or any other external tool: the work is container parsing and byte
arithmetic. `api` already parses container headers and has deliberately shipped
without ffmpeg since [external track
sidecars](../external-track-sidecars/feature.md). The engine's stated reasons for
existing are hardware-encoding isolation and long-running CPU work, and neither
applies. **Subtitles are the exception** — they must be rewritten into the
transport's own format — but that is text processing rather than encoding, so it does
not move the answer.

## Deliverables

One PR. A throwaway prototype comes first — see the gate below — because the index
design rests on an assumption no measurement has tested yet.

### Phase 0 — the prototype gate

A throwaway spike — not the feature — because the design can still be killed by its
own foundation. **No production code starts until this has an answer.** It was
written outside either repository and kept afterwards, in
[`scripts/remux-prototype/`](../../../scripts/remux-prototype/README.md), only
because a nightly `/tmp` cleanup had already destroyed it once.

- [x] **Byte identity** — answered 2026-08-07: **video holds, audio needs de-lacing,
      and the design survives.** See
      [Byte identity](#byte-identity-measured-2026-08-07).
- [x] **Synthesise an MP4 from an index** — done 2026-08-08, and the `mdat`-wraps-the-
      source simplification holds.
- [x] **Dolby Vision on the Apple TV** from that synthesised file — confirmed on the
      television.
- [x] **Seeking** across it — 0.35 s, 0.35 s and 2.51 s over a 2 h 12 m film.
- [ ] **The index walk on the slow disk** — how long, and how large the result. The
      only item this hardware cannot answer: the dev machine is an SSD.
- [x] **Written outcome**, below.

#### Outcome of the prototype (2026-08-08)

A throwaway prototype — an MKV indexer, an MP4 synthesiser and a range server, in
Python — served the untouched 26.4 GB source as a `dvh1` MP4 and played it on the
Apple TV 4K **in Dolby Vision**, with seeking, having produced and stored nothing.

| | 2 h 12 m, 26.4 GB source |
| --- | --- |
| Index walk | 27 s cold, 2.3 s warm (SSD) |
| Synthesised header | **7.8 MB — 0.0296 % of the source** |
| Written to disk | nothing |
| Time to `readyToPlay` | 0.43 s |
| Reported duration | 7951.10 s, exactly the source's |
| Seeks (1:00:00 / 10:00 / 2:11:40) | 0.35 s, 0.35 s, 2.51 s |
| Stalls | 0 |

**Non-fragmented wins, decisively.** This settles the shape question by measurement:
a real sample table costs **7 requests** to start playing, because the player reads
`moov` once and then addresses the media directly. The fragmented file measured on
2026-08-07 needed **3309**, because it had to walk every `moof` first.

**Everything Dolby Vision needs is carried, not derived.** `hvcC` comes from the
track's `CodecPrivate`, and the DV configuration from its `BlockAdditionMapping`,
whose `BlockAddIDType` is literally `dvcC` — 24 bytes decoding to profile 8, level 6,
RPU present, `bl_signal_compatibility_id` 1, matching the source exactly.

**Colour is the exception, and it is not always in the container.** The library's
originals carry no Matroska `Colour` element at all; the information is in the HEVC
SPS, which is where `ffprobe` reads it from. A slice remuxed by ffmpeg gains one,
which is how the difference surfaced — a test file can be more complete than the
source it came from. The Dolby Vision run above wrote **no `colr` box** and engaged DV
anyway, because the decoder reads the VUI. The implementation should still parse the
SPS when the container is silent: `colr` costs nothing to write, and HDR10 content
carries no DV configuration to fall back on.

Three traps, each found by a failure and each cheap to fall into again:

- **A uniform sample duration drifts.** Taking `DefaultDuration` as the frame
  duration diverged from the real timestamps by 33 s over this film — invisible on a
  30 s test slice. Sample durations must come from the timestamps in the file. The
  decode timeline is the presentation timestamps in sorted order.
- **Truncating an *explicit* byte range is read as a failed request.** A client that
  asks for `bytes=0-67832060` and receives eight megabytes gives up. Only an
  open-ended range may be answered with a window.
- **An open-ended range must be streamed, not assembled.** The first server built the
  response in memory, which for a 26 GB source is exactly as bad as it sounds.

**Where it belongs: `api`.** Nothing on the serving path invoked ffmpeg, or any
external tool — the prototype is container parsing and byte arithmetic. The engine's
stated reasons for existing are hardware-encoding isolation and long-running CPU
work, and neither applies. Subtitles remain the exception, and they need text
rewriting rather than encoding tooling, so they do not change the answer.

### Phase 1 — the index

- [ ] **An MKV indexer** producing, per track, the sample table: timestamp, duration,
      size, offset in the source, keyframe flag. Built by walking cluster and block
      headers rather than reading payloads.
- [ ] **De-lacing** — fixed, EBML and Xiph, so a laced audio block becomes one index
      entry per frame. Measured as present on AC-3, E-AC-3 and DTS tracks across this
      library.
- [ ] **Storage** — where the index lives, its format, and how it is invalidated when
      the source changes.
- [ ] **Built in the background**, at scan or ingest, so no viewer waits for it.
- [ ] **Measured on a real film from the slow disk**: how long the walk takes, how
      large the index is, and how much of the file the walk has to touch.
- [ ] **Unit tests** over a small crafted MKV, including a source whose cover art is
      not flagged `attached_pic`.

### Phase 2 — the synthesiser

- [ ] **Compute the container from the index**: `ftyp`, `moov`, and the chosen body
      shape, with `hvc1` tagging, explicit video mapping, and the requested sample
      entry — `dvh1` when the client asked for Dolby Vision.
- [ ] **Answer an arbitrary byte range** by resolving it to samples and reading those
      from the source, with the total length declared, since AVFoundation refuses an
      undeclared one.
- [x] **Decide fragmented or not, by measurement** — answered by the prototype:
      **non-fragmented**, 7 requests against the fragmented file's 3309.
- [ ] **Track selection** so the output carries what the viewer chose, including
      sidecars folded in as tracks.
- [ ] **Subtitle conversion**, which is the one thing that cannot be referenced the
      way audio and video can. A SubRip or ASS sample is not a valid MP4 subtitle
      sample and not a valid HLS one either: MP4 carries `tx3g` or `wvtt`, HLS
      carries WebVTT or IMSC1. So text has to be **rewritten**, per transport, and
      ASS styling is lost in the process. This applies to the container's own
      subtitle tracks and to
      [sidecar files](../external-track-sidecars/feature.md) equally.
- [ ] **Unit tests for the conversion** — timing preserved across the rewrite, a
      cue spanning a segment boundary, and an ASS source degrading to plain text
      rather than failing.
- [ ] **An HLS renderer over the same index** — a media playlist cut at keyframe
      boundaries and segments rendered from the same sample table, so the second
      transport is a second output rather than a second pipeline.
- [ ] **Unit tests**: a range spanning a fragment boundary, the first and last byte,
      a range beyond the end, the sample entry surviving into the output, and a
      segment boundary landing on a keyframe.

### Phase 3 — serving it

- [ ] **A `Transport` axis on the contract** — `byteRange | hls` beside `Decision`,
      chosen from the client's declared capabilities and overridable by a setting.
- [ ] **`api` serves the synthesised bytes** by byte range and as HLS, under the
      existing signed URL tokens, with the same sandbox and access rules as the
      direct path.
- [ ] **`NativePackagingAvailability` reflects reality**, so `resolve` starts
      answering `remux` and `GET /native/v1/server` reports `packaging: true`.
- [ ] **A source with no index yet** — what `resolve` answers, and what the client
      shows, when the background walk has not run.
- [ ] **Unit tests**: a remux URL is refused without a valid token, an unpublished
      item is unreachable, and a client that cannot open the container still gets
      `remux` rather than `directPlay`.

### Phase 4 — the load it will actually see

- [ ] **A whole film end to end on an Apple TV**, including seeking across it.
- [ ] **Time to first frame from cold**, on a source on the slow disk.
- [ ] **60–80 Mbit/s**, the bitrate class never yet exercised.
- [ ] **Several concurrent clients.**
- [ ] **Multi-audio, sidecar audio, and subtitles** folded into the output.
- [ ] **DV profiles 5 and 8**, and E-AC-3/Atmos passthrough.
- [ ] **Confirm the measurements above on tvOS.** Everything in "What AVFoundation
      demands" was measured on macOS, which has already proven the more permissive of
      the two in this project.

### Closing the plan

- [ ] **`feature.md`**, and an update to
      [native-playback](../native-playback/feature.md) where it says packaging does
      not exist yet.
- [ ] **Index** — `node scripts/docs-index.mjs --fix`.
- [ ] **Version bump** — new functionality, so a minor; read `manifest.json` when
      the work lands.

## Open questions

- ~~**Does the byte-identity assumption hold?**~~ Answered 2026-08-07: for video yes,
  for audio only after de-lacing, which is now a deliverable rather than a risk.
- **Which transport a given playback gets.** The negotiation exists already —
  `NativeCapabilityProfile` travels with every resolve request and the resolver
  answers against it — so the question is only the policy: which declared
  capabilities select MP4 over HLS, and how a manual override interacts with it.
  Filling the profile from the device is client work and is a deliverable on
  [apple-client](../apple-client/plan.md).
- ~~**Does this still belong to `transcode-engine`?**~~ Answered by the prototype:
  no ffmpeg appears on the serving path, so it belongs in `api`. What remains open is
  narrower — whether *folding a sidecar in* can be done the same way, or whether that
  one operation still wants the engine.
- **What the index costs on the slow disk.** Walking block headers is far cheaper
  than reading payloads, but it is still a pass over a 40 GB file and it has never
  been measured on this hardware. Phase 1 answers it, and the answer decides whether
  indexing at scan time is acceptable or has to be deferred to first play.
- **Whether the source's own signalling should be recorded too.** Nothing captures
  what a file says about itself today, so direct play serves Dolby Vision blind (see
  [media-probe-providers](../media-probe-providers/plan.md)). The index walk touches
  the same headers, so the two may be cheaper built together.

## Verification steps

1. `dotnet test` for the API test project.
2. Phase 4 in full on a Core-managed dev runtime.
3. End to end on an Apple TV 4K: an MKV the client cannot open resolves to `remux`,
   plays, seeks across the whole film, and shows the dynamic range the response
   promised.
4. A source with a sidecar dub plays with that dub selected.
5. Confirm Infuse still plays the same titles through the Jellyfin surface — this
   feature is additive and must not disturb it.
