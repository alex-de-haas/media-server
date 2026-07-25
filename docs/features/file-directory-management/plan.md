# Plan: Delete individual episodes and seasons

Status: Draft
Created: 2026-07-25
Updated: 2026-07-25

## Goal

Let an admin remove a single episode — or a whole season — from a published series,
with the same two modes the item-level delete already offers (drop the rows only, or
also erase the files). Today `LibraryDeleteService.DeleteAsync` accepts only a
published top-level `Movie`/`Series`, so replacing one bad episode file means
deleting and re-ingesting the entire show.

## Target behavior

Written as a diff against [File and directory management](../file-directory-management.md)
→ *Removal Semantics* and [Frontend application](../frontend-application/feature.md)
→ *Series detail*:

- **New** `DELETE /api/library/episodes/{id}?deleteFiles={bool}` (admin) removes one
  published episode: its media sources, streams, metadata records, image rows, credits
  and per-user data. `deleteFiles=true` also erases the episode's file(s) from the
  catalog and prunes the directories they leave empty; `deleteFiles=false` leaves them
  on disk for an import scan to re-adopt.
- **New** `DELETE /api/library/seasons/{id}?deleteFiles={bool}` (admin) does the same for
  every episode of one season, the extras parented to that season, and the season row.
- Both **prune emptied containers**, counting *every* remaining child rather than only
  episodes: a season is removed once nothing carries its `SeasonId` — a season-scoped
  extra (`Kind.Video`, which `IdentifyService.ResolveExtraAsync` parents to the season
  with both `ParentId` and `SeasonId`) keeps its season alive — and a series is removed
  once nothing is left under it (no seasons, episodes, or series-level extras). This is
  exactly what `RemapService.CleanupOrphanAsync` already counts after a remap, and the
  `Restrict` self-FK makes it mandatory: pruning a season that still has an extra under
  it would fail the delete outright. The response reports what was pruned so the UI can
  navigate away once the series is gone.
- Both are **refused with 409 while the owning series is moving** between catalogs, like
  every other library mutation.
- A `SourceFile` that fed the episode is **detached, not deleted** (`MediaItemId → null`),
  so the download's own data is untouched — mirroring the item-level delete.
- **Watch history follows the deleted episode**, as it already does for a deleted movie or
  series: `PlaybackHistoryEntry.MediaItemId` is a required cascading FK
  (`MediaServerDbContext.cs:590-594`, "a deleted item's plays cannot be projected or
  exported", asserted by `WatchHistorySchemaTests.DeletingAnItemDropsItsHistory`), so the
  episode's plays go with it. Queued exports are unaffected: `WatchHistoryOutboxEvent`
  carries a `MediaItemId` column with no FK plus its own frozen `IdentitySnapshot`, and so
  still describes the item as it was identified when the play happened. This work changes
  neither behavior and adds no migration.
- **Series detail → Episodes tab** gains, for admins: a delete action on each episode row
  and one on each season heading, each opening a confirm dialog with a "Delete files from
  disk" checkbox that defaults to off (mirroring `DeleteItemDialog`). When the last
  episode goes and the series is pruned, the page navigates back to `/series`.
- Unchanged: **no per-version control for episodes** — deleting an episode takes all of
  its media sources. `DELETE /api/library/sources/{id}` stays movies-only in the UI.

## Design notes

- `LibraryDeleteService` grows one shared private purge (dependents first, then items
  child→parent because the `ParentId` self-FK is `Restrict`) used by `DeleteAsync` and
  both new paths, rather than a fourth copy of that sequence — `RemapService`,
  `LibraryMoveService`, and `CatalogService` each already keep their own. The shared
  version also deletes `MediaItemPersons`, which `LibraryDeleteService` currently omits
  while the other two delete it explicitly.
- File targets are collected before the rows go and erased after the commit, reusing
  `GatherLibraryFilesAsync` + `LibraryFileEraser` (which already refuses `.incoming/`
  and cleans emptied parents up to the catalog root).
- `LibraryMoveGuard.IsItemMovingAsync` resolves an id to `SeriesId ?? Id`, and both
  episodes and seasons carry `SeriesId`, so the 409 guard needs no change.
- A double-episode file (`IndexNumberEnd`) is a single row: deleting it removes both
  numbers. The row already renders as `S01E01-E02`, so the dialog shows what goes.
- `EpisodeDto` gains `SeasonId` so the Episodes tab — which groups client-side by
  `seasonNumber` — can target the season row; a group whose episodes carry no `SeasonId`
  simply gets no season-delete action.
- Deleting with `deleteFiles=false` leaves a file a later catalog scan re-adopts and
  re-publishes; that is the documented behavior of the existing delete and is kept.
- Not addressed (pre-existing, unchanged by this work): cached artwork binaries under
  `{AppDataDir}/images/` are erased by no delete path — only their `ImageAsset` rows go.

## Deliverables

- [ ] Lazy doc migration: `git mv docs/features/file-directory-management.md` into this
      folder as `feature.md` (header drops `Status:`), and update the cross-references in
      `docs/root.md`, `torrents-and-organizer.md`, `domain-model.md`, `storage-and-data.md`,
      `catalogs.md`, `security.md`, plus the two `<c>docs/...</c>` comments in
      `CatalogPathSandbox.cs` and `LibraryMoveService.cs`.
- [ ] `LibraryDeleteService`: shared purge helper + `DeleteEpisodeAsync` /
      `DeleteSeasonAsync` with container pruning, returning what was pruned.
- [ ] `LibraryEndpoints`: the two admin routes behind `LibraryMoveGuard`.
- [ ] `EpisodeDto.SeasonId` + the `LibraryReadService.GetEpisodesAsync` projection.
- [ ] `media-server.ts`: `deleteEpisode` / `deleteSeason` clients.
- [ ] `media-detail.tsx`: episode-row and season-heading delete actions, the confirm
      dialog, the existing `invalidate()` key set, and navigate-away on a pruned series.
- [ ] Backend tests: a new `LibraryDeleteServiceTests` (episode delete keeps siblings;
      season delete takes its episodes and its season-scoped extras; emptied season and
      series pruned; a leftover season-scoped extra keeps its season, and the delete still
      succeeds; `deleteFiles` false/true; a movie or top-level series id rejected on the new
      routes; `SourceFile` detached; `UserItemData` and the episode's `PlaybackHistoryEntry`
      rows gone, its `WatchHistoryOutboxEvent` rows kept) and endpoint tests for the admin
      gate and the 409 move guard.
- [ ] Web tests for the admin gate on the new actions and the dialog's default-off checkbox.
- [ ] Docs: `feature.md` Removal Semantics, endpoint list, and Testing Expectations; the
      series-detail bullet in `frontend-application/feature.md`; the duplicated Removal
      Semantics in `torrents-and-organizer.md`; regenerate `docs/root.md`.
- [ ] `manifest.json` 0.37.1 → 0.38.0 (new functionality) in the same commit.
- [ ] Delete this `plan.md`.

## Phases

One PR, per the repository's one-PR-per-feature rule:

1. Backend — service, endpoints, DTO field, tests.
2. Web — API client, episode/season actions, dialog, tests.
3. Docs — the migration, the three doc updates, index regeneration, version bump.

## Verification

- `dotnet build --configuration Release` and `dotnet test --configuration Release`.
- `pnpm -C src/web lint`, `pnpm -C src/web test`, `pnpm -C src/web build`.
- `node scripts/docs-index.mjs --check`.
- Manual, through Hosty Core: delete one episode of a multi-episode season with the
  checkbox off (row goes, file stays, a rescan re-adopts it), then with it on (file and
  the emptied folder go); delete a whole season; delete the last remaining season and
  land back on `/series`; confirm a non-admin sees no action and the API answers 403.

## Open questions

None. Scope confirmed: episode **and** whole season, emptied season **and** series pruned,
no per-version control for episodes.
