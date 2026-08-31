# Catalog Maintenance

Created: 2026-08-31
Updated: 2026-08-31

The library keeps itself in step with two actions, both offered per catalog and
over every catalog at once, and both also run on their own:

- **Scan for media** syncs a catalog with its disk, in both directions.
- **Refresh metadata** syncs stored metadata with its sources.

They live on the Catalogs page, where the storage they act on lives. Settings
carries no upkeep controls.

## Scan for media

`POST /api/catalogs/{id}/scan` for one catalog, `POST /api/catalogs/scan` for
all of them, and nightly at **03:00 local** for all of them
(`NightlyMaintenanceWorker`). Both endpoints answer with what the scan did; the
caller waits for it.

### Is the storage there at all

Before anything else, the scan decides whether the disk is answering, because
every removal below depends on that answer being right.

A catalog sits on one mount, so its files are present or absent together:

- the root directory is missing → **offline**;
- the root is there and **none** of the catalog's known library files can be
  read → **offline**. This is the case a directory check misses: an unmounted
  bind mount still presents as an empty directory inside a container;
- **any** file can be read → the volume is there, and the files that cannot be
  read really were deleted.

An offline catalog is stamped (`Catalog.OfflineSince`), announced once through
Core, and otherwise left completely alone — nothing is imported and nothing is
removed. A catalog whose files answer again is announced as recovered and the
marker cleared. `CatalogHealthService`'s five-minute poll shares that rule but
only in the safe direction: it can mark a catalog offline on a missing root, and
clears the marker only once a file has actually been read, so it never undoes
what a scan concluded.

Acting on the "really deleted" case is safe in a way it could not be for files:
a scan never erases anything from disk. A wrong call costs a metadata refetch —
the file's return is picked up by the next scan, identification adopts the
tombstone, and the title comes back under its old public id with its history
intact.

### What arrives

Unchanged: media files under the root with no `MediaSource` row are ingested
from the identify stage (`LibraryImportService`), `.incoming/` is never scanned,
and confident matches publish while the rest land in review.

### What is gone

Every published `MediaSource` of the catalog is resolved against the disk, and
so is every sidecar file beside one that survived. What the disk no longer backs
is removed through the ordinary delete pipeline, with both switches off — the
files are already gone, and user data is kept:

- a gone file whose item keeps another version drops **that version** only, and
  the item's default-version pin is cleared if it named it;
- a gone sidecar beside a surviving file drops **its track**;
- an item whose every version is gone goes through
  `LibraryDeleteService.RemoveVanishedAsync`, which routes by kind so a leaf
  still prunes the containers it empties, and which decides per item whether the
  title becomes a **tombstone** or is **purged** — see
  [Library item tombstones](../library-item-tombstones/feature.md).

### Saying so

A scan that removed anything publishes one Core notification per catalog naming
the outcome rather than the symptom: how many files are gone, how many titles
left the library but kept their watch history, and how many nobody had watched
were deleted. A nightly job that unpublishes a film without a word reads as data
loss the next morning.

The web reports the same thing as a toast, through
`scanSummary`/`libraryScanSummary`, which name the halves that actually happened
instead of printing every counter.

## Refresh metadata

`POST /api/catalogs/{id}/refresh-metadata` for one catalog and
`POST /api/catalogs/refresh-metadata` for all of them, queued the same way so
they run one at a time; a catalog already refreshing is left to the run it is in
rather than refused. Progress streams over `/api/events` as before.

Each run re-enriches every identified, published item in the catalog, and then
**fills in media data**: sources whose data came from the container-header
reader rather than the transcode engine are re-probed, and sidecar rows still
missing their codec, channels, sample rate or bitrate are read. That was its own
Settings button until this pass absorbed it; it is bounded to the rows a weaker
provider wrote, so it is not a re-probe of the library. If it fails, the run
still succeeds — the provider half already landed, and losing the probe half is
worth a log line, not a job the operator has to repeat.

### Nightly, only what changed

A full refresh is thousands of provider calls to learn that almost nothing
moved, so the scheduled pass follows TMDb's change lists instead
(`IncrementalMetadataRefreshService`, after the scan at 03:00):

- `/movie/changes` and `/tv/changes` name everything the provider edited in a
  date range; the library is intersected with that, and only the titles held
  here are enriched. Removed titles are not: provider calls spent on a ghost buy
  nothing. The window is walked a **day at a time**, the unit TMDb answers in —
  asking for a fortnight at once would run to hundreds of pages, and a day that
  cannot be read in full is reported as no answer rather than as a short one.
- `AppSettings.MetadataChangesSyncedThrough` is how far the pass has followed.
  It advances only when a pass completes, so a night the provider was
  unreachable is **retried** rather than stepped over.
- An individual title whose enrich threw is named in
  `AppSettings.MetadataRefreshRetries` and is due again the next night, whether
  or not the provider touches it again. Holding the marker back for it instead
  would grow the window every night until it clamped, and then re-refresh a
  fortnight forever over one unreachable title.
- The first ever run records the instant and refreshes nothing: the library was
  enriched as it was imported, and reaching backwards would refresh titles on no
  evidence.
- A gap longer than the 14 days TMDb keeps is **clamped** to what it can still
  answer, with a warning. It never degrades into a full refresh — that is the
  expensive pass this exists to avoid, and it stays a manual action.

Because this runs unattended, what enrich must not touch is pinned down by
`EnrichPreservesManualEditsTests`: a pinned poster, the default-version pin, and
hand-written track labels and sidecars all survive it.

## Testing Expectations

- `CatalogScanServiceTests` — the mount rule in both directions (every file
  unreadable → offline and untouched; one readable file → the rest are real
  deletions), a missing root, an empty catalog, version and sidecar removal with
  pin survival, ghost-vs-purge, series pruning, recovery, and the all-catalogs
  pass.
- `CatalogHealthServiceTests` — an offline catalog stays offline while its root
  is back but empty.
- `LibraryDeleteServiceTests` — the signal definition: history, rating or
  favorite keep a title; aggregate counters and a resume position do not.
- `IncrementalMetadataRefreshServiceTests` — marker behavior (first run, retry
  on provider failure, clamped window), intersection with the library, ghosts
  excluded, and a failed enrich owed again the next night.
- `TmdbChangeFeedTests` — the window walked day by day, every page of a day
  read, and a failed day answered as nothing rather than as a short list.
- `EnrichPreservesManualEditsTests` — the operator's choices survive an
  unattended enrich.
- `NightlyMaintenanceWorkerTests` — the 03:00 schedule.
- Web unit (`catalog-scan.test.ts`) — every shape of scan summary, including the
  offline one that must not read as a clean bill of health.
- Web e2e (`catalogs.spec.ts`, `settings.spec.ts`) — the per-catalog and global
  scan outcomes, the offline report, and Settings carrying no upkeep controls.
