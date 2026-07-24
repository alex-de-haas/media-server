# Multi-Movie Ingest (Franchise Packs)

Created: 2026-07-24
Updated: 2026-07-24

## Description

A single download that contains several movies — a franchise pack like
"Die Hard 1–4" — imports as the separate movies it is. Each video file gets its
own `MediaItem`, its own canonical folder, its own metadata, and (through the
enrich-time collection sync) its own place in the TMDb collection, so the pack
lands in the library indistinguishable from four separate grabs.

Nothing about the pipeline is movie-pack-specific: identify has always been
per-file. What this feature adds is the operator surface for the cases the
per-file path can't resolve alone, plus the identification fixes that keep most
packs off that surface entirely.

Series packs are unaffected: an episodic torrent never mixes shows, so those
batches still resolve against one series with per-file season/episode.

## Automatic Path

For a pack whose files carry recognizable names, no operator action is needed:

- Identify parses and searches **each playable video independently**
  (`IdentifyService.IdentifyAsync`), so `Die Hard 2 (1990).mkv` and
  `Die Hard (1988).mkv` resolve to two different movies in one batch.
- Organize groups the batch's files by assigned media item, giving each movie
  its own folder; Probe creates one `MediaSource` per file under its own item;
  Enrich and Publish walk the whole ingest graph rather than a single item.
- `CollectionSyncService` links each enriched movie to its
  `belongs_to_collection` franchise, so the pack's films group on the
  Collections page (see [collections](../collections.md)) without extra work.

Two identification behaviors carry most of this path for non-English packs —
ordinal-prefix stripping and script-matched provider search. They belong to the
metadata feature and are documented there: [metadata](../metadata/feature.md).

## Review: One Identity per Group

When files can't be auto-matched the batch parks at `NeedsReview`, and the
review dialog resolves it. For **movie catalogs** the dialog is organized around
identity groups rather than one batch-wide identity:

- Pending files are **pre-grouped by parsed title + year** — a franchise pack
  parses into distinct titles, so each movie starts as its own group. An
  operator-pinned identity collapses the batch to a single group instead (the
  pin is a declaration that the batch is one movie).
- Each group carries its own corrected-title/year search and its own candidate
  list; picking a candidate applies to that group's files only.
- A per-file **Group** select moves a file to another group, or splits it into a
  new one, when the automatic grouping guessed wrong.
- External audio tracks join the video group whose title prefixes theirs
  (`Movie One Rus.mka` → the "Movie One" group), or the sole video group when
  there is only one. The merge preview keys on the group's identity, so a track
  and its video render as one merge box.
- **Skip stays per file**, unchanged.
- One Approve sends **all groups in a single match request**, so the pipeline
  re-drives once and the batch resolves atomically.

Series and anime catalogs keep the previous single-identity dialog: one series
picked at the top, per-file season/episode below.

## Match API

`POST /api/ingest/{id}/match` accepts either shape:

```jsonc
// Legacy: one identity for every file in the request.
{ "kind": "Movie", "provider": "tmdb", "providerId": "562", "title": "Die Hard",
  "year": 1988, "files": [{ "sourceFileId": "…" }] }

// Grouped: one identity per group, resolved in one transaction.
{ "groups": [
  { "kind": "Movie", "provider": "tmdb", "providerId": "562", "title": "Die Hard",
    "year": 1988, "files": [{ "sourceFileId": "…" }] },
  { "kind": "Movie", "provider": "tmdb", "providerId": "1573", "title": "Die Hard 2",
    "year": 1990, "files": [{ "sourceFileId": "…" }] }
] }
```

`MatchRequest.ToGroups()` normalizes the legacy shape to a single group, so
validation and resolution have one code path. Rules:

- Every group needs at least one file and an identity (`provider`, `providerId`,
  `title`); `kind` must be `Movie` or `Episode`.
- A source file may appear in **only one group** — two identities claiming one
  file has no honest resolution order, so the request is rejected (400) and the
  item stays parked.
- Groups pinning the same provider identity resolve to **one** `MediaItem`: the
  resolver reads the store, which cannot see an unflushed sibling from an
  earlier group in the same request.
- All groups are applied in one save with one re-drive, so partial matching
  never races the orchestrator.

The endpoint still refuses any match once the `identify` stage has completed —
by then Organize may have moved files, and re-pointing an assignment is a
library remap.

## Operator Surfaces

- **Pin dialog.** Pinning applies to every video in the batch, so a movie-catalog
  item with more than one video file shows a warning: pinning one movie imports
  all of them as versions of it, and a pack of different films should be left
  unpinned (auto-identify, or per-group matching in review). Series batches don't
  warn — pinning the show there is exactly right.
- **Activity row.** A batch that mapped several distinct movies reads
  `Die Hard 2 (+1 more)` instead of naming only its primary item. Counted over
  movie assignments only, so a season pack still reads as its one series.

## Testing Expectations

Backend (xUnit, Imposter for provider mocks), in
`IngestGroupedMatchTests`:

- A grouped match over a parked pack publishes **separate movies** with distinct
  library paths, each file mapped to its own group's item.
- An audio file inside a group is assigned that group's movie and muxes into
  that group's video only.
- A source file repeated across groups is rejected and the item stays
  `NeedsReview` (no re-drive).
- Two groups sharing one provider identity produce a single media item carrying
  both files as versions.

The legacy single-identity match paths (movie batch, series pack) keep their
existing coverage in `IngestPipelineTests`.

Parser and scoring coverage for the identification fixes lives with the metadata
feature: [metadata](../metadata/feature.md).
