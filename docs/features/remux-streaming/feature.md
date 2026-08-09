# Remux Streaming

Created: 2026-08-08
Updated: 2026-08-09

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
seeking past every payload. On production a feature film costs a minute or two.

Each sample is an offset and a size into the source, because the design references
samples rather than copying them. Alongside them the index carries what an MP4 sample
entry needs, all of it taken from the source rather than derived: `CodecPrivate` is
already the payload of `hvcC` or `avcC`, and the Dolby Vision configuration is already
the payload of `dvvC`, in a `BlockAdditionMapping` whose type is literally `dvcC`.

**Only tracks a sample entry can be written for are walked** — the video codecs
`Mp4Writer` knows, AC-3 and E-AC-3, and text subtitles. Every other track is still listed,
because its ordinal is what keeps a viewer's stored stream indexes lined up with the file
and the resolver has to see it to explain why it cannot be used, but its frames are never
delaced, measured or recorded. `RemuxCodecs.WantsSamples` is the single place that answers
this, so the walk and the synthesiser cannot disagree.

That filter exists because of a measurement rather than a hunch. Before it, 147 indexes
came to 1.2 GB, and four files held 43 % of that: one 50-track remux at 273 MB, and a
5-track film whose single TrueHD track was 96 % of its own index — all of it describing
frames nothing could ever point at.

Two things the walk has to get right:

- **A block is not a sample.** Matroska laces audio — fixed lacing on AC-3 in this
  library, EBML lacing on DTS — so a block may hold many frames. They stay contiguous
  in the source, so each is still a plain offset and size; only the arithmetic differs.
  A lacing header that does not add up leaves the block whole rather than slicing it
  into samples pointing outside it. All three lacing forms stay handled even though DTS
  is no longer walked: nothing says AC-3 will never arrive EBML-laced.
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
offsets both climb in small repetitive increments, so variable-length deltas take a
sample from about 21 bytes to **between 7 and 13**. Loading one takes a fraction of a
second, so it is read per request rather than held in memory.

It is a range rather than a number, and the reason is worth knowing: between two
consecutive frames of one track lie all the other tracks' data for that stretch of the
film, so an offset step is the **interleaving stride**, not the frame size — it grows
with the number of tracks in the file. Production measures 7.3 bytes a sample on a
five-track file, 9.0 on a twenty-five-track one and 11.4 on a fifty-track one.

Invalidation needs no schema. The header carries the source's length and last-write
time, so a file that was replaced or re-encoded invalidates its own index and the caller
rebuilds; a format version bump does the same for every index at once. Each is written
aside and moved into place, so an interrupted build leaves nothing to mistake for an
index, and a truncated or foreign file reads as "no index" rather than as an error.

`RemuxIndexWorker` builds missing ones in the background, **one at a time**. The walk
turns out not to be bound by the disk it reads — see [Measured](#measured) — but it is
still the least urgent thing in the process, and running several at once would take cores
and disk from scans and playback to finish a chore nothing waits on. There is no queue:
the database knows which sources exist and the store knows which have an index, so the
outstanding work is a query and a restart resumes without remembering anything. Orphaned
indexes are pruned once per process, since nothing else deletes a file when its title
goes.

Each finished walk logs what it cost and what it passed over:

```text
Indexed <path> in 162.3s: 21474836480 bytes at 126 MB/s, 4/7 tracks, 812004 samples;
skipped A_DTS x2, S_HDMV/PGS x1.
```

Sidecar dubs are indexed too, keyed by their stream row rather than by a media source —
the store does not care which owns an index, so an external `.mka` is walked exactly as
its video is, and both keep theirs alive against pruning.

Only Matroska is indexed. An MP4 source is already playable and has nothing to gain.

## What the container carries

`Mp4Synthesizer` writes descriptors rather than deriving them. Audio is the exception:
neither AC-3 nor E-AC-3 has any `CodecPrivate` in Matroska — every frame restates its
own parameters — so `dac3` and `dec3` are read out of a sync frame rather than believed
from the container.

E-AC-3 is not AC-3 with a different name. Its access unit may hold several substreams,
one independent and then any dependent ones carrying extra channels; they are walked by
their stated sizes, and a unit that has dependents is **left out** rather than described
as its base layout, because saying "5.1" about a 7.1 stream is a claim a player is
entitled to believe. A frame is also **not** always 1536 samples: it carries one, two,
three or six blocks of 256. The count is read from the frame, and read again over a
spread of the track — a stream that varies it is refused rather than given a timeline
built on its first frame, which would drift for the whole of its length.

Atmos rides on E-AC-3 and survives untouched, because the samples are the same bytes.

- **Dolby Vision** is offered as a `dvh1` sample entry, and only for HEVC that came with
  a configuration, and only when the client asked. The cross-compatible `hvc1` form
  still carries `dvvC`, which is what makes a client without Dolby Vision see HDR10.
- **H.264** keeps `avc1` whatever is asked for.
- **The picture is the first video track a sample entry can be written for**, not
  simply the first. A muxer may write cover art as a real video track, and taking that
  because it comes first would produce an output with no picture. (In this library
  cover art is a Matroska attachment instead, which the indexer never sees at all —
  `ffprobe` surfaces attachments as streams, which is where the warning came from.)
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

A subtitle **beside** the video joins the same path. It has no index and needs none —
a film's dialogue is a hundred kilobytes, so a `.srt`, `.ass` or `.vtt` is parsed per
request into cues, which is exactly what an embedded track becomes before the timeline
is laid out. Timestamps are read whether they use a comma, a dot, or ASS's hundredths.

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

The `ETag` covers everything the answer is made of — the source, the tracks chosen, the
signalling asked for, and every sidecar carried with it — and `Last-Modified` is the
freshest of them. A subtitle file edited in place changes the body without changing
anything about the source, and a conditional request must not be told otherwise.

`POST /native/v1/playback/resolve` decides. Beside the existing `Decision` it carries a
**`Transport`**, which is `byteRange` and only that: HLS is a second transport the
design makes room for and which has been deliberately deferred, because the one that
ships carries Dolby Vision and nothing has yet asked for one that does not. The axis
exists because adding it later would have been a breaking change.

A remux is refused, with the reason said plainly, when:

| Reason | Meaning |
| --- | --- |
| `packaging_pending` | Indexable, but the walk has not reached it. Retrying later succeeds; the URL answers `503` meanwhile. |
| `packaging_unavailable` | Nothing here can index this container, and nothing later will. |
| `packaging_unsupported_audio` | The client could decode this audio, but no sample entry can be written for it — so a remux would play silently. |
| `packaging_unsupported_video` | The same for the picture — so a remux would be the soundtrack alone. |

The two `unsupported_*` answers are given **before** `packaging_pending`, because they are
permanent: a picture nothing here can describe will not become describable when the walk
reaches the file, and "not yet" about a source that will never work is the more misleading
of the two.

The audio one is why what packaging can describe lives in **one** place, in both
vocabularies that ask: the resolver reasons in the probe's names and the synthesiser in
Matroska's, and when they drifted the result was a source advertised as remuxable whose
only audio track was then quietly declined. Audio is AC-3 and E-AC-3; AAC would need an
`mp4a` entry with an `esds`, and DTS and TrueHD are out of scope for this client.

The same gap exists on the video axis and is closed the same way, in both places: the
resolver refuses to advertise a URL it knows will fail, and the stream service refuses to
serve one that was asked for anyway. AV1 is where the two questions part company — a
recent Apple TV decodes it and `Mp4Writer` has no entry for it — so a source with a
picture that cannot be described is refused outright rather than served as the soundtrack
that is left. The serving-side guard is the stricter of the two, because by then it can
also see whether the configuration record arrived at all. A source that never
had a picture is untouched by that rule.

Within a source the same reasoning picks the default audio: **the first track that can be
described, not the first in the file.** A film that leads with TrueHD and keeps AC-3
behind it is the ordinary layout for anything remuxed from a disc, and a stored preference
pointing at the lossless track falls back exactly as a stale one does.

## Verified

On an Apple TV 4K (tvOS 26.5), against a 26.37 GB HEVC Dolby Vision profile 8.1 source:
playback at 3840×2160 **in Dolby Vision**, seeking across the two-hour film in 0.58 s,
2.13 s and 2.76 s, with no stalls — while the source lay untouched and nothing was
produced or stored beyond the index. `transcode-engine` was stopped throughout, which is
why this lives in `api`: nothing on the serving path invokes ffmpeg.

## Measured

The first production run indexed 26 films in 57 minutes back to back — a median of 97 s
each and a mean of 131 s, the two-second pause between files invisible against them. At
that rate a 300-title library is a one-off cost of about eleven hours, and nothing waits
on it.

**The walk is not disk-bound.** This was the open question the run existed to answer, and
it came back the other way. The cleanest pair: two files of near-identical sample count,
2.81 M on an SSD and 2.82 M on a spinning disk, took 204.5 s and 162.3 s — the HDD
*faster*. Normalised, the two HDD files sit at 17.4 k and 20.0 k samples a second against
an SSD median of 19.7 k. The remedy the plan had held in reserve — sequential reads with a
large buffer instead of seeking per block — would have addressed a bottleneck that is not
there.

Two caveats stated rather than buried: only two files in that sample were on the HDD, and
the log line did not yet carry file sizes, so the comparison is by sample count and not by
bytes. It does now, which is what will settle it properly.

What the same run did surface was the index size that led to the codec filter above.

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
- **What is not walked**: a track no sample entry covers keeps its ordinal, codec and
  channel count but gets no samples — checked for lossless audio, bitmap subtitles and a
  video track that arrived without its configuration. The synthesiser's own refusal is
  covered separately, through a hand-built index, so the guard does not go stale now that
  the walk rarely reaches it.
- **Track choice**: the default audio is the first that can be *described*, not the first
  in the file, and a stored preference pointing at a lossless track falls back the same
  way a stale one does — a film that leads with TrueHD must not play as a silent one. A
  picture that cannot be described is refused rather than served as audio alone, while a
  source that has no picture at all still is — and the resolver refuses to advertise it in
  the first place, including when the walk has not yet reached the file.
- **The stream**: reads that stop at every boundary, seeks that land inside a part, and
  a seek before the beginning refused rather than silently allowed.
- **The service**, over a seeded library: what is pending, what is not, what a changed
  file does, and that orphans are pruned while live indexes are kept.
- **A second input**: that it gets a wrapper of its own, that the wrapper sits between
  the files, and that its samples are addressed past the first file rather than from
  the same base.
- **Subtitle files**: SubRip with and without cue numbers, WebVTT's dotted timestamps,
  ASS dialogue counted in hundredths, cues returned in start order however the file
  lists them, and one with no duration or no words dropped rather than carried.

New codec support — `ec-3`, `mp4a`, a second video codec — must extend
`RemuxCodecs` and the synthesiser together, and a test must assert that a source
carrying only that codec is offered rather than refused.
