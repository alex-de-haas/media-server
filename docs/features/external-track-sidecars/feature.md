# External Track Sidecars

Created: 2026-07-27
Updated: 2026-07-29

A release's separate audio tracks and subtitles are kept as files beside the
library file they belong to, and recorded as external streams of its media
source. Ingest never merges them in; merging is a separate operation, run later
and only when asked.

Before this, ingest muxed those tracks into the video during staging. Every other
outcome was lossy: a failed mux, an absent transcode engine, or a batch whose
videos were not present destroyed the track. Nothing destroys one now.

It is also why this app no longer ships `ffmpeg` — with merging delegated and
probing already delegated or read from headers, no binary is left to run.

## What ingest does

`SidecarStage` runs after Probe: the video has been organized by then, so its
canonical name is known, and its `MediaSource` exists for the rows to attach to.
Every mapped companion is moved next to it under a canonical name and recorded as
an external `MediaStream`.

Both audio and subtitle companions are admitted from a torrent's file list in the
first place: one that never became a `SourceFile` could not reach this stage, and
would be swept as an untracked staging leftover.

Identify treats both as companions too — matched against the batch's videos rather
than searched for as content of their own. A subtitle is a subtitle *of* a film,
not a film named `Форсированные.srt`; identified as content it would invent an item
and make a one-movie batch look like several, which parks the dubs beside it.
The review surface says the same thing: a companion row offers no "Extra"
decision, and states which video the track lands beside — or, when no video here
claims it, that it stays where it downloaded rather than being discarded.

Companions are never organized by `OrganizerService` — their names derive from the
video's — so its recursive staging sweep deliberately spares any root still
holding one. Without that, the sweep would take the only copy of a dub with it.
The emptied folders are cleared once the files are out.

A release whose companions have no video in the batch — a dub-only torrent, or
specials whose episodes ship elsewhere — keeps them where they are. That case used
to flip the tracks to `Skipped` and sweep them.

## Naming

Three layers, because no single one survives every case:

1. **The database is the source of truth** — `MediaStream.Language` / `Title`.
2. **The container repeats it where the format has tags.** A `.mka` carries its own
   language and title, which is why the file name never has to be authoritative;
   `.ac3`, `.eac3`, `.dts` and `.aac` are elementary streams with nowhere to put
   one, and `AudioTrackLabeler` reads their path instead.
3. **The file name is a label**: `<video>.<language>[.<slug>].<ext>`.

**A slug is added only when it disambiguates** — when the item has more than one
companion of the same kind and language. One Russian subtitle therefore lands as
`<video>.rus.srt`, exactly the pattern clients match on, while three Russian dubs
each get their group name. The conventional form is the default; the slug is the
exception, rather than every file paying for a rare collision.

Audio and subtitles do not crowd each other: a lone dub and a lone subtitle both
keep the plain form.

### Where the label comes from

A title has to **distinguish** the tracks, and only the siblings reveal what does —
so an item's companions are labelled as a set rather than one at a time. Releases
label in two opposite ways, and a single path cannot tell them apart because in
isolation both look like a label:

| release | what varies | what repeats |
| --- | --- | --- |
| `RUS Sound/[AniDUB]/Movie.mka`, `RUS Sound/[MCA]/Movie.mka` | the folder | the file name |
| `Володарский.ac3`, `Гаврилов.ac3`, `Сербин.dts` beside the film | the file name | the folder |

Whichever component varies across the set is the one carrying the labels. Getting
this backwards is not a cosmetic error: it gives every track the release name and
no way to tell them apart at all.

The set is a **cohort** — one kind, one language — because that is the group a
title has to tell apart, and the same group the slug rule below adds a slug for.
Reading the question across everything at once lets an unrelated companion answer
it: one subtitle named unlike a release's dubs is enough to make file names look
like the varying component, and every dub then falls back to the same label.

Within a name, the label is what it carries beyond the video's own name
(`Movie.rus.AniDUB.mka` — also the shape this app writes, so its output reads back
on a later scan), or the whole name when it shares nothing with the video's.
Language and flag tokens drop out, as does anything that merely restates the
release or names a bucket — a folder called `RUS Subs`, a file called `dub.ac3`.
The comparison against the video is on word tokens, because a staging folder and
the organized file differ in punctuation far more often than in words.

Slugs are sanitized, because real titles are not file names — one release in the
development library labels a track `DUB | DD5.1 @ 640 kbps`, and `|` is invalid on
Windows, exFAT and SMB. The forbidden set is fixed rather than taken from
`Path.GetInvalidFileNameChars()`: that answers for the *runtime*, and on the Linux
container that is only `/` and NUL — while the library it writes into may be exFAT
or SMB, or be opened from Windows later. Names are capped at 255 **bytes** (Cyrillic costs two per
character), collisions that survive sanitizing take a numeric suffix, and a
crowded track with no title at all falls back to its position.

## Merging

Merging submits a job to the transcode engine with the sidecars as additional
inputs, and produces a new version alongside them. It reuses the transcode job
machinery: same queue, same progress, same import of the result as a version.

**It is composed in the [Convert dialog](../convert-dialog/feature.md), not started
from here.** The Media tab's Merge button opens that dialog with the checked
sidecars already selected; more can be added there, each track's name and language
corrected, and the video re-encoded in the same pass if wanted. Merging used to
submit a job on the click, with no way to see what the result would carry or fix a
mislabelled dub before gigabytes started moving.

A merge no longer implies a stream copy — it says what joins the output, not what
happens to the picture. It keeps only the *default*: with no codec named the video
is copied and the output is `<video> - Merged.mkv`, which is what every merge has
produced so far.

**A merge keeps its sidecars.** The files stay on disk untouched — not removed and
not rewritten. Normalizing an untagged `.ac3` into a tagged `.mka` was considered
and rejected: after a merge the sidecar is an archive, its metadata already lives
in the database and in its name, and rewriting every one of them would be a full
extra pass over gigabytes to produce a third copy of data that is not lost — and
would turn the exact bytes the release shipped into a derivative.

Re-probing a video spares them for the same reason: probing says nothing about
files sitting beside it, so `RefreshMediaAsync` replaces only the embedded streams
and leaves external rows alone. Without that, refreshing — or the media backfill,
which runs on exactly the items most likely to have sidecars — would delete entries
whose files are still on disk.

Which is also why the **media backfill probes sidecars separately**, by their own
paths: the refresh pass it is built on can never reach them. It fills in the codec,
channels and sample rate of rows that lack them — those placed before the specs were
recorded, and any whose file the engine could not answer for at the time. A missing
codec is the marker for "never answered", so a file that still cannot be read is
picked up again on the next run instead of being recorded as having no codec.

It writes **only those three fields**. Language and title are a labelling decision
made across a whole cohort of files, weighing what each container tagged against
what the paths reveal; re-reading one file's tags in isolation would overwrite that
with strictly less information.

Removing one is therefore a deliberate act: its own operation, presented in the
Media tab the way deleting an unwanted version is, with the same explicit choice
between dropping the entry and erasing the file. It is its own operation and not a
call into `DeleteSourceAsync` because a sidecar is a `MediaStream` on a source, not
a `MediaSource` — there is no version to drop.

**Deleting what a sidecar hangs off takes it along.** Removing a movie or a single
version with "delete files" erases the sidecars beside that video too, and their
staged rows go back to unassigned. A sidecar is a file of its own but not an item
of its own: it exists only as an external stream of a source, so once that source
is gone nothing in the library refers to it. Leaving them behind stranded a dub
next to a deleted film — in a folder the eraser could then not prune either, since
it was no longer empty. This is the one case where the files go without being
named individually; every other removal is the deliberate per-track act above.

## The limitation, stated plainly

**A sidecar audio track is preserved but not playable.** Infuse does not support
external audio tracks by any route — they must be inside the container
([Firecore community](https://community.firecore.com/t/support-for-external-audio-tracks/15848/10))
— and Jellyfin has no external-audio delivery either, so neither the API path nor
direct file access over SMB/NFS helps. For audio, **merging is what makes a track
usable**, not an optional nicety, and the Media tab says so.

**Subtitles are the opposite.** Clients read sidecar subtitles from disk, which is
why their names follow the convention those clients match on. Serving them over
the Jellyfin API additionally needs a delivery endpoint this app does not have; it
is parked in [external-subtitle-delivery](../external-subtitle-delivery/plan.md).

## In the Media tab

Sidecars are listed apart from the container's own tracks, because they behave
differently: each is a file that can be removed on its own, and an external audio
track is inert until merged. Selecting some opens the Convert dialog on them; each
row offers a removal. The merge control follows the transcode engine's availability,
since it has nothing to talk to without it.

They are then **grouped by kind** — audio under an audio heading, subtitles under
theirs, each with an icon — and every row carries **its own file name** under the
label. Neither is decoration. A sidecar routinely has no language and no codec to
show: an `.ac3` dub named only after its voice-over group leaves the row with
nothing that says whether it is a dub or a subtitle, and four of them read as one
undifferentiated list. The kind answers that, the file name is what an operator
sees when they open the folder, and the extension answers it a second time. For
the same reason the label leads the row when there is nothing else to lead it —
`Гаврилов`, not `— "Гаврилов"`.

The icons are the **same marks the container's own tracks carry** in the list
above — video, audio, subtitles — so the two lists read as one vocabulary rather
than as a labelled list above an iconned one.

A sidecar also carries **its codec, channel count and sample rate**, so it reads
like any other track — `rus AC3 5.1 · 48 kHz` with its release name in quotes —
rather than being the one kind of track in the library with nothing but a name.
Nothing in the UI was added for this: the same `DisplayTitle` and specs that render
an embedded track render a sidecar once the fields are there.

They come from the probe the sidecar stage **already runs** to read the file's tags,
which used to discard everything else it returned. An elementary stream read without
the engine still answers nothing — a container-header parser has nothing to parse in
a raw `.ac3` — so those fields stay null until an engine is attached and the backfill
is run from **Media data** on Settings.

The "only plays once merged" caveat sits on the audio heading rather than under
the whole list, because it is true of audio and false of subtitles.

## Testing Expectations

- `SidecarNamingTests` — the naming rule: a lone track keeping the plain form even
  when it has a title, several in one language told apart by theirs, audio and
  subtitles not crowding each other, a title no filesystem accepts made safe —
  including characters a Linux runtime would have let through,
  titles that sanitize to the same thing still made distinct, a crowded untitled
  track falling back to its position, the byte cap with Cyrillic, and a sidecar
  never taking the video's own name.
- `IngestSidecarTests` — through the real pipeline: tracks matching their episodes
  and landing beside them under the plain form, three dubs of one movie told apart
  by their group folders, a tagged container naming its own track (including one
  whose title cannot be a file name), the emptied staging folder being cleared once
  the files are out, sidecar indexes staying unique across drives and past the
  container's own numbering, a subtitle in the batch neither creating a movie of
  its own nor deciding how the dubs beside it are labelled, a placed sidecar
  carrying the codec, channels and sample rate the same probe reported, and a
  dub-only batch keeping its tracks instead of discarding them.
- `SidecarDeletionTests` — dropping the entry leaving the file, erasing taking it,
  the video and its own streams untouched, the staged row going back to unassigned,
  an identically-placed sidecar of another catalog left alone, an embedded stream
  not being deletable this way, and an unknown id reported. Plus the deletes one
  level up: a movie or a version removed with its files taking the sidecars beside
  it and pruning the emptied folder, the same delete without the flag leaving every
  file where it is, and a version's sidecar rows returning to unassigned.
- `LibraryMaintenanceServiceTests` — re-probing a video replacing its embedded
  streams while sparing the sidecar rows beside it; the backfill filling in a
  sidecar's missing specs, leaving its language and title alone (its probe answers
  a different language on purpose), and leaving a file it cannot read for the next
  run rather than marking it done.
- `DownloadFileServiceTests` — a torrent's dubs *and* subtitles admitted as source
  files, junk still refused.
- `AudioTrackLabelerTests` — the language and title inference all three layouts
  rely on, including which component the set reveals as the label, a name or folder
  that only repeats the release yielding nothing, and reading this feature's own
  output back on a later scan.
