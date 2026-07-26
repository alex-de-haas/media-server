# Library Item Tombstones

Status: Ready
Created: 2026-07-26
Updated: 2026-07-26

## Goal

Deleting a movie, series, season, or episode from the library stops erasing the
user's relationship with it. Watch history, the favorite flag, and watched
state survive the deletion by default; the item can return later (re-download,
rescan) and find its history waiting. A full purge — today's behavior — remains
available behind an explicit checkbox for users who really do want the history
gone.

This also dissolves the sharpest open question in
[Trakt favorites sync](../trakt-favorites-sync/plan.md): with tombstones,
deleting a file can never be mistaken for unfavoriting a work.

## Background

Today deletion is total. `LibraryDeleteService.PurgeItemsAsync` explicitly
bulk-deletes `UserItemData` (favorites, watched, resume), and every
`PlaybackHistoryEntry` dies via the FK cascade on `MediaItemId` — the model
comment says so outright: "History follows the item." A user who frees disk
space loses their screening diary and their favorites for those titles.

Three properties of the current code make the fix cheap:

- **Visibility is already a filter.** Every read surface — Jellyfin, browse,
  collections, people, search — shows only published items
  (`PublicId != null`; ~36 call sites). An unpublished `MediaItem` row is
  already invisible everywhere.
- **Identity reuse is already written.** `IdentifyService.ResolveMovieAsync`
  (and its series counterpart) looks up an existing `MediaItem` by catalog +
  provider identity *without* a published filter and reuses the row, keeping
  its internal `Guid`. `PublicIdFactory` mints public ids deterministically
  from kind + catalog + identity, so a returning item regains its previous
  public id — even Jellyfin clients see the same item again.
- **The watched calendar tolerates unpublished items.** Its join goes through
  the internal id and its DTO's `PublicId` is nullable, so history of an
  unpublished item still renders with title and poster.

## Target Behavior

### Deletion becomes a tombstone by default

Deleting an item with any user signal (a favorite, watched state, resume
position, or at least one history entry — for any user):

- **removes** `MediaSources`, `MediaStreams`, and, when "Delete files from
  disk" is checked, the files — exactly as today;
- **keeps** the `MediaItem` row itself, unpublished (`PublicId = null`) and
  marked with a new `RemovedAt` timestamp, plus its `MetadataRecords`,
  `ImageAssets`, person credits, `UserItemData`, and `PlaybackHistoryEntries`.
  `LibraryPath` and `DefaultSourceId` are cleared.

An item with **no** user signal is purged completely, as today — tombstones
exist to preserve someone's history, not to accumulate husks of never-watched
titles.

### Full purge stays one checkbox away

The delete dialog gains a second, independent checkbox beside "Delete files
from disk": **"Also delete watch history and favorites"**. Checked, it forces
today's full purge regardless of user signal. The three delete endpoints
(`DELETE /library/{id}`, `/library/episodes/{id}`, `/library/seasons/{id}`)
gain a matching `deleteUserData` query parameter alongside `deleteFiles`.

### Containers follow their children

Season and episode deletion currently prunes emptied containers. With
tombstones, "empty" counts **published** children only, and a container whose
last published child was tombstoned is tombstoned too — never left behind as a
published shell, and never hard-deleted while ghost children point at it.

### Catalog deletion

User signal is bound to the work, not to the shelf it stood on. Deleting a
catalog (`CatalogService.DeleteAsync`) applies the same rule as item deletion:
items with user signal become **catalog-less tombstones** — `CatalogId` turns
nullable and is cleared, since a ghost has no files and needs no root — while
items without signal are purged as today. A tombstone therefore survives its
catalog: the diary keeps rendering it, and it stays adoptable.

### Revival

When ingest identifies a title that matches a tombstone by identity, the
lookup adopts the row; publish clears `RemovedAt`, mints the public id, and
everything keyed to the internal id — favorites, watched state, every play —
is simply visible again. No relink logic exists anywhere, because nothing was
ever detached. The same applies to a rescan re-publishing an item that was
removed without deleting files.

Adoption is **not** limited to the tombstone's original catalog: a
catalog-less ghost, or one whose catalog no longer matches, is re-homed — its
`CatalogId` set to the ingesting catalog and the public id minted for it (the
id embeds the catalog, so a cross-catalog return surfaces under a new public
id; the internal id, and with it all user data, is unchanged). Same-catalog
tombstones take precedence over foreign ones when both match.

### The diary keeps its memory

The watched calendar continues to show plays of tombstoned titles, with poster
and title served from the retained metadata. The card and day-detail entries
must not link to the item page while the item has no `PublicId`.

### A window onto ghosts

The calendar shows only plays, and a tombstone can carry no plays at all — a
favorited, never-watched, deleted title would otherwise be invisible and
unmanageable. A **Removed titles** list, placed beside the existing library
maintenance surface (the missing-files scan report), shows every tombstoned
movie and series with its poster, title, and signal summary (favorite, play
count, last watched), and offers two actions: clear the signed-in user's
favorite, and **delete permanently** — the retroactive full purge (admin).

### Remap stops losing signal

`RemapService.CleanupOrphanAsync` currently deletes the misidentified orphan's
`UserItemData` and (via cascade) its history. Remap instead migrates them to
the target item: history entries repoint (per-play rows, no conflicts);
`UserItemData` merges field-wise where the target already has a row —
`IsFavorite` and `Played` combine with OR, resume position and play counts
keep the target's values when both exist.

## Deliverables

- [ ] Migration: `RemovedAt` on `MediaItem`; `CatalogId` nullable (null only
      for tombstones — a published item always has a catalog).
- [ ] `LibraryDeleteService`: tombstone path (default with user signal), full
      purge path (`deleteUserData` or no signal), container tombstoning for
      season/episode deletes; `deleteUserData` on the three endpoints.
- [ ] `CatalogService.DeleteAsync`: items with user signal become catalog-less
      tombstones instead of being purged with the catalog.
- [ ] Revival: identify/publish adoption of tombstones clears `RemovedAt`;
      covered for ingest re-download and rescan re-publish; cross-catalog
      adoption re-homes the ghost (same-catalog match preferred).
- [ ] Remap signal migration replacing the orphan-cleanup data loss.
- [ ] Query audit: every `MediaItems` read either filters published or
      deliberately serves tombstones (calendar); regression tests that a
      tombstone leaks into neither browse, Jellyfin, collections, people,
      search, nor title preview.
- [ ] Frontend: second checkbox in `DeleteItemDialog`; calendar cards and day
      detail stop linking when `PublicId` is null.
- [ ] Removed-titles surface: API listing tombstones with signal summary,
      unfavorite and permanent-purge actions, web page beside the library
      maintenance surface.
- [ ] Backend xUnit tests (tombstone vs purge decision, container rules,
      revival, remap merge) and frontend tests for the dialog and calendar.
- [ ] `feature.md`, `plan.md` deleted, index regenerated.

## Phases

One branch, one PR:

1. **Tombstones.** Migration, delete-service rework, endpoints, dialog.
2. **Revival and remap.** Adoption on identify/publish and rescan; remap
   migration.
3. **Audit and polish.** Query audit with leak tests, calendar link guard,
   the removed-titles surface, docs.

## Out of Scope

- **Simultaneous multi-catalog membership.** One movie living in two catalogs
  at once is still two `MediaItem` rows with independent user data. Making a
  single record belong to several catalogs is a far deeper change — the public
  id embeds the catalog, sources resolve their files through the item's
  catalog root, and Jellyfin views are derived from catalog membership — and
  is deliberately a separate future feature. Tombstone adoption narrows the
  gap only for the sequential case (deleted here, re-added there); the
  simultaneous case is instead forbidden outright by
  [single catalog per title](../single-catalog-per-title/plan.md) until
  multi-catalog membership is built for real.

## Verification

- `dotnet build` and `dotnet test`; frontend test run.
- Manual e2e: watch + favorite an item, delete it (files checked, history
  unchecked) → calendar still shows the plays, item absent from browse and
  Jellyfin; re-download the same title → same public id, favorite and history
  back. Repeat with the history checkbox → everything gone, as today.
