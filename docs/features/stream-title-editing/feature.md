# Stream Title Editing

Created: 2026-07-27
Updated: 2026-07-27

An operator can correct a track's name while submitting a conversion or a merge,
instead of living with whatever the release wrote. Real releases make the case:
`Mercy (2026).mkv` labels its English track with nothing at all, and
`Zootopia (2016).mkv` calls one `DUB | DD5.1 @ 640 kbps`.

## Where it is offered, and why only there

The Convert dialog lists each track with an editable name beside its keep/default
controls. Merging sidecars in offers the same, for the tracks coming from those
files as well as the video's own.

There is **no standalone rename**. Changing nothing but a title still rewrites the
whole file — a stream copy, but a full pass over a multi-gigabyte remux — so
editing is offered only where a job is being submitted anyway and the rewrite is
already happening. Matroska keeps track names in its header and could in principle
be edited in place, but that is a different mechanism needing its own endpoint on
the engine; nothing here waits on it.

## What is sent

Only what actually changed. A field the operator did not touch is not sent, so the
source stream's own tag survives and relabelling one track never silently freezes
the others' metadata.

Edits name a stream by id, and the service maps each onto the output stream that
will carry it:

- an **embedded** track is addressed within the primary input by its own absolute
  index;
- a **sidecar being merged** becomes an ffmpeg input of its own holding a single
  track, so it is index 0 of that input.

An edit naming a sidecar that is not part of this merge has no output stream to
write to and is **refused** rather than dropped — silently ignoring it would look
like the rename worked. So is an edit that sets neither a language nor a title.

## Nothing edits the database

The values travel with the job and are applied by the engine as output metadata.
When the job lands, `TranscodeOutputImporter` re-probes the result as it always
has, so the stored rows come back from the file rather than being asserted here.
Stream metadata stays read-only in this app: it is what a provider reported, not
something the operator types into a row.

## Testing Expectations

- `StreamMetadataEditTests` — the mapping onto output streams: an embedded track
  addressed within the video by its own index, a merged sidecar addressed as its
  own input at index 0, embedded and merged edits travelling together, and the
  refusals — a sidecar that is not being merged, an unknown track, and an edit that
  sets nothing.
