# Library Item Tombstones

Created: 2026-07-26
Updated: 2026-08-31

Deleting a movie, series, season, or episode no longer erases the user's
relationship with it. An item some user favorited, rated, or has at least one
play of in their history survives deletion as a **tombstone**: the
`MediaItem` row stays, unpublished (`PublicId` null) and stamped with
`RemovedAt`, together with its metadata, artwork, person credits,
`UserItemData`, and every `PlaybackHistoryEntry`. Its sources, streams, and
(when asked) files are removed exactly as before. An item nobody has touched
is purged completely — tombstones preserve history, they don't hoard husks.

## What counts as signal

A **history entry**, a **rating**, or a **favorite** — for any user. Nothing
else, and the exclusions are deliberate:

- `Played` and `PlayCount` are projections of history and state nothing it does
  not: every path that marks something watched writes an entry
  (`WatchHistoryRecorder` stages a timeless one for a first manual mark, dated
  ones for completions, manual logs and provider sync). Reading them as well
  only made a title immortal when its counters had drifted from the entries they
  came from — and a drifted counter is not something the UI can clear, so the
  ghost could never be emptied.
- A **resume position** is not a relationship: an abandoned half-watch says the
  viewer did not care.

The same definition decides what a catalog scan does with a title whose files
vanished (see [Catalog maintenance](../catalog-maintenance/feature.md)).

## The last mark takes the ghost with it

Clearing the last thing that holds a tombstone up purges it, then and there:
the last rating or favorite cleared from its card, or the last play deleted
from the watched calendar. Signal is judged across **every** user, so tidying
up one's own marks never erases someone else's history.

It is immediate rather than swept up later because clearing is the only way to
empty a ghost: a row left standing with nothing on it is a title nobody can
see, play, or reach — and one that could never be removed again.

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
- The **Movies** and **Series** grids offer a **Show removed** toggle, which
  appends the signed-in user's ghosts of that kind after the library — dimmed,
  badged *Removed*, and never mixed into it. The toggle is off by default and
  absent entirely when this user has none; it lives in the URL (`?removed=1`),
  like the catalog filter, so the view survives a refresh.

A ghost card opens a dialog rather than a page — there is no page, and nothing
about it can be played or edited. The dialog shows the user's signal summary
(favorite, rating, plays aggregated across the ghost subtree, last watched) and
the only writes that reach a tombstone:

- **Unfavorite**, across the whole ghost subtree — the favorite may sit on an
  episode that kept the chain alive, and the ordinary endpoint refuses ghosts;
- **Clear rating** — its own action, because deleting a file does not retract a
  verdict on a film that was watched;
- **Delete permanently** (admin), the retroactive full purge of the tombstone
  and its subtree (`DELETE /api/library/removed/{id}`).

Clearing the last of those marks purges the ghost outright, as above.

## Testing Expectations

- `LibraryDeleteServiceTests` — tombstone vs purge decision, `deleteUserData`,
  container rules (extras, ghost ancestors), metadata retention,
  favorite-only and rating-only movies, and the exclusions: aggregate counters
  and a resume position keep nothing alive.
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
  and rating clearing, subtree purge and refusals, and the purge that follows
  the last mark being cleared.
- `WatchHistoryEntryServiceTests` — deleting a ghost's last play takes the
  ghost; another user's play keeps it; a published title is untouched.
- Web e2e (`removed-titles.spec.ts`) — the toggle's default-off, hidden-when-
  empty and URL behavior; the dialog's per-mark actions; admin-only permanent
  delete; a series ghost belonging to the series grid.
- Web e2e (`detail.spec.ts`) — both delete checkboxes and the query
  parameters they drive.
