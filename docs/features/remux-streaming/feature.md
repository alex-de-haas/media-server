# Remux Streaming

Created: 2026-08-08
Updated: 2026-08-15

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
`Mp4Writer` knows, AC-3, E-AC-3 and AAC, and text subtitles. Every other track is still
listed, because its ordinal is what keeps a viewer's stored stream indexes lined up with
the file and the resolver has to see it to explain why it cannot be used, but its frames
are never delaced, measured or recorded. `RemuxCodecs.WantsSamples` is the single place that answers
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

One file per media source under the Hosty cache directory
([cache-storage](../cache-storage/feature.md)), as files rather than database rows. An
index is derived data — large next to a row, rebuildable, and of no interest to a
backup — and the cache directory is precisely that contract: it survives restarts and
updates but is never backed up, so the indexes stopped riding along in every snapshot.
Under a Core that predates the contract the store falls back to the app data
directory, which is the old layout; a one-time startup migration moves files from
`data/remux-index/` into the cache when the two differ.

The file stores the steps rather than the values. Within a track the timestamps and
offsets both climb in small repetitive increments, so variable-length deltas take a
sample from about 21 bytes to **between 7 and 13**. Loading one takes a fraction of a
second, so it is read per request; what is kept between requests is the *built header*, for the reason below.

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

`RemuxIndexWorker` builds missing ones in the background, **one at a time** — the walk is
bound by the disk it reads wherever that disk spins ([Measured](#measured)), so several at
once would be slower in total while making everything else on that disk worse. It is also
the least urgent thing in the process, and nothing waits on it. There is no queue:
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

`Mp4Synthesizer` writes descriptors rather than deriving them. Audio splits three ways:
neither AC-3 nor E-AC-3 has any `CodecPrivate` in Matroska — every frame restates its
own parameters — so `dac3` and `dec3` are read out of a sync frame rather than believed
from the container. **AAC is the opposite and the easy one**: Matroska stores the
AudioSpecificConfig verbatim, and that is exactly the payload MP4 wants inside its
`esds`, so it is carried through untouched and only *read* to learn the rate, the
channel count and the frame length.

What AAC refuses, and why each is refused rather than guessed at:

- **Explicitly signalled SBR or PS.** These declare a second, higher sampling frequency,
  and the two conventions for which one belongs in the sample entry disagree. Getting it
  wrong plays the track at half or double speed, silently. Implicit SBR is untouched and
  works: the core frame is still 1024 samples at the core rate, so the wall-clock
  duration is the same however the decoder expands it.
- **A channel configuration of zero**, which defers to a program config element inside
  the first frame — a bitstream read this deliberately does not do.
- **Anything that is not plain AAC** — LD, ELD, scalable. Their frame lengths differ and
  none of them is in this library.

The frame length is read rather than assumed: 1024 samples, or 960 when the config's
`frameLengthFlag` says so. That is the same trap E-AC-3 set with its block count, and it
is read for the same reason — a wrong constant plays at the wrong speed rather than
failing.

### Priming is trimmed, not heard

An AAC encoder emits about a frame of priming before the first real audio. Matroska
states it in `CodecDelay` and expects the demuxer to drop it; MP4 has no such convention,
so it becomes an **edit list**. Without one the soundtrack starts 1024 samples — 21 ms —
after the picture.

The conversion is rounded rather than truncated. A frame of priming at 48 kHz is
21333333.33 ns, the container stores whole nanoseconds, and truncating the way back gives
1023 samples and leaves the track one sample late.

Both of these were found by remuxing a real file and decoding both sides, not by reading
the specification — see [Measured](#measured).

E-AC-3 is not AC-3 with a different name. Its access unit may hold several substreams,
one independent and then any dependent ones carrying extra channels; they are walked by
their stated sizes, and a unit that has dependents is **left out** rather than described
as its base layout, because saying "5.1" about a 7.1 stream is a claim a player is
entitled to believe. A frame is also **not** always 1536 samples: it carries one, two,
three or six blocks of 256. The count is read from the frame, and read again over a
spread of the track — a stream that varies it is refused rather than given a timeline
built on its first frame, which would drift for the whole of its length.

Atmos rides on E-AC-3 and survives untouched, because the samples are the same bytes.

### Every describable track is carried, not just the chosen one

The container holds **all** the audio tracks a sample entry can be written for, and all the
text subtitles, each kind with the viewer's choice first.

That order is the whole mechanism for video and audio: a player takes the first track of a
kind as its default, so the choice arrives as *ordering* rather than as omission, and only
that track is marked `enabled`.

**Subtitles are the exception, and getting it wrong is loud.** Carrying one for the menu is
not the same as turning it on, so no subtitle is enabled unless the viewer asked for one —
otherwise every source with embedded subtitles would put words on screen unbidden. A chosen
*external* subtitle is the awkward case: it is prepared after the referenced tracks, so
being chosen is not enough to make it first, and it has to overtake the embedded ones
explicitly.

Each track carries **its own language**, packed into `mdhd` as ISO-639-2 rather than left at
`und`. Six dubs a viewer cannot tell apart in the menu would be barely better than carrying
one of them. Tracks of one kind also share an `alternate_group`, saying in the file what a
player would otherwise have to infer.

The alternative was one audio track per request, which is what this used to do. It gave
`AVPlayerViewController` nothing to choose between, so changing a dub meant asking for a
different URL and re-seating the player at the current time — a visible re-buffer, and a
track picker every client would have to build for itself.

The cost is header. A sample table is around twelve bytes a sample once `stsz` and `co64`
are counted, so an audio track of a feature film adds a couple of megabytes to what is
fetched before the first frame. It is paid once, at open, by a player that already walks
every box header of the whole file before it shows anything.

A **sidecar dub** leads the list when one is chosen, and the file's own tracks follow it
into the same menu rather than disappearing because a dub was picked.

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
only audio track was then quietly declined. Audio is AC-3, E-AC-3 and AAC; DTS, TrueHD
and FLAC are out of scope for this client.

The Matroska side is the stricter of the two for AAC, and strict in a particular way: it
does not ask whether a config is *present*, it **parses** it, with the same routine that
would build the descriptor. Asking the cheaper question would answer yes for an
explicitly signalled SBR stream that the descriptor then declines, and the track would be
walked, chosen and finally dropped — the exact failure this shared vocabulary exists to
prevent.

One asymmetry remains, and it is stated rather than hidden: the resolver reasons about
`aac` as a name and cannot see the config, so a source whose only AAC track is explicitly
signalled SBR is still advertised and then refused at the request. The client meets a
refusal instead of a silent film, which is the important half; the wasted round trip is
the residual cost, and no such source is in this library.

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

### The output, against the source it was made from

A real HEVC + AAC Matroska was synthesised into an MP4, and both were decoded to raw PCM
and compared sample for sample. The result: **byte-identical**. ffmpeg's own remux of the
same source lands 16 samples away from it.

Two defects surfaced there that no unit test would have caught, because both are about
agreement with a decoder rather than about the bytes written:

| Found | Effect | Fix |
| --- | --- | --- |
| No edit list | Soundtrack 1024 samples (21 ms) late | Trim `CodecDelay` via `elst` |
| Truncated ns→samples conversion | One sample late | Round instead |

The same round trip over an AC-3 source shows the audio **256 samples — 5.3 ms — late**
against the source's own decode. That is not new, is unchanged by the AAC work, and is
left alone deliberately: the container states no delay for AC-3, so acting on it would
mean hard-coding one encoder's behaviour, and 5.3 ms is far below the threshold at which
a lagging soundtrack is perceptible. It is recorded here with the number so nobody has to
measure it twice, and carried as an open deliverable in the plan.

### The walk

The number the log reports is the source's length divided by the elapsed time. That is a
**traversal rate over the file's logical span, not measured device throughput** — the walk
reads element headers and seeks past every payload, and how many bytes the disk actually
delivered is not instrumented. Read it as "how fast the walk gets through a file of this
size", which is the figure that matters for planning, and not as a device benchmark.

| Where | Files | Traversal rate |
| --- | --- | --- |
| Anime episodes, spinning disk | 64 | **103–119 MB/s** |
| Films, spinning disk | 10 | 95–146 MB/s, nine of them ≤ 113 |
| Films, SSD | 6 | 125–287 MB/s |

Extras under 200 MB are left out: at one to five seconds, start-up dominates them and they
scatter from 35 to 132 MB/s.

**The clustering is the evidence, not the absolute number.** Sixty-four anime episodes,
differing in size, all land within 16 % of each other; nine of the ten films on the same
disk sit in the same band, with Poseidon the one outlier at 146. On the SSD the same walk
spreads over more than a factor of two. A rate that barely moves across wildly different
files is the signature of a shared resource at its limit, and the disk is the resource the
two groups share and the SSD files do not. That is what supports the conclusion — the
`~105` on its own could not, since the denominator is bytes spanned rather than bytes read.

In practice a 20 GB film off the HDD is about three minutes. The failure mode the plan
feared — the walk degenerating into a seek per block — did not happen: that would have put
the traversal rate an order of magnitude lower.

**An earlier reading of this said the opposite, and it was wrong.** The first production
run had no file sizes in its log, so throughput was estimated as samples per second — and
a sample count does not track file size at all. Two files with near-identical sample counts
were compared, the HDD one came out faster, and the conclusion drawn was that the walk is
not disk-bound. Adding bytes to the log line is what exposed it. The prediction the plan
had written down before any of this — *size over throughput, minutes per film* — was right
all along.

That is also why `RemuxIndexWorker` builds one index at a time: on the disk where it
matters, several at once would compete for the one thing that is actually scarce. Worth
naming what would settle this beyond doubt, since the traversal rate cannot: bytes actually
read, or the disk's own utilisation while a walk runs. Neither is instrumented, and neither
is worth instrumenting for a background chore already inside its budget.

What the same run surfaced was the index size that led to the codec filter above, and one
gap worth naming: a whole anime series walks **1 track of 5**, its every audio track being
AAC or FLAC. Those titles are indexed and still refused, with
`packaging_unsupported_audio`. Support for `mp4a`/`esds` stopped being theoretical the day
that was measured.

## The header is built once and kept

Synthesising a header is cheap arithmetic over an index that is already in memory: 80 ms to read a
12 MB index, 96 ms to lay out the sample tables. What is not cheap is that the synthesiser opens the
**film**. Subtitle text is rewritten rather than referenced, so every cue is read from the source; an
E-AC-3 track is probed at sixty-four places to confirm its frame size does not vary.

A title with nine subtitle tracks costs some eighteen thousand scattered reads across thirty
gigabytes — and it paid them again on **every byte-range request**, of which a player makes one after
another for as long as it plays. Measured on production: seven seconds to first byte, and a film on a
spinning disk that stopped rather than played, while the same file through the Jellyfin surface
played perfectly.

`RemuxHeaderCache` keeps the built result under the same key the ETag uses, so anything that changes
the body — a different dub, an edited subtitle file, a replaced source — lands on a different entry.
512 MB, least recently used evicted first.

It does not make the **first** request cheap on its own, which is why the walk now keeps what the
synthesis used to fetch.

## The walk keeps what synthesis used to fetch

Three things were read out of the film every time a header was built, and all three are fixed the
moment the file is written:

| Kept | Instead of |
| --- | --- |
| The converted text of every subtitle cue | Reading each one from the source — thousands of seeks, and 97 % of the total |
| The first access unit of each audio track | A seek per track to describe it |
| Whether every audio frame carries the same number of samples | Sixty-four probes per E-AC-3 track |

The last is now *stricter* as well as cheaper: the walk answers it over every frame in the track
rather than over sixty-four of them, and a track whose frames disagree is refused rather than given a
timeline built on its first frame.

The cost is index size — a thirteen-track film's index is 9.2 MB, most of it dialogue — and the walk
reads subtitle payloads rather than seeking past them. Both are paid once, in the background.

On a fast disc this changes nothing measurable: 8,800 reads there are milliseconds. It is the spinning
disk this is for, where the same reads are seconds and playback stopped rather than played.

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
- **AAC**, at the level a wrong answer would be silent rather than loud: the frame length
  read from the config rather than assumed, an escaped sampling frequency, the descriptor
  chain's tags walked the way a demuxer walks them, the config carried through byte for
  byte, an edit list written only where there is priming to trim and rounded rather than
  truncated, and every config the reader cannot be sure of refused.
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
