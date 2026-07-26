# Library Item Tombstones

Created: 2026-07-26
Updated: 2026-07-26

Deleting a movie, series, season, or episode no longer erases the user's
relationship with it. An item some user favorited, watched, holds a resume
position on, or played at least once survives deletion as a **tombstone**: the
`MediaItem` row stays, unpublished (`PublicId` null) and stamped with
`RemovedAt`, together with its metadata, artwork, person credits,
`UserItemData`, and every `PlaybackHistoryEntry`. Its sources, streams, and
(when asked) files are removed exactly as before. An item nobody has touched
is purged completely — tombstones preserve history, they don't hoard husks.

## Deletion

- `DELETE /api/library/{id}`, `/episodes/{id}`, `/seasons/{id}` carry
  `deleteUserData` beside `deleteFiles`. The delete dialog exposes it as a
  second checkbox — "Also delete watch history and favorites" — which forces
  the old full purge; both checkboxes default off.
- A tombstoned leaf keeps its ancestor chain: the signal walks up to the
  season and series, and a container still holding earlier ghosts is never
  hard-deleted (`ParentId` is a `Restrict` FK, and the ghosts' history
  outlives the container). Container pruning counts **published** children
  only; an emptied container is tombstoned or purged by the same signal rule.
- Transient playback sessions are dropped from tombstones, and the wishlist's
  `TrackedTitle.MediaItemId` link is cleared by hand — the FK's `SetNull`
  fires only on a purge.
- Deleting a **catalog** (`CatalogService.DeleteAsync`) applies the same rule:
  items with signal become catalog-less tombstones (`CatalogId` is nullable
  and null only for them; the FK is `SetNull`, never a cascade that could
  erase history). Untouched items are purged with the catalog.

## Revival

Ingest identification adopts tombstones. The same-catalog identity lookup
prefers a live row, falls back to a ghost, and — for movies and series — then
searches tombstones anywhere (another catalog, or catalog-less): adoption
clears `RemovedAt`, re-homes the row into the ingesting catalog, and drags a
series' or movie's ghost children along, still unpublished, so the per-catalog
lookups cannot mint duplicates beside them. Season and episode ghosts only
return through their series' adoption — their parent links point into the
original hierarchy. The publish stage mints the public id and clears
`RemovedAt` definitively; `PublicIdFactory` refuses catalog-less items.

Because public ids are deterministic (kind + catalog + identity), a title
re-added to the same catalog resurfaces under its **old** public id — Jellyfin
clients see the same item again with its watched state intact. A cross-catalog
return surfaces under a fresh public id; the internal id, and with it all user
data, never changes. A rescan re-publishing an item removed without deleting
files takes the same path.

## Moves and remap

- A series move plans over published children only. On a re-point, ghost
  children follow into the target catalog without being republished; on a
  merge, whatever the move empties goes through the shared delete rules —
  merged rows with user signal survive as tombstones under their (equally
  ghosted) source hierarchy, and nothing a move leaves behind is republished.
  Merge-target lookups never select a tombstone.
- Remap migrates user signal to the corrected identity instead of deleting
  it: history entries repoint (a colliding user+session play is the same
  viewing twice and dies), per-user state repoints or merges field-wise
  (`IsFavorite`/`Played` OR, the target keeps its position and counts, with
  a manual `StateRevision` bump). The misidentified husk is then purged —
  its signal describes the file, and the file now lives under the target.

## Visibility

Every library surface — browse, rails, search, detail, Jellyfin, collections,
people, title preview, recommendations' "already in library", watch-history
sync preview/apply, catalog metadata refresh — reads published items only; a
tombstone surfaces nowhere. Two places deliberately know ghosts:

- The **watched calendar** keeps rendering plays of tombstoned titles with
  poster and title from the retained metadata (it never links to item pages).
- The **Removed titles** section on Settings lists every tombstoned movie and
  series with the signed-in user's signal summary (favorite, plays aggregated
  across the ghost subtree, last watched). It offers clearing one's own
  favorite — the one favorite write that reaches ghosts; the ordinary
  endpoint refuses them — and, for admins, **Delete permanently**: the
  retroactive full purge of the tombstone and its subtree
  (`DELETE /api/library/removed/{id}`). The section hides while empty.

## Testing Expectations

- `LibraryDeleteServiceTests` — tombstone vs purge decision, `deleteUserData`,
  container rules (extras, ghost ancestors), metadata retention,
  favorite-only movies.
- `CatalogServiceTests` — catalog-less tombstones with ancestor chains;
  untouched items purge with the catalog.
- `TombstoneRevivalTests` — end-to-end re-download revival: same internal and
  public id in-catalog, re-homing cross-catalog, ghost-chain revival for
  series.
- `RemapServiceTests` — history and state migration, OR-merge onto an
  existing target.
- `LibraryMoveServiceTests` — re-point carries ghosts unpublished; merge
  tombstones instead of purging.
- `TombstoneLeakRegressionTests` — ghosts leak into no read surface; the
  tracked-title unlink on tombstoning.
- `RemovedTitlesServiceTests` — list and signal summary, ghost-only favorite
  clearing, subtree purge and refusals.
- Web e2e (`detail.spec.ts`) — both delete checkboxes and the query
  parameters they drive.
