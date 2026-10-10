---
created: 2026-06-15
updated: 2026-10-10
summary: File operations happen only as catalog automation and stay confined to configured catalog roots.
---

# File and Directory Management

## Description

Media Server resolves and manipulates files only as part of catalog automation:
torrent intake, organizer moves, import scans, streaming, cleanup, and library
item deletion. Every file operation must stay constrained to configured catalog
roots and reject directory traversal. v1 is not a general filesystem browser or
manual file manager.

## Sandbox

All file access is sandboxed to configured catalog roots (see
[Catalogs](../catalogs/feature.md) and [Storage and data](../storage-and-data/feature.md)).

- Resolve and normalize every path, then verify it is contained within a
  configured root before any operation.
- Reject symlink escapes and `..` traversal.
- The UI cannot select arbitrary host directories. Under v1 `dev` runtime,
  catalog roots are configured host paths. Under future `docker` runtime, roots
  must come from Hosty-owned mounts.

## Supported Operations

- Resolve catalog-relative paths to absolute paths.
- Move a completed file from `.incoming/` into its canonical place at the root.
- Move (rename) the canonical file during remap.
- Move a published item's file(s) into **another catalog** (see Move Semantics).
- Clear a download's `.incoming/` staging folder once its file moves out, or when
  the in-flight download is removed.
- Delete a library item — a whole movie or series, one season, one episode — with
  optional file deletion, or remove one version while another remains.
- Import scan: enumerate the catalog root (excluding `.incoming/`) for orphan
  media files.
- Stream large files without whole-file buffering.

## Unsupported v1 Operations

- Arbitrary upload, copy, move, and rename from the UI.
- Recursive directory management as a standalone user workflow.
- Direct editing of raw torrent folders outside the organizer/remap flow.
- Extracting archived/multi-part releases (`.rar`/`.zip`); v1 organizes only plain
  video files.

## API Endpoints

Internal endpoints (under `/api`, behind Host identity):

```text
GET    /api/files/resolve?catalogId={id}&path=...   # internal/debug only
DELETE /api/library/{id}?deleteFiles={bool}         # removes the published item (and file if asked)
DELETE /api/library/episodes/{id}?deleteFiles={bool} # removes one episode → { seasonRemoved, seriesRemoved }
DELETE /api/library/seasons/{id}?deleteFiles={bool}  # removes one season  → { seasonRemoved, seriesRemoved }
DELETE /api/library/sources/{id}?deleteFile={bool}   # removes one version only if another remains
POST   /api/catalogs/{id}/scan                       # import scan of the catalog root
POST   /api/library/{id}/move                        # move the item into another catalog → { jobId }
```

Paths are expressed relative to a catalog root, never as absolute host paths.

## Removal Semantics

Whole-item removal and version removal have different scopes:

- **Remove from library** (`DELETE /api/library/{id}`): removes the published item
  and its sources; `deleteFiles=true` also deletes its files, while
  `deleteFiles=false` leaves them on disk for a later import scan to re-adopt.
  Accepts a published top-level movie or series; a series takes its seasons,
  episodes, and extras with it. By default, user history, ratings and favorites
  retain an unpublished [tombstone](../library-item-tombstones/feature.md).
  `deleteUserData=true` explicitly purges that data too. Both options default off
  in the existing item deletion dialog.
- **Remove one episode or one season** (`DELETE /api/library/episodes/{id}`,
  `DELETE /api/library/seasons/{id}`, both admin): the same two modes, applied to part
  of a series. A season takes its episodes and the extras parented to it. Both then
  prune what they emptied — a season once no published child carries its `SeasonId`,
  then the series once no published child remains. A published season-scoped extra
  keeps its season. Retained history keeps unpublished ancestors under the same
  tombstone rules. The response reports
  `{ seasonRemoved, seriesRemoved }` so a caller standing on the series page knows when
  that page is gone. `deleteUserData` has the same opt-in purge semantics as
  top-level deletion.
- **Remove one version** (`DELETE /api/library/sources/{id}`, admin): drops a single
  `MediaSource` of a movie or an episode, used to retire the original after a verified
  transcode. The item and user history stay. If no other source remains, the API
  returns **409** with `error: last_media_source` before changing any rows or files.
  The check runs inside the file-mutation lock, so concurrent requests cannot
  remove the last two versions separately. Every source counts, including a
  Blu-ray directory; streams and sidecars are not versions.
- The last version's delete control opens the existing **Delete movie?** or
  **Delete episode?** dialog and explains that the last version cannot be removed
  separately. Opening or cancelling it changes nothing; files and user data remain
  unchecked by default. A stale version list that receives `last_media_source`
  refreshes its data and opens the same dialog for explicit confirmation, without
  automatically deleting the item or carrying over a file-deletion choice.
- Purging an item drops its `ImageAsset` rows; tombstones and version-only removal
  retain its artwork. Cached artwork binaries with no remaining references are
  reclaimed later by the image-cache sweep (see
  [Storage and data](../storage-and-data/feature.md)), not inline.
- Any of these is refused with **409** while the owning item (for a season or episode,
  its series) is being moved to another catalog.
- **Remove download** (`DELETE /api/torrents/{id}`) only applies while a download
  exists (before the download→identify hand-off); it clears the `.incoming/` data
  and the in-flight ingest. After the hand-off there is no download — removal goes
  through the library.

## Move Semantics

**Move to another catalog** (`POST /api/library/{id}/move`, admin) relocates a
published top-level **movie or series** into another **type-compatible** catalog
(`movie` items → a `movie` catalog; `series`/`anime` interchange). It runs as a
background job (its id is returned; progress rides the realtime stream) because a
cross-volume move copies bytes:

- **Re-point** (the target catalog does not already hold this identity): the
  existing rows move as-is — `MediaItem.CatalogId` changes and `PublicId` is
  re-minted (the Jellyfin id changes, clients re-sync), and files move into the
  target's canonical layout. The internal `Id` is preserved, so
  `UserData`/metadata/credits survive.
- **Merge** (the target already holds this identity): the source's media sources are
  reassigned onto the existing target item as additional versions (edition-labelled
  to keep paths distinct) — for a series, decided per episode — and the orphaned
  source rows are pruned, mirroring remap.

A move within one volume is an atomic rename; across volumes it copies then deletes
the source after the database commit, with a free-space pre-check on the target
volume (mirroring the torrent add check). The owning `IngestItem.CatalogId` follows
the item. Only movies and series (not episodes, seasons, or unmatched videos) move
in v1.

## Blu-ray source integration

[Blu-ray sources](../bluray-import/feature.md) are directory sources for movies.
Their import, manual MKV preparation and playback availability are documented
separately; internal disc clips are not individual library versions.
Removing an MKV while a disc remains is allowed: the title stays present with
conversion-required availability. A lone disc follows the same last-source refusal.

## Testing Expectations

Backend tests should use xUnit and Imposter. Required coverage:

- Catalog root resolution and containment checks.
- Path sandboxing, traversal, and symlink-escape rejection (including refusing to
  delete inside `.incoming/`).
- Move behavior for organize and remap; staging cleanup on hand-off and download
  removal; canonical-file deletion on library item removal.
- Episode and season removal: siblings and containers survive a single episode; a
  season takes its episodes and season-scoped extras; emptied containers are pruned
  while a leftover extra keeps its season; `deleteFiles` false leaves the file for a
  rescan and true erases it; a movie, a top-level series, or an unpublished row is
  refused; the source file is detached rather than deleted.
- `LibrarySourceDeletionTests`: last-version refusal for movies and episodes with
  either file-deletion choice preserves files, sidecars, streams, default source,
  ingest ownership and user history; ordinary version removal and disc counting;
  concurrent removals leave one source; HTTP 404, 409 with `last_media_source`, and
  204 remain distinct.
- Web `last-version-deletion.spec.ts`: movie and episode dialogs, cancellation,
  safe defaults on every open, explicit file/history choices, ordinary version
  deletion, stale-list conflicts, viewer permissions and MKV-plus-disc counting.
- Import scan skips `.incoming/` and already-known files.
- Large-file streaming stays inside catalog roots.
