# Remux Streaming — plan

Status: In Progress
Created: 2026-08-05
Updated: 2026-08-08

> Part of the [Apple client](../apple-client/plan.md) epic, and the last server
> piece before a client can play the library.
> [`native-playback`](../native-playback/feature.md) already answers `remux` for a
> source whose codecs a client can decode and whose container it cannot open — but
> only when packaging is available, which today it never is. This makes it
> available.

Phase 0 was a *gate* — a throwaway prototype whose only job was to find out whether
the design is possible — and it closed on 2026-08-08. The remaining open questions
were settled in discussion the same day, and implementation started with the index.

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

| Transport | Dolby Vision | Ships |
| --- | --- | --- |
| **MP4 over byte ranges** | **yes** | first, with the index |
| **HLS** | no — HDR10 only | second, over the same index |

### Why the index and MP4 come first

An earlier revision had HLS first, on the reasoning that it needs no pre-built index
and could therefore ship while the indexer was still being written. **The prototype
retired that argument.** The index stopped being the long, risky part the moment it
was measured: it walks a 26 GB film in 27 s, and the whole thing is a few hundred
lines. Ordering the work around avoiding it no longer buys anything.

What HLS would have bought — an earlier unblocking of the client — is worth less than
what it costs, which is that Dolby Vision does not arrive with it. DV is the entire
reason this feature is expensive, and the earlier ordering carried an explicit risk
that it might quietly never be built at all. Doing the index first retires that risk
instead of managing it.

HLS keeps its place as the second transport, rendered from the same sample table, for
clients and situations where HDR10 is the answer.

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

**By capability, with a manual override.** Settled in discussion on 2026-08-08.

The client already declares what it can open through `NativeCapabilityProfile`, so
the server picks: a client that reports Dolby Vision support, playing a source that
carries it, is served MP4 over byte ranges; everything else is served HLS. A setting
overrides the choice, which turns "the picture is not what I expected" from a bug
report into a switch — the pattern
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

- [x] **An MKV indexer** producing, per track, the sample table: timestamp, size,
      offset in the source, keyframe flag. Built by walking cluster and block headers
      rather than reading payloads. The EBML primitives were lifted out of
      `ContainerHeader` into a shared `Ebml` reader first, so the two walkers cannot
      drift apart.
- [x] **De-lacing** — fixed, EBML and Xiph, so a laced audio block becomes one index
      entry per frame. A lacing header that does not add up leaves the block whole
      rather than slicing it into samples that point outside it.
- [x] **Storage** — one file per media source under the app data directory, beside
      the torrents rather than in the database: an index is derived, large next to a
      row, rebuildable, and of no interest to a backup. Timestamps and offsets climb
      in small steps, so the file stores the steps as variable-length integers rather
      than the values as fixed-width ones — **5.6 bytes a sample**, which is 9.33 MB
      for a 26.37 GB film and 3.32 MB for an 8.32 GB one, both around 0.04 % of the
      source. Loading one costs 0.1 s. Invalidation needs no schema: the header
      carries the source's length and last-write time, so a file that was replaced
      invalidates its own index. Written aside and moved into place, so an
      interrupted build leaves nothing to mistake for an index.
- [x] **Built in the background**, so no viewer waits for it. A worker walks one
      source at a time — several at once would be slower in total on a spinning disk
      and would make everything else on it worse — and there is no queue to keep in
      sync: the database knows which sources exist and the store knows which have an
      index, so the outstanding work is a query and a restart resumes without
      remembering anything. Orphaned indexes are pruned once per process, since
      nothing else removes a file when its title is deleted.
- [ ] **Measured on a real film from the slow disk**: how long the walk takes and
      how much of the file it has to touch. On the dev SSD a 26.37 GB film costs
      32.6 s and a 8.32 GB one 14.4 s; the spinning disk is still unmeasured.
- [x] **Unit tests** over crafted Matroska written by hand — all three lacing forms,
      block groups whose keyframe answer is the absence of a `ReferenceBlock` rather
      than a flag, and a lacing header that does not add up. ffmpeg cannot produce a
      laced test file, so the builder writes one directly.
- [ ] **Unit tests for the awkward sources** — a cover-art track not flagged
      `attached_pic`, and a source whose timestamps are not monotonic in file order.

### Phase 2 — the synthesiser

- [x] **Compute the container from the index**: `ftyp`, `moov` and an `mdat` that
      wraps the source verbatim, so an output offset is the header's length plus the
      source offset. `hvcC` and `avcC` are carried from `CodecPrivate`, `dvvC` from
      the Dolby Vision mapping, `colr` from the `Colour` element when the container
      states one and left out rather than guessed when it does not. `dvh1` is offered
      only for HEVC that came with a configuration, and only when asked for. Verified
      through AVFoundation and by decoding both streams.
- [ ] **Answer an arbitrary byte range** by resolving it to samples and reading those
      from the source, with the total length declared, since AVFoundation refuses an
      undeclared one.
- [x] **Decide fragmented or not, by measurement** — answered by the prototype:
      **non-fragmented**, 7 requests against the fragmented file's 3309.
- [x] **Track selection** so the output carries what the viewer chose — video first,
      then the chosen dub, then subtitles only when they were asked for. The choice
      arrives as stream indexes, which are positions in the file rather than Matroska
      track numbers, so the index carries both. A stale choice falls back to the
      first track of its kind rather than playing nothing.
- [x] **A sidecar dub folded in as a track.** The output takes several inputs, one
      `mdat` per wrapped file, and a sample offset may point into any of them. An
      external `.mka` is indexed in the background like any other Matroska file, keyed
      by its stream row, and the endpoint names tracks by stream id rather than by
      position — a sidecar has no position in the container. Verified on a real dub
      file: video from one file, audio from another, in one MP4.
- [x] **A sidecar subtitle folded in.** `.srt`, `.ass`, `.ssa` and `.vtt` are parsed
      per request into cues — no index, because a film's dialogue is a hundred
      kilobytes — and join the embedded path at the point where both are simply a list
      of cues. Verified on a real file: parsed, rewritten as `tx3g`, and extracted back
      with its timings intact to the millisecond.
- [x] **Subtitle conversion** — the one thing that cannot be referenced the way audio
      and video can. A SubRip or ASS sample is not a valid MP4 subtitle sample: MP4
      wants `tx3g`, which is a length-prefixed string, and the gaps between cues need
      empty samples that exist nowhere in Matroska. So the text is rewritten and
      carried in a **second `mdat` inside the header** — a film's dialogue is a
      hundred kilobytes against a source of gigabytes — while the media `mdat` still
      wraps the source untouched. ASS rows give up their fields and override codes;
      styling is lost, which the epic already accepted. A cue with no stated duration
      is dropped rather than guessed at, because MP4 has no "until the next one".
- [x] **Unit tests for the conversion** — markup stripped, ASS fields and overrides
      removed, a comma inside the text not mistaken for a separator, gaps becoming
      empty samples, and a real film's subtitle track rewritten and read back with
      its timings intact.
- [ ] **Subtitle conversion for HLS**, which wants WebVTT or IMSC1 rather than
      `tx3g`, and segmented to match.
- [ ] **An HLS renderer over the same index** — a media playlist cut at keyframe
      boundaries and segments rendered from the same sample table, so the second
      transport is a second output rather than a second pipeline.
- [ ] **Unit tests**: a range spanning a fragment boundary, the first and last byte,
      a range beyond the end, the sample entry surviving into the output, and a
      segment boundary landing on a keyframe.

### Phase 3 — serving it

- [x] **A `Transport` axis on the contract** — `byteRange | hls` beside `Decision`,
      because HLS is another way to deliver a repackaging rather than a fourth kind
      of decision. Only `byteRange` exists so far.
- [x] **`api` serves the synthesised bytes** by byte range at
      `/native/v1/media/{id}/remux`, under the same signed URL token, sandbox and
      visibility rules as the direct path. The header and the untouched source are
      presented as one seekable stream, so ranges are handled by the framework's own
      file result rather than by hand — which matters, since AVFoundation refuses a
      server that will not declare a total length and reads a truncated answer to an
      explicit range as a failure.
- [ ] **`api` serves HLS**, once there is an HLS renderer to serve.
- [x] **Availability reflects reality**, so `resolve` answers `remux` with a URL. The
      placeholder flag is gone: readiness is now asked per source, in one query for
      all the editions of a title rather than one round trip each.
- [x] **A source with no index yet** answers `unsupported` with `packaging_pending`,
      which is a different thing from `packaging_unavailable` and deliberately so: a
      container nothing can index will never become playable, while a file the walk
      has not reached will. A client that knows the difference shows "preparing" and
      retries instead of showing "unavailable" forever. The URL itself answers 503
      for the same case.
- [ ] **`GET /native/v1/server` reports the packaging capability.**
- [ ] **Unit tests**: a remux URL is refused without a valid token, an unpublished
      item is unreachable, and a client that cannot open the container still gets
      `remux` rather than `directPlay`.

### Phase 4 — the load it will actually see

#### First end-to-end pass through the running server (2026-08-08)

Everything above had been verified in pieces. This was the first time the whole chain
ran as it will ship: the app under Hosty Core, the background worker, the index store,
the synthesiser and the endpoint, with an Apple TV 4K as the client.

The worker started with the app and built **seven indexes on its own**, before any
request. A 26.37 GB Matroska source then played at **3840×2160 in Dolby Vision**,
seeking across a two-hour film in 0.58 s, 2.13 s and 2.76 s, with **0 stalls** — while
the source lay untouched, no file was produced, and nothing was stored beyond the
index.

`transcode-engine` was stopped throughout, which settles the last of the
`api`-versus-engine question by demonstration rather than by argument.

Two operational facts worth keeping, both of which cost time to find:

- **In dev the app binds loopback only** — Core is what faces the network — so a
  device on the LAN needs a forwarder to reach it directly. Ports are Core-assigned
  and live in `HOSTY_PORT_*`; they are not stable and must not be assumed.
- **Port 7000 on macOS is the AirPlay receiver.** Probing it returns a confident
  `403` from `Server: AirTunes`, which reads exactly like the app refusing a request.

- [x] **A whole film end to end on an Apple TV**, including seeking across it —
      played and seeked across; an unattended watch of the whole thing is still
      untried.
- [ ] **Time to first frame from cold**, on a source on the slow disk.
- [ ] **60–80 Mbit/s**, the bitrate class never yet exercised.
- [ ] **Several concurrent clients.**
- [ ] **Multi-audio, sidecar audio, and subtitles** folded into the output.
- [ ] **DV profiles 5 and 8.**
- [x] **E-AC-3** as an `ec-3` entry with a `dec3` descriptor: the access unit's
      substreams are walked so the descriptor can count the dependent ones, and the
      frame duration is read from the blocks it carries rather than assumed to be
      1536. Verified on an Atmos track — repackaged, still reported as Dolby Digital
      Plus with Atmos, and decoding.
- [ ] **AAC**, which needs `mp4a` with an `esds` carrying the audio specific config.
      Nothing in this library uses it, but a client that declares AAC support would
      otherwise be offered a remux with no sound — which is why a source whose only
      audio cannot be packaged is refused outright until each of these lands.
- [ ] **Confirm the measurements above on tvOS.** Everything in "What AVFoundation
      demands" was measured on macOS, which has already proven the more permissive of
      the two in this project.

### Closing the plan

- [x] **`feature.md`** — created with the first shipped behaviour, describing what is
      there now rather than what is intended.
- [ ] **Update [native-playback](../native-playback/feature.md)** where it says
      packaging does not exist yet.
- [ ] **Index** — `node scripts/docs-index.mjs --fix`.
- [ ] **Version bump** — new functionality, so a minor; read `manifest.json` when
      the work lands.

## Open questions

- **Whether folding a sidecar in can avoid the engine.** Everything else on the
  serving path is container parsing and byte arithmetic, but a sidecar is a second
  file whose samples have to join the output, and a subtitle sidecar needs rewriting
  as well. Phase 2 answers it by building the plain case first and seeing what the
  sidecar case actually needs; if it needs the engine, that one operation routes
  there and the rest does not.
- **Whether the source's own signalling should be recorded too.** Nothing captures
  what a file says about itself today, so direct play serves Dolby Vision blind (see
  [media-probe-providers](../media-probe-providers/plan.md)). It does not block this
  feature — here we author the container — but the index walk touches the same
  headers, so the two may be cheaper built together.

## Verification steps

1. `dotnet test` for the API test project.
2. Phase 4 in full on a Core-managed dev runtime.
3. End to end on an Apple TV 4K: an MKV the client cannot open resolves to `remux`,
   plays, seeks across the whole film, and shows the dynamic range the response
   promised.
4. A source with a sidecar dub plays with that dub selected.
5. Confirm Infuse still plays the same titles through the Jellyfin surface — this
   feature is additive and must not disturb it.
