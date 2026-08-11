# Cache Storage

Created: 2026-08-11
Updated: 2026-08-11

The app declares the Hosty cache directory (docker-host
`docs/features/app-cache-storage/feature.md`) and keeps its remux indexes there:
derived, rebuildable data that Hosty persists across restarts and updates but never
backs up. Before this, over a gigabyte of indexes lived under `data/` and rode along
in every backup — including the automatic pre-update snapshot taken while the app is
stopped, where the cost was minutes of downtime on every update.

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

## Migration

Startup calls `RemuxIndexStore.MigrateFrom(AppDataDir)` right after schema
migrations, so hours of background walking survive the layout change instead of
being rebuilt. The move is synchronous and one-time: a rename per file on one
filesystem, a copy across the two docker binds. It is idempotent — a crash mid-way
resumes on the next start, a file already at the destination wins, stray `.partial`
leftovers are deleted — and the legacy `data/remux-index/` directory is removed once
emptied, after which every later start returns immediately. When cache and data
resolve to the same root (the fallback), the migration recognizes itself as a no-op.

A restore of an old backup can make the database older than the cache; the store's
per-file stamps (source length and mtime, checked on every load) already answer
that — a stale index reads as absent and rebuilds.

## Testing Expectations

- `HostyOptionsTests` — cache root resolution: injected path preferred, data
  directory fallback.
- `RemuxIndexStoreTests` — migration: moves indexes and removes the legacy
  directory; destination wins on a double-sided id and a re-run is a no-op; a
  shared root (the fallback) moves and deletes nothing.
