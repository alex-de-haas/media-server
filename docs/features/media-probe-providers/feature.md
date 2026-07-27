# Media Probe Providers

Created: 2026-07-27
Updated: 2026-07-27

Probing a library file runs through two providers behind one `IMediaProbe`. The
external `transcode-engine` leads, because it runs `ffprobe` and therefore knows
things a container cannot state. This app's own container-header reader follows,
so a library keeps working when that dependency is not attached.

This app no longer runs `ffprobe` itself.

## Why the engine leads

The inverse — parser first, engine for verification — was considered and rejected:
it would change behavior for every existing library on day one. Engine-first means
a configured deployment behaves exactly as it did, and the reader only ever adds
capability where there was none.

It also removes a failure mode. Probing used to run a local `ffprobe` whose failure
propagated out of `ProbeStage` and parked the ingest item as a retryable failure.
Now an engine that is absent, unreachable or refusing degrades to the reader, and
only a file **neither** can read fails.

## What each provider knows

`RemoteMediaProbe` addresses files the way job creation does — by media mount label
and relative path — so a file whose catalog root is not bound into the engine
cannot be probed there and falls through. It reports codec profiles, colour read
out of the codec bitstream, and Dolby Vision.

`HeaderMediaProbe` reads the file's own header and nothing else:

| Container | Duration | Track list |
| --- | --- | --- |
| MP4 / MOV / M4V / M4A | `moov/mvhd` | `moov/trak/…` |
| Matroska, including `.mka` / `.mks` | `Segment/Info` | `Segment/Tracks` |
| AVI | `hdrl/avih` (+ `hdrl/odml/dmlh`) | — AVI stores no language or title at all |

Measured against `ffprobe` over a 49-file, 52 GB library: every duration within one
second, worst delta 57 ms on a 2 h 12 m file — the difference between the video and
audio track lengths, not a parse error — reading 11.4 KB in total against 1.66 s of
process time.

It answers `null` wherever a header cannot say. A transport stream's duration, a
codec profile, HDR10 versus HDR10+ — all stay unanswered for the engine to fill in.

## Three container traps, handled

- **OpenDML.** Past ~2 GB an AVI continues in further `RIFF AVIX` segments and
  `avih.dwTotalFrames` counts only the first, so a long file reads short. The true
  total is in `hdrl/odml/dmlh`. Two files in the development library were 1252 s and
  715 s out before this was read.
- **Embedded cover art shifts every index.** `ffprobe` synthesizes a video stream
  for artwork in `moov/udta/meta` and places it at index 1. `TranscodeService`
  passes audio and subtitle **indexes** to the engine, and Jellyfin exposes them to
  clients, so the reader emits the same placeholder rather than numbering the real
  tracks 0,1,2 while `ffprobe` numbers them 0,2,3.
- **Colour can live outside the container.** A remuxed MP4 carried no `colr` box at
  all, yet `ffprobe` still reported PQ — it had parsed the HEVC SPS. A header-only
  provider cannot follow it there, which is why HDR admits *unknown*.

## Provenance

Every `MediaSource` records which provider produced its data. The two do not know
the same things, so a null field means different things depending on it, and rows
read by the weaker provider have to be findable again.

`POST /api/library/backfill-media` re-probes every source still carrying
header-read data. It is deliberately an explicit action rather than something that
fires when the dependency reconnects: a probe is fast enough that a whole-library
pass is a foreground operation, and rewriting stored data on its own the moment a
dependency reappears would be a surprise.

## HDR says how sure it is

`MediaStream.HdrFormat` carries the engine's full vocabulary — `Dolby Vision`,
`HDR10+`, `HDR10`, `HLG`, `SDR` — and is **not** narrowed to what the reader can
produce. The reader fills the same field with what it actually knows: `HLG` for
transfer 18, a generic `HDR` for transfer 16 (PQ, but a header cannot say which
kind), `SDR` when colour data is present and says otherwise, and **null** when the
container carried none at all.

`SDR` and null are different answers. Null means nobody could tell; `SDR` is a
positive statement. An HDR badge appears only for a positive HDR value, so the worst
case is a missing badge rather than a false one — and never an assertion of SDR
about a file nobody could read. Rows written before this feature all came from
`ffprobe`, whose silence really was a negative, so the migration turns their nulls
into `SDR`.

A badge on a poster reads the item's **default source** — the version that actually
plays — rather than aggregating across versions. A movie can hold an HDR remux and
an SDR rip side by side, and a badge describing something the viewer will not get is
worse than none.

## Divergence is logged, not acted on

While both providers can answer, their durations are compared. A disagreement over
one second absolute, or half a percent, is logged with the file, both values and —
for Matroska — the application that wrote it. Anything smaller is container noise,
not a defect.

The reader is on probation: that log is the evidence that decides, field by field,
whether it is ever promoted past being a fallback. Grouping by writing application is
what found the OpenDML defect; "some files are off" would not have.

## Shared vocabulary

Both providers pass through one normalization layer, because results that cannot be
compared make the divergence log worthless. Codec identifiers differ per container
(`V_MPEG4/ISO/AVC`, `avc1`, `h264` are one codec); Matroska's `LanguageBCP47` yields
two-letter codes where the legacy element and `ffprobe` use three; and a track name
lives in Matroska's `Name` but MP4's `udta/name`, which `ffprobe` surfaces as the
`name` tag rather than `title`.

An absent Matroska language element is left **unknown** rather than taking the
spec's `eng` default: a file that never stated a language has not claimed English,
and asserting it would mislabel an untagged dub. `AudioTrackLabeler` infers one from
the path instead. In practice the element is nearly always present — ffmpeg writes an
explicit `und`, which normalizes to no language, matching `ffprobe`.

## The Media tab without an engine

The tab stays: it lists a title's versions and picks which one plays, both
database-side. Only the conversion control is hidden, since it would have nothing to
talk to — the same graceful degradation `DisabledTranscodeEngine` already provides
for transcoding itself.

## Testing Expectations

- `HeaderMediaProbeTests` — the reader over containers built byte by byte: MP4
  duration in both header versions, track mapping, embedded cover art shifting later
  indexes, Dolby Vision outranking the transfer function; Matroska duration scaled by
  its timestamp scale, track flags and names, every HDR case, BCP-47 language
  normalization, an unknown codec id passed through, an absent duration element, the
  writing application, and an `.mka` sidecar read as Matroska; AVI duration and the
  OpenDML override, and AVI reporting no track list; plus the refusals — an
  unsupported container, a truncated file, and something that is not a container.
- `RemoteMediaProbeTests` — the engine-backed provider against a stubbed transport:
  addressing by mount label rather than absolute path, the vocabulary translation,
  every HDR value the engine can report, indexes carried through including the
  synthesized artwork entry, unmodelled kinds dropped without disturbing indexes, and
  declining — never throwing — for a file outside every mount, a refusal, an
  unreachable engine and unreadable output.
- `CompositeMediaProbeTests` — the engine's answer winning when it has one, a
  refusing engine degrading to the header, no engine configured, a file neither can
  read still failing, and the divergence report: logged with the writing application
  when material, silent for container noise and when only one provider answered.
- `AudioTrackLabelerTests` — language and title inference over both release layouts.
