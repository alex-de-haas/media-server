# Cache Storage

Created: 2026-08-11
Updated: 2026-08-11

The app declares the Hosty cache directory (docker-host
`docs/features/app-cache-storage/feature.md`) and keeps its remux indexes and
downloaded artwork there: derived, rebuildable data that Hosty persists across
restarts and updates but never backs up. Before this, over a gigabyte of indexes —
and every downloaded poster, backdrop, and person photo — lived under `data/` and
rode along in every backup, including the automatic pre-update snapshot taken while
the app is stopped, where the cost was minutes of downtime on every update.

## Contract

- `manifest.json` declares the `cache` block beside `data`, shaped identically: the
  docker target binds `/app/cache` into the `api` service, the dev target is
  environment-only. Core injects `HOSTY_APP_CACHE_DIR` either way.
- `HostyOptions.AppCacheDir` resolves the root: the injected path when present,
  otherwise the data directory. The fallback covers a Core that predates the cache
  contract and standalone runs — both simply keep the pre-cache layout, everything
  under `data/`, everything backed up.
- `RemuxIndexStore` roots `remux-index/` under that resolved directory. Nothing else
  about the store changed: format, stamps, invalidation, worker, and pruning are as
  [remux-streaming](../remux-streaming/feature.md) describes.
- `JellyfinImageService` roots the artwork cache (`images/`) under the same resolved
  directory. Naming, serving, and the periodic sweep are as
  [storage-and-data](../storage-and-data/feature.md) describes.

## Migration

Startup calls `RemuxIndexStore.MigrateFrom(AppDataDir)` right after schema
migrations, so hours of background walking survive the layout change instead of
being rebuilt. The move is synchronous and one-time: a rename per file on one
filesystem, a copy across the two docker binds. It is idempotent — a crash mid-way
resumes on the next start, a file already at the destination wins, stray `.partial`
leftovers are deleted — and the legacy `data/remux-index/` directory is removed once
emptied, after which every later start returns immediately. When cache and data
resolve to the same root (the fallback), the migration recognizes itself as a no-op.
Each file crosses the bind staged through a sibling temp name, appearing at its
final name only via a same-volume rename — an interrupted copy can never masquerade
as a migrated file. The whole pass is best-effort: a filesystem surprise is logged
and skipped, and startup never fails over rebuildable data.

Cached artwork migrates right after, by the same rules:
`JellyfinImageService.MigrateCache` moves `data/images/` the way `MigrateFrom`
moves the indexes, with one addition — `ImageAsset.LocalPath` pins absolute paths
into the legacy directory, so the migration repoints those rows before removing
it. Without that every migrated file would read as a cache miss and be fetched
again.

A restore of an old backup can make the database older than the cache; the store's
per-file stamps (source length and mtime, checked on every load) already answer
that — a stale index reads as absent and rebuilds. For artwork the same restore
reads as cache misses that refetch, and the sweep reclaims whatever the restored
database no longer names.

## Testing Expectations

- `HostyOptionsTests` — cache root resolution: injected path preferred, data
  directory fallback.
- `RemuxIndexStoreTests` — migration: moves indexes and removes the legacy
  directory; destination wins on a double-sided id and a re-run is a no-op; a
  shared root (the fallback) moves and deletes nothing.
- `ImageCacheMigrationTests` — artwork migration: moves files, repoints
  `LocalPath` rows, and removes the legacy directory; destination wins and a
  re-run is a no-op; a shared root moves, deletes, and repoints nothing.
- `ImageCacheSweeperTests` — the sweep follows the cache directory: the fixture
  roots `images/` under a cache directory distinct from data.
