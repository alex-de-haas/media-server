# Track Extraction

Created: 2026-08-07
Updated: 2026-08-07

A version's embedded audio and subtitle tracks can be written out as files beside
it, each recorded as an external `MediaStream` of the same source.

This is the exact inverse of merging, and it lands on the model that already
exists: an extracted track **is** a sidecar, indistinguishable from one a release
shipped. Everything
[external-track-sidecars](../external-track-sidecars/feature.md) does — listing
them apart in the Media tab, removing one entry-or-file, merging a chosen set back
into a new version, filling in missing specs on the backfill, taking them along
when the video is deleted — applies to an extracted track with nothing written
for it.

## What it is for, stated honestly

The two kinds are not equally useful, and the surface says so rather than offering
one control that means two different things.

**A subtitle earns its file.** Clients read sidecar subtitles from disk, the naming
convention this app writes is the one they match on, and as a file a subtitle can
be retimed, corrected, or handed to something else. It also survives a remux that
drops the embedded track.

**An audio track does not become playable.** External audio is inert — Infuse
supports it by no route, and Jellyfin has no delivery for it either. Extracting a
dub is therefore an **archival** act: it is how a track is kept before the container
is rebuilt without it. That is a real workflow (a 141 GB remux in the development
library is 87.5 GB of audio), but it is a two-step one, and the dialog says so on
the audio heading rather than in a footnote.

## The container is not touched

Extraction copies out; it never rewrites the video. Dropping a track from the
container stays what it already is — a conversion composed in the
[Convert dialog](../convert-dialog/feature.md).

So a track exists in **two places** afterwards, which is designed for rather than
discovered:

- **Re-extracting is refused, not duplicated.** An extracted row records the track
  it came out of (`MediaStream.SourceStreamIndex`), so "already out" is a property
  of the row. Removing the sidecar makes the track extractable again — an operator
  who deleted it is asking for exactly that.

  **The job history cannot answer this**, which is why the field exists. Dismissing
  a terminal job is an ordinary action and cascades its output rows away; a job
  whose import only partly succeeded ends up `Failed` with the sidecars it did
  produce still on disk. Either would let the guard forget a track was out and
  write a second copy under a different name — the naming rule avoids the
  collision, so nothing else would notice.
- **Merging an extracted sidecar back into its own version is left alone.** It
  would produce a container carrying that track twice, and nothing guards against
  it, because the Convert dialog already holds the controls to avoid it: the
  embedded track can be dropped in the same job that folds the sidecar in. A guard
  would refuse a combination the operator can already compose correctly.
- **Nothing in the UI says a sidecar was extracted.** The Media tab makes no
  distinction between one this app produced and one a release shipped — that would
  be a difference the sidecar model draws nowhere else, on every row, to annotate a
  case the operator created moments earlier. `SourceStreamIndex` exists only to
  refuse a second extraction and is never displayed.

## Formats

**Audio is always `.mka`.** Matroska carries the language and the title inside the
file, which is precisely the property the sidecar feature values it for — it is why
a `.mka` never needs its name to be authoritative, and why `AudioTrackLabeler`
exists only for the elementary streams (`.ac3`, `.eac3`, `.dts`, `.aac`) that have
nowhere to put one. Writing a raw elementary stream here would manufacture that
problem on purpose. The stream is copied byte for byte and the row's language and
title are written into the container as it is produced, so the file reads back
correctly on any later scan.

**Subtitles are text only, in their own format:**

| source codec | output | how |
| --- | --- | --- |
| `subrip` | `.srt` | copy |
| `ass`, `ssa` | `.ass` | copy |
| `webvtt` | `.vtt` | copy |
| `mov_text` | `.srt` | `-c:s srt` — text to text, the one conversion here |

**Nothing touches the character encoding, and that is a guarantee rather than an
omission.** An embedded text subtitle is already UTF-8 by its container's
specification — Matroska says so in the codec id itself (`S_TEXT/UTF8` *is*
`subrip`; `S_TEXT/ASS` and `S_TEXT/SSA` are specified the same way), and `mov_text`
is UTF-8 by the 3GPP timed-text spec. Converting from a legacy code page was the
muxer's job when the release was built, so a stream copy writes the same bytes back
out. It follows that **mojibake in an extracted file was already in the source** —
a release muxed under the wrong declared charset, visible during playback too — and
extraction reproduces it faithfully instead of guessing. A charset detector would
apply a heuristic to data that is correct by construction. No BOM is written
either: clients read BOM-less UTF-8, and a leading BOM breaks naive parsers on the
index line.

**Bitmap subtitles are not extracted** — `hdmv_pgs_subtitle`, `dvd_subtitle`,
`dvb_subtitle` and `xsub` are refused. They already reach the viewer by Direct Play
from the container, no client reads them better as a file, and VobSub needs an
`.idx`/`.sub` *pair*, which breaks the one-track-one-file assumption in both the
sidecar model and the engine's publish protocol. The dialog shows such a row as
unselectable and says why, rather than letting a composed job come back as a 400.

A subtitle whose codec the library does not know is refused too, naming the fix:
there is no telling what file it should become, and guessing would write an `.srt`
full of nothing.

## Naming

`SidecarNaming.For` is the rule, shared with ingest rather than re-implemented. It
is keyed on an opaque id — a `SourceFile` when ingest places a file a release
shipped, a `MediaStream` when a track is extracted — because the rule has no reason
to know which, and two copies would eventually disagree on the slug.

**The cohort includes the sidecars already on disk.** The slug exists to tell apart
companions of the same kind and language, and extracting a second Russian dub next
to an existing `Movie.rus.mka` is exactly that case — but the existing file is not
in the batch being named. Left out, `Unique` would quietly produce `Movie.rus.2.mka`
while the track's own group name sat unused, which is the failure the slug rule was
written to prevent. Nothing already on disk is renamed, so an existing lone track
keeps the plain form and only the newcomer carries a slug — asymmetric, and the only
option that does not rewrite files a client may already be reading.

**Names already taken on disk are reserved too**, not just the ones with rows. A
sidecar's entry can be dropped while its file is kept, and an operator can copy a
subtitle in by hand; either leaves a file the database knows nothing about. The
engine writes with ffmpeg's `-y`, which is right for a conversion (the operator
named that exact path) and wrong here, where the name is generated — it would
silently replace a retained or hand-edited subtitle. A reserved name says nothing
about crowding, because an unknown file has no language to compare: it only takes
its own name out of circulation.

Tracks are ordered by their position in the container, not by the order a client
listed them, so the naming rule's position fallback is stable.

**Index assignment goes through the same rule as ingest** — `ExternalStreamIndex`
starts at 1000 and continues past whatever the source already carries, so nothing
reuses an index a client may be selecting on. That rule is shared for the same
reason the naming is: the failure of two copies disagreeing would be a client
silently playing the wrong track.

## The job

Extraction runs in the [`transcode-engine`](https://github.com/alex-de-haas/transcode-engine/blob/main/docs/features/extract-jobs/feature.md)
app, which grew an `outputs` list for it: one ffmpeg invocation writes every
selected track, so a nineteen-dub remux is read once rather than nineteen times.
The `api` image ships without `ffmpeg` and keeps it that way.

`TranscodeJob.Kind` tells the two shapes apart, and `TranscodeJobOutput` holds each
planned file. The names are fixed at submit time because the engine writes them, and
the language and title are carried rather than re-read: a `.srt` has nowhere to hold
them, so re-reading one would unlabel every extracted subtitle.

`TranscodeCoordinator` branches on the kind when a job completes. A conversion's
single output becomes a new version; an extraction's files become external streams
of the source it read, probed for their codec, channels and sample rate so a row
reads like any other track. Importing is idempotent on `(source, path)`, because a
completion is observed twice — the engine event and the reconcile tick, or across a
restart.

**Promotions run one at a time.** The per-job dedup above keeps one job from being
promoted twice but says nothing about two *different* jobs, and an extraction picks
its external indexes by reading the rows already on the source. Two completions
racing on one source would both read the same rows, both allocate the same index,
and leave two external tracks a client cannot tell apart — there is no unique
constraint on `(MediaSourceId, Index)` to catch it. A single gate in the
coordinator serializes them; a promotion is a probe and a few inserts, so a lock
per source would need its own lifetime management to buy a concurrency nobody is
waiting on.

**A completed job missing an output fails while importing the rest**, naming what is
missing. Leaving a produced file with no row pointing at it is the one outcome the
sidecar model exists to prevent.

## In the Media tab

A version's action row carries an **Extract** control beside Convert, and it opens a
dialog rather than submitting on the click — the reason the Convert dialog exists:
gigabytes start moving, and an operator should see what the result will be. It also
gives the audio caveat somewhere to sit.

The dialog lists the container's own audio and subtitle tracks with the file each
becomes. Sidecars are not offered: one is already a file, so there is nothing to
extract it from. The whole surface follows the engine's availability, since it has
nothing to talk to without it, and a job refused because the item is mid-move comes
back as a 409 through the same `LibraryMoveGuard` a conversion uses.

The client-side bitmap check names only codecs known to be pictures; an unrecognised
one stays selectable on purpose. The server owns the rule, and a client stricter than
the API blocks a submit the API would have accepted — the one direction such a check
must never be wrong in. A codec the dialog does not recognise says so instead of
naming a file: guessing `.srt` would promise an outcome the server refuses.

An extraction appears in the same conversion list as a conversion, reading
`Extract · N files` where a conversion states its codec and quality: it encodes
nothing, so those columns would only ever say "Remux".

## Over the API

An extracted subtitle is visible in `MediaSources[].MediaStreams` but not fetchable,
which is the gap every sidecar subtitle has — parked in
[external-subtitle-delivery](../external-subtitle-delivery/plan.md). Clients reading
the folder directly (SMB/NFS) do get it. This feature does not narrow that gap.

## Testing Expectations

- `TrackExtractionTests` — the whole path against a real database and a recording
  engine: audio always becoming Matroska with its tags on the output entry; each text
  subtitle codec mapping to its file and `mov_text` to the one conversion; a
  picture-based subtitle, an unknown codec and video refused; a sidecar, a stream of
  another version, and an empty selection refused; tracks ordered by container
  position; a track extracted beside an existing sidecar of its cohort taking its
  title as a slug while a lone one keeps the plain form; the job stored as an
  extraction that composes nothing and sends no picture settings; a second job for a
  file one is already writing refused; a track already out refused while its file is
  recorded — including after its job is dismissed and after a partly-failed import —
  and extractable again once removed; a file on disk with no row never written over,
  and the video itself never taken as a name; a produced file becoming an external
  row with the specs from its probe, the label from the job, and an index past 1000
  and past the ones already there; a double import recording it once; and a missing
  output failing the job while the rest is still recorded.
- `SidecarNamingTests` — the widened cohort: a track arriving beside one of its own
  cohort told apart by its title, a name already taken never handed out again, an
  existing sidecar of another cohort not crowding, one crowding every newcomer in its
  own, and a reserved name taken out of circulation without affecting crowding.
- `RemoteTranscodeEngineWireTests` — the outputs travelling under the names the
  engine binds (`path`, not `relativePath`), an extraction sending a null
  `outputPath`, a text conversion naming its codec, and an ordinary job sending no
  outputs.
- `detail.spec.ts` — the Extract control opening the dialog, the audio caveat on its
  heading, the file each track becomes, a picture-based subtitle unselectable with
  its reason, sidecars not offered, and the submitted payload.
