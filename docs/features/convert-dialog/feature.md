# Convert Dialog

Created: 2026-07-29
Updated: 2026-07-29

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

- **Video** — re-encode (codec, downscale target, encoder, CRF for software) or keep
  the original picture untouched, which is lossless and the only HDR-safe answer.
  A Dolby Vision or HDR10+ source says so, in the dialog, before it is re-encoded.
- **The container's own tracks** — each audio and subtitle track kept or dropped,
  and one of each marked the default a player starts on.
- **The files beside it** — each sidecar dub or subtitle folded into the output as
  an extra track. The files themselves stay on disk; see
  [external-track-sidecars](../external-track-sidecars/feature.md).
- **Each track's name and language**, written into the output as it is produced.
  See [stream-title-editing](../stream-title-editing/feature.md).

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
  appending "Merged" after the encode label, and staying plain "Merged" for a copy.
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
