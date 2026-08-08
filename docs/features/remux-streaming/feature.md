# Remux Streaming

Created: 2026-08-08
Updated: 2026-08-08

A Matroska source is served to a native client as an MP4, without a second copy on
disk and without producing anything at play time. The container is **computed**: an
index built in the background says where every sample lives, and the header is
assembled per request over the file as it stands.

```text
[ftyp][moov][text mdat][mdat header][ ...the whole .mkv, byte for byte... ]
```

An `mdat` is an opaque blob, so it wraps the entire source and the sample table points
at payload positions inside it. An output offset is the header's length plus the source
offset, and answering a byte range is reading the same range from the source. No byte
of media is moved, copied or stored; the Matroska framing bytes inside `mdat` are never
referenced by any sample.

The layout takes several inputs, one `mdat` per wrapped file, which is what lets a
**sidecar dub** be carried: an external audio track is a second file, and its samples
join the video's in the same container. The wrappers after the first sit *between* the
files, where the sample offsets expect them.

## The index

`MatroskaIndexer` walks a file into per-track sample tables, reading element headers and
seeking past every payload. A 26 GB film costs about 25 s and produces about 9 MB.

Each sample is an offset and a size into the source, because the design references
samples rather than copying them. Alongside them the index carries what an MP4 sample
entry needs, all of it taken from the source rather than derived: `CodecPrivate` is
already the payload of `hvcC` or `avcC`, and the Dolby Vision configuration is already
the payload of `dvvC`, in a `BlockAdditionMapping` whose type is literally `dvcC`.

Two things the walk has to get right:

- **A block is not a sample.** Matroska laces audio — fixed lacing on AC-3 in this
  library, EBML lacing on DTS — so a block may hold many frames. They stay contiguous
  in the source, so each is still a plain offset and size; only the arithmetic differs.
  A lacing header that does not add up leaves the block whole rather than slicing it
  into samples pointing outside it.
- **A `BlockGroup`'s keyframe answer is the absence of a `ReferenceBlock`**, not a flag.
  Getting it wrong fills the sync table with the wrong entries and makes seeking land in
  the wrong places.

The EBML primitives are shared with [`ContainerHeader`](../media-probe-providers/feature.md)
rather than duplicated: two walkers over one format would drift.

## Where indexes live

One file per media source under the app data directory, beside the torrents rather than
in the database. An index is derived data — large next to a row, rebuildable, and of no
interest to a backup.

The file stores the steps rather than the values. Within a track the timestamps and
offsets both climb in small repetitive increments, so variable-length deltas cost
**5.6 bytes a sample** where fixed-width fields would cost about 21: 9.33 MB for a
26.37 GB film, around 0.04 % of the source. Loading one takes 0.1 s, so it is read per
request rather than held in memory.

Invalidation needs no schema. The header carries the source's length and last-write
time, so a file that was replaced or re-encoded invalidates its own index and the caller
rebuilds; a format version bump does the same for every index at once. Each is written
aside and moved into place, so an interrupted build leaves nothing to mistake for an
index, and a truncated or foreign file reads as "no index" rather than as an error.

`RemuxIndexWorker` builds missing ones in the background, **one at a time** — the walk
is bound by the disk it reads, and several at once would be slower in total while making
everything else on that disk worse. There is no queue: the database knows which sources
exist and the store knows which have an index, so the outstanding work is a query and a
restart resumes without remembering anything. Orphaned indexes are pruned once per
process, since nothing else deletes a file when its title goes.

Sidecar dubs are indexed too, keyed by their stream row rather than by a media source —
the store does not care which owns an index, so an external `.mka` is walked exactly as
its video is, and both keep theirs alive against pruning.

Only Matroska is indexed. An MP4 source is already playable and has nothing to gain.

## What the container carries

`Mp4Synthesizer` writes descriptors rather than deriving them. Only `dac3` is parsed,
because an AC-3 track in Matroska has no `CodecPrivate` at all — every frame restates
its own parameters — so the channel count and sample rate are read out of a sync frame
rather than believed from the container.

- **Dolby Vision** is offered as a `dvh1` sample entry, and only for HEVC that came with
  a configuration, and only when the client asked. The cross-compatible `hvc1` form
  still carries `dvvC`, which is what makes a client without Dolby Vision see HDR10.
- **H.264** keeps `avc1` whatever is asked for.
- **`colr`** is written from the `Colour` element and left out when the container is
  silent, which this library's own files are — they keep it in the HEVC SPS instead, and
  Dolby Vision engages from the bitstream regardless.
- **Timing comes from the file.** Taking `DefaultDuration` as a constant drifts by half
  a minute over a two-hour film. The decode timeline is the presentation timestamps in
  sorted order, and a composition table is written only where frames are reordered.
- **Each track counts in its own units** — the source's ticks for video and text, the
  stream's sample rate for audio, where 1536 samples a frame is exact at any rate. A
  nanosecond timescale would make a 32-bit sample delta top out at 4.29 s, which is
  shorter than the gaps a subtitle track routinely has.

### Subtitles are rewritten, not referenced

A SubRip or ASS sample is not a valid MP4 subtitle sample: `tx3g` wants a
length-prefixed string, and the gaps between cues need empty samples that exist nowhere
in Matroska. So the text is converted and carried in a **second `mdat` inside the
header** — a film's dialogue is a hundred kilobytes against a source of gigabytes.

SubRip gives up its markup; an ASS row gives up its fields and override codes, and a
comma inside the line is not mistaken for a separator. Styling is lost, which
[the epic](../apple-client/plan.md) accepted for text subtitles. A cue with no stated
duration is dropped rather than guessed at, because MP4 has no "until the next one", and
bitmap subtitles are left out for the same honesty.

## What is offered, and what is refused

`/native/v1/media/{mediaSourceId}/remux` serves the computed MP4 under the same signed
URL token, catalog sandbox and visibility rules as direct play. Tracks are named by
**stream id**, not by position: a sidecar lives in its own file and has no position in
the container at all. The header and the
untouched source are presented as one seekable stream, so byte ranges are handled by the
framework's own file result — which matters, because AVFoundation refuses a server that
will not declare a total length, and reads a truncated answer to an explicit range as a
failed request rather than a smaller one.

`POST /native/v1/playback/resolve` decides. Beside the existing `Decision` it now carries
a **`Transport`** — `byteRange` today — because HLS would be another way to deliver a
repackaging rather than a fourth kind of decision.

A remux is refused, with the reason said plainly, when:

| Reason | Meaning |
| --- | --- |
| `packaging_pending` | Indexable, but the walk has not reached it. Retrying later succeeds; the URL answers `503` meanwhile. |
| `packaging_unavailable` | Nothing here can index this container, and nothing later will. |
| `packaging_unsupported_audio` | The client could decode this audio, but no sample entry can be written for it — so a remux would play silently. |

That last one is why what packaging can describe lives in **one** place, in both
vocabularies that ask: the resolver reasons in the probe's names and the synthesiser in
Matroska's, and when they drifted the result was a source advertised as remuxable whose
only audio track was then quietly declined. Audio is AC-3 only today; E-AC-3 needs an
`ec-3` entry with a `dec3` descriptor, and AAC an `mp4a` with an `esds`.

## Verified

On an Apple TV 4K (tvOS 26.5), against a 26.37 GB HEVC Dolby Vision profile 8.1 source:
playback at 3840×2160 **in Dolby Vision**, seeking across the two-hour film in 0.58 s,
2.13 s and 2.76 s, with no stalls — while the source lay untouched and nothing was
produced or stored beyond the index. `transcode-engine` was stopped throughout, which is
why this lives in `api`: nothing on the serving path invokes ffmpeg.

## Testing Expectations

- **The indexer**, over Matroska written by hand rather than remuxed — ffmpeg never
  laces, so the case that matters most cannot be produced any other way. All three
  lacing forms, a `BlockGroup` whose keyframe answer is the absence of a
  `ReferenceBlock`, a lacing header that does not add up, and offsets checked against
  the actual bytes rather than against an assumed header length.
- **The store**: a round trip field by field including the Dolby Vision configuration
  bytes and non-monotonic timestamps; refusal when the source changed length or was
  rewritten; a truncated or foreign file read as "no index"; no partial left behind.
- **The synthesiser**, by reading the emitted boxes back — which sample entry was
  written and why, `colr` present or deliberately absent, a constant frame rate
  collapsing to one timing run, a composition table only where frames are reordered, a
  sync table omitted when every sample is one, and a long subtitle gap that would
  overflow a narrower timescale.
- **What is refused**: a track that cannot be described is left out, and a source whose
  only audio cannot be packaged is refused rather than served silently.
- **The stream**: reads that stop at every boundary, seeks that land inside a part, and
  a seek before the beginning refused rather than silently allowed.
- **The service**, over a seeded library: what is pending, what is not, what a changed
  file does, and that orphans are pruned while live indexes are kept.
- **A second input**: that it gets a wrapper of its own, that the wrapper sits between
  the files, and that its samples are addressed past the first file rather than from
  the same base.

New codec support — `ec-3`, `mp4a`, a second video codec — must extend
`RemuxCodecs` and the synthesiser together, and a test must assert that a source
carrying only that codec is offered rather than refused.
