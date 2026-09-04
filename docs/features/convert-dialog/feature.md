# Convert Dialog

Created: 2026-07-29
Updated: 2026-09-04

The one place a new version of a movie is composed. It submits a single job to the
transcode engine, and everything that job can carry is decided here: what happens
to the video, which of the container's tracks survive, which files beside it are
folded in, and what each resulting track is called and tagged as.

Reached from a version's **Convert** control, or from **Merge** on the sidecar list
below it — the same dialog either way, the second arriving with those files already
checked. Merging used to submit a job the moment it was clicked, which meant no way
to see what the result would carry, or to fix a mislabelled dub, before gigabytes
started moving.

That hand-off **consumes** the tab's selection: its checkboxes clear as the dialog
opens. Keeping them would leave two places claiming to hold the answer, and the stale
one wins the next time the button is pressed — after the dialog's own selection has
moved on.

The whole surface follows the engine's availability, since it has nothing to talk to
without it. The Media tab itself does not: listing versions and picking which one
plays is database-side and works with no engine attached.

## What it composes

- **Video** — re-encode (codec, downscale target, encoder, quality level) or keep
  the original picture untouched, which is lossless and the only HDR-safe answer.
  A Dolby Vision or HDR10+ source says so, in the dialog, before it is re-encoded.
- **The container's own tracks** — each audio and subtitle track kept or dropped,
  one of each marked the default a player starts on, and each kept audio track
  copied or re-encoded.
- **The files beside it** — each sidecar dub or subtitle folded into the output as
  an extra track. The files themselves stay on disk; see
  [external-track-sidecars](../external-track-sidecars/feature.md).
- **Each track's name and language**, written into the output as it is produced.
  See [stream-title-editing](../stream-title-editing/feature.md).

## Quality is a level, not a CRF

The picture setting is one of four levels — `highest`, `high` (the default),
`balanced`, `small` — and it is shown for **every** encoder. It used to be a CRF box
that appeared only when the encoder was set to Software, which meant a job running on
a GPU had no way to ask for a smaller file at all.

A level is not a CRF because the engine's encoder choice is opportunistic: a job can
land on `hevc_amf` or on `libx265` depending on the host. The engine holds the
mapping and translates the level for whichever encoder runs, so the same level means
the same picture either way — see
[transcode-engine / compression controls](https://github.com/alex-de-haas/transcode-engine/blob/main/docs/features/compression-controls/feature.md).

Beside the level, the dialog states **what it is expected to cost**: "About 2.5 GB
of video, from 8.3 GB." A level is a quality target, and "Balanced" tells an operator
weighing a day of CPU nothing about whether it is worth spending. The estimate is the
source's own video bitrate times the share that level came out at on the file the
engine's mapping was measured on — an anchor, not a prediction, and the dialog says
so: a source that is already an efficient encode has far less left to give up. A
downscale is named rather than modelled ("or less at 1080p"), because the same level
at fewer pixels lands under the figure and by how much is content's business.

The level rides into the **version label** only when it is not the default:
`- HEVC 1080p Merged` is unchanged for an ordinary job, and a non-default one reads
`- HEVC 1080p Small Merged`. The label is the whole of what separates one output path
from another and a second job producing an existing path is refused, so two jobs
differing only by quality must not collide — but the common case should not grow a
word that never varies.

Re-encoded audio enters the label the same way, as the codec it targets: `- Remux
EAC3`, or `- EAC3 Merged` for a merged copy. It has to, and on a video copy it is the
*only* thing that changes — without it "shrink the dubs, keep every frame of picture"
would land on the path a plain remux already holds and be refused as a duplicate,
which is precisely the cheap conversion this feature exists to make reachable. Copied
audio stays silent, so nothing already on disk is renamed.

## Audio is re-encoded per track

Each kept audio track carries a **Re-encode** toggle: off it is copied byte for byte,
on it becomes E-AC-3 at a bitrate picked from its channel count (640 kbps above
stereo, 256 for stereo, 128 for mono).

Per track rather than per job, because one file's tracks want opposite answers. A UHD
remux can carry nineteen voice-over dubs stored as lossless DTS-HD MA 7.1 — 4.1 GB
each, 55% of a 141 GB file — beside an original TrueHD Atmos track that must not be
touched.

A re-encoded row states the trade in full — `3.9 GB → E-AC-3, 8 channels → 5.1,
640 kbps · about 618 MB`. Both sizes are a bitrate times the duration, so they
compare like for like. Two things it is careful about:

- **The downmix is on the row, not in a footnote.** E-AC-3 stops at 5.1, so a 7.1
  source loses its height channels — a real loss, and invisible in the output.
- **A track with no recorded bitrate has no "before".** The row drops the comparison
  and leads with the result, which is still exact. It does not fall back on a share
  of the file's overall rate.

## An object-based track says so before it is touched

A track carrying **Atmos** or **DTS:X** is marked on its row, and a re-encode of one
leads with what it costs — `drops Atmos · 2.3 GB → E-AC-3, …`.

It earns the marker because nothing else on the row carries it. The codec reads
`truehd` whether or not there are objects, so the summary beside it — `eng TrueHD
7.1` — is identical either way; the fact lives only in the probe's codec profile.
And it is the one audio fact that settles what may be done to a track: ffmpeg encodes
neither JOC nor DTS:X, so there is no bitrate at which a re-encode keeps them.
Copying is the only way.

The marker is absent, not negative, on a source probed from container headers alone —
a header carries no codec profile, so nobody looked. See
[media-probe-providers](../media-probe-providers/feature.md).

Re-encoding audio says nothing about the picture, so the cheapest useful conversion —
shrink the dubs, copy every frame of video — is one job with the video left on
"keep original".

## The dialog leads with the cheap lever

When a source's audio outweighs its video, a line above the video controls says so:
"Audio is the larger half of this file: 15 GB across 4 tracks, against 11 GB of
video." It sits there, before anything about the picture, because the dialog opens on
"re-encode" — the expensive, lossy, day-long option — and on a file like this the
cheap one is worth more. A 141.7 GB 4K remux in the development library is 54.1 GB of
video and 87.5 GB of audio; track selection alone takes it to ~61 GB without touching
a frame of picture.

Only tracks that state a bitrate are counted. Leaving the silent ones out can only
understate the audio side, which is the safe direction: the line claims audio is the
larger half, and it must never make that claim on a total it filled in itself.

## What the Dolby Vision warning does and does not mean

The warning above the video controls says a re-encode drops the Dolby Vision layer.
It does **not** mean the result is broken. What survives is the base layer, and on the
sources this library mostly holds — disc remuxes, which are profile 7 — that base
layer is ordinary HEVC Main 10 PQ. The picture stays a valid HDR10 one, its
mastering-display and MaxCLL metadata come through the encode, and what is lost is the
per-frame dynamic metadata, nothing else.

That reassurance is not universal, because "Dolby Vision" covers several base layers:
profile 7 and 8.1 are HDR10-based, 8.4 is HLG-based, 8.2 is SDR-based, and profile 5
has no viewable base layer at all. The library records the profile now
([dolby-vision-profile](../dolby-vision-profile/feature.md)), so the warning says which:
a profile 5 re-encode wrecks the colours, an 8.4 lands on HLG, an 8.2 on SDR, and 7
and 8.1 keep an HDR10 picture. A source whose profile is not yet recorded gets the
generic warning it always did. The profile table lives in
[transcode-engine / compression controls](https://github.com/alex-de-haas/transcode-engine/blob/main/docs/features/compression-controls/feature.md#dolby-vision-does-not-survive-a-re-encode).

So on a Dolby Vision source the choice is not "shrink it or keep it watchable", it is
"keep the dynamic layer or spend a day of CPU". Preserving Dolby Vision through a
re-encode is possible but the engine does not do it, and deliberately: it would force
the software encoder, which measured **equivalent to hardware in quality per byte** —
the same output size for 30–70× the time, buying only the metadata. Dropping a
profile 7 source's enhancement layer losslessly was measured too and comes to 1.6% of
the file. Both findings live in
[transcode-engine / compression controls](https://github.com/alex-de-haas/transcode-engine/blob/main/docs/features/compression-controls/feature.md#dolby-vision-does-not-survive-a-re-encode).

## Converting a disc's Dolby Vision into one Apple hardware plays

A profile 7 source has a third choice beside "drop the layer" and "spend a day of
CPU". With the video kept, the dialog offers **Convert Dolby Vision to profile 8.1
(single layer)**: the picture is copied byte for byte, the RPU metadata is rewritten to
profile 8.1 — the form Apple TV and Infuse play as Dolby Vision — and the enhancement
layer, the 1.6 % measured above, is dropped. The checkbox appears only on a profile 7
source, only under "Keep original video", and only when the engine advertises the
tools for it (`GET /api/transcode/availability` answers `dolbyVisionConversion`), since
an engine without them refuses the job rather than copying silently. Its copy says what
happens and what is lost.

The request carries `dolbyVision: toProfile81`. `TranscodeService.ResolveDolbyVision`
mirrors the engine's rules so a contradictory request fails here, in this app's
vocabulary: a re-encode is refused (a re-encode drops Dolby Vision whatever is asked),
so is a version whose picture is profile 8 or 5, one that is not Dolby Vision at all,
and one whose profile is not yet recorded — that one is sent to the catalog's media
refresh rather than to an engine that would refuse it three stages in. The picture is
judged by the rule every other surface uses, so a cover a muxer wrote as a video track
is passed over.

The version label carries `DV 8.1` after the audio codec and before `Merged` — `Remux
DV 8.1`, `Remux EAC3 DV 8.1`, `DV 8.1 Merged` — because a rewritten Dolby Vision is a
different file from a plain copy of the same source and the two must not land on one
path. The imported version is named the same way from the job rather than from the
output's record, which says profile 8 for a plain copy of a profile 8 source too. The
job card reads `Remux DV 8.1`. The engine's four stages and their honesty checks are its
own ([dolby-vision-conversion](https://github.com/alex-de-haas/transcode-engine/blob/main/docs/features/dolby-vision-conversion/feature.md)).

The order that actually pays on such a file is the one this dialog already leads
with: audio first — 87.5 GB of that 141.7 GB remux — then the picture if it is still
too large.

**Profile 5 is where the warning must be obeyed rather than weighed**: re-encoding one
produces wrong colours, not merely flatter ones. Since the dialog cannot tell it
apart, the safe rule is the source's provenance — profile 5 arrives from streaming
rips, not from the disc remuxes this library is built on.

## Every size comes from a recorded bitrate, or is absent

The estimate beside the quality level, the audio/video split, and a re-encoded row's
"before" all rest on `MediaStream.Bitrate`, which the engine probe reads per track —
from `ffprobe`'s `bit_rate`, or from the `BPS` tag `mkvmerge` writes, which is what
the remuxes this feature exists for actually carry. See
[media-probe-providers](../media-probe-providers/feature.md).

A source with no per-track bitrates shows **none of the three**. The overall rate is
known and could be divided across the streams, and that is exactly what is not done:
an operator cannot tell a derived number from a measured one, and these numbers are
the basis for deciding whether to spend a day of CPU. A library filled in before the
column existed answers them after "Refresh media data" re-probes the item.

## Merging and re-encoding are one job

A merge says what joins the output, not what happens to the picture. Shrinking a
9.5 GB remux while folding its three dubs in is one pass over the file; as two jobs
it is two passes, and the second re-encodes what the first has just written.

What a merge keeps is the **default**: with no codec named the video is copied,
where a plain conversion would encode. Re-encoding is the expensive and lossy
direction, and it must never be what omission buys — so opening the dialog through
Merge lands on "keep the original video". The operator asked for more tracks, not
for a re-encode.

The engine enforces the same rule; `TranscodeService.ResolveCodec` mirrors it so a
contradictory request (a downscale with nothing to encode) fails here with a message
in this app's own vocabulary, rather than coming back as a rejected job.

The output's version label carries both — `- HEVC 1080p Merged` — because that label
is the whole of what separates one output path from another, and a second job
producing a path that already exists is refused. Folding the encode into "Merged"
would make "merge these dubs" and "merge these dubs into a 1080p HEVC" collide.

## Track selection travels explicitly when merging

Ordinarily a selection is only sent when it is a subset, or when the default moved;
otherwise the engine is left to copy every track of the type, which is the more
robust path. **A merge always sends the explicit lists.** An appended track's output
position is only knowable when the primary list is explicit — a "copy every stream
of this type" mapping expands to however many the file holds — so the engine refuses
to relabel an appended track without one. Sending the full list costs nothing: it
maps to exactly the same streams, and it keeps a merged dub's name editable.

## Languages are validated, names are not

A name is free text; nothing can be wrong with it. A language is a code with a
meaning, and it is the one value here typed rather than read out of a file:

- it is normalized onto the library's vocabulary — the ISO 639-1 pair, the
  terminological spelling and a BCP-47 region subtag all fold onto the bibliographic
  form (`de`, `deu` and `ger` cannot become three spellings of German);
- a tag the library does not know is **refused**. A track tagged `rsu` is one no
  "play my language" control will ever find, the value is written into the output
  permanently, and re-encoding gigabytes is an expensive way to discover a typo.

`LanguageTags` holds the vocabulary and `GET /api/transcode/languages` serves it, so
the dialog flags a bad tag as it is typed and disables the submit rather than letting
the whole form be filled in and rejected. It is served rather than duplicated in the
web bundle because two copies drift, and the half that drifts is the one that lets an
operator submit what the service then refuses.

What it serves is the **accepted** set, not the stored one — the canonical forms plus
the terminological spellings and 639-1 pairs that fold onto them. A client filtering
by the stored forms alone would refuse `ru`, `deu` and `pt-BR`, which this service
takes: stricter than the API is the one direction a client-side check must never be
wrong in, because it blocks a submit the server would have accepted. A region subtag
is not enumerable, so the dialog drops it before the membership test exactly as
`Normalize` does.

Two things a language field cannot do, both because the job carries *overrides* and
none of them removes a tag:

- **Clearing a language means keep, not erase.** An emptied field sends nothing, so
  the source's own tag survives. Sending `""` would fail the whole submit over a field
  the operator emptied rather than filled.
- **A dropped track's language stops mattering.** Typing a bad tag and then unchecking
  the track unblocks the submit — the edit is not built for a track that is not in the
  output, so there is nothing left to be wrong.

The same vocabulary is shared with the probe, but not the same decision: an
unrecognized tag *in a file* is kept, because it is what the file claims and dropping
it would unlabel a labelled track. Only typed input is refused.

## Testing Expectations

- `TranscodeServiceTests` — `ResolveCodec`: a merge re-encoding when it names a codec
  and copying when it does not, the encode-only knobs refused on a merge that names
  none and on an explicit copy but accepted once one is named; `VersionLabel`
  appending "Merged" after the encode label, and staying plain "Merged" for a copy;
  the quality level carried only when it is not the default; the audio codec carried
  only when tracks are re-encoded, across every copy/encode/merge combination, with
  repeated codecs collapsed and their order not changing the path; `DV 8.1` carried
  after the audio and before "Merged"; `ResolveDolbyVision` — keep and absent as the
  default, the engine's spelling accepted, an unknown word, a re-encode, profiles 8
  and 5 by name, an unrecorded profile told from no Dolby Vision, and the picture
  judged rather than the cover art.
- `RemoteTranscodeEngineWireTests` — the Dolby Vision mode travelling under the
  engine's name and null when kept; the tooling read from `GET /hardware`, including an
  engine from before the `tools` block answering none.
- Vitest (`format.test.ts`) — the re-encode warning per profile and the generic
  fallbacks.
- `StreamMetadataEditTests` — language normalization across the 639-1 pair, the
  terminological spelling, case, whitespace and a BCP-47 region; an unrecognized tag
  refused; a title-only edit leaving the language alone.
- `LanguageTagsTests` — the vocabulary itself: every accepted spelling folding onto
  the stored one, what is refused, the served set carrying the aliases as well as the
  canonical forms, and every entry in that set surviving `Normalize` — so the dialog
  cannot offer a value the submit then refuses.
- `detail.spec.ts` — Merge opening this dialog with those tracks checked instead of
  starting a job and clearing the tab's selection as it does, a second sidecar added
  here, and the submitted payload carrying both ids with the explicit track lists a
  merge needs; a language corrected in the dialog, a bad tag blocking the submit, and
  only the changed field travelling; every spelling the API accepts accepted here too,
  a dropped track unblocking the submit its bad tag was holding, and a cleared field
  sending no edit at all.
- `detail.spec.ts`, sizing — the quality estimate shown for the default level and
  following the level actually selected; a re-encoded row stating both its before and
  its after; the split line on a source whose dubs outweigh its picture, with the
  7.1 → 5.1 note on the row; and a source with no per-track bitrate showing no
  estimate and no split line while its row still states the resulting size.
