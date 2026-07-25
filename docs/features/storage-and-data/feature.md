# Storage and Data

Created: 2026-06-15
Updated: 2026-07-25

## Description

Media Server separates two kinds of storage: small **app data** that Hosty backs
up, and large **catalog roots** that hold the actual media. The database is
embedded so that backup is a directory copy and there is no extra server process
to operate.

## Database: SQLite

Media Server uses **SQLite** (single embedded file) via EF Core. SQLite is chosen
over a document database for two decisive reasons:

- **Backup compatibility.** Hosty backups cover the primary app data directory by
  copying it. A SQLite file lives inside that directory and is captured directly.
  A separate server such as MongoDB cannot be backed up safely by file copy (it
  needs `mongodump` or a stopped server) and adds an operational dependency that
  is especially awkward under the `localCommand` runtime.
- **Fit.** The domain is relational and small-scale (a home library): catalogs →
  items → media sources → streams; series → seasons → episodes; per-user user
  data; torrents; jobs. SQLite handles this comfortably with referential
  integrity and zero operations.

Document-style flexibility is obtained with **JSON columns** (SQLite JSON1 /
EF Core JSON mapping) for provider-specific and multi-language metadata blobs, so
there is no need for a document database.

## Schema Migrations

The schema evolves with standard **EF Core migrations**. Applied migrations are
tracked in `__EFMigrationsHistory` inside the database file, so a Hosty backup
captures migration history with the data, and after a restore the app applies
only newer migrations.

On startup `api` checks for pending migrations. If any exist:

1. Request an on-demand backup from Hosty Core (preferred). This depends on Core
   exposing an app-callable backup endpoint; if it is not available, the app
   applies migrations without a pre-migration backup (no local-copy fallback),
   relying on Hosty's `pre-update` backup taken before the new version starts.
2. Apply the migrations.
3. On failure, surface a clear notification recommending the operator restore the
   Media Server app data from Hosty, and refuse to start against a half-migrated
   database.

Because `api` is a single instance, there is no migration race between instances.

## Write Concurrency

SQLite is single-writer, so the app minimizes and serializes writes:

- Run in **WAL** mode (also required for backup consistency below) and set a
  `busy_timeout` (5–10 s) so transient lock contention retries instead of failing.
- **Torrent progress, speed, ratio, and ETA are never written to the database.**
  The external `torrent-engine` app tracks them in memory and streams them over its
  SSE, which Media Server relays to the UI; only **state transitions** (e.g.
  Completed) are persisted, and a
  transition is what triggers downstream pipeline actions (identify, move/organize,
  probe, enrich, publish).
- The orchestrator claims an ingest item with a lease (`LeaseOwner`/`LeaseUntil`)
  and uses an optimistic-concurrency token, so the reconciler and operator actions
  never double-drive the same item (see [Domain model](../domain-model.md)).
- No write transaction is held open across I/O (ffprobe, provider HTTP): do the
  long operation first, then a short write.

## App Data Directory

Everything Hosty should back up lives under `HOSTY_APP_DATA_DIR`:

- `media-server.db` (plus `-wal` / `-shm`).
- Metadata image cache (`images/`, see below).
- Background job and pipeline state.

(Torrent resume/fast-resume state is **not** here — it lives in the external
`torrent-engine` app's own data directory and is backed up with that app.)

### Image Cache

Artwork is a provider URL until it is first requested; the Jellyfin image surface
then downloads the binary and caches it under `images/`, keyed by what the file
holds rather than by who points at it:

- Item artwork is stored as `{tag}{extension}`, where the tag is the provider's
  image hash. Two items sharing artwork therefore share one file.
- Collection (BoxSet) artwork has no `ImageAsset` row, so its identity lives in
  the file name: `collection-{collectionId}-{slot}-{tag}{extension}`. Swapping a
  collection's poster changes the tag and lands the new art in a new file.

Nothing erases these files at delete time. Every purge path (library item delete,
catalog delete, remap, move-merge) drops `ImageAsset` rows with `ExecuteDelete`,
and a shared tag means a file is dead only once the *last* referencing row is
gone — a question about the whole table, not about the rows being deleted. A
catalog purge additionally never materializes its item ids on purpose, so it has
no tag list to erase from.

Instead a scheduled sweep bounds the cache: every 12 hours (first pass 10 minutes
after startup) it lists `images/`, rebuilds the set of live names from the
distinct `ImageAsset.Tag` values plus the names each `MovieCollection` can
currently produce, and deletes every file that matches none of them — including
`.tmp` leftovers from failed writes. Files written within the last hour are
skipped so a sweep never races an in-flight download. Because the sweep runs over
the directory rather than off a delete, it also reclaims artwork that existing
installs leaked before it existed. A cache miss is not an error state — the image
surface refetches — so reclaiming late, or reclaiming a file that turns out to be
live, costs at most one download.

### Backup Consistency

Hosty backups are **directory-level copies** of the primary `data/` directory,
created by Core/Shell/CLI (`manual`, `scheduled`, `pre-update`, `pre-restore`,
`pre-runtime-switch`); restore runs against a **stopped** app. Hosty does not
expose an app-facing pre-backup flush hook, so a `manual` or `scheduled` backup
can run while the app is writing. The app must therefore keep the data directory
continuously backup-safe on its own:

- Run SQLite in WAL mode so a hot copy of `*.db` + `*.db-wal` + `*.db-shm` is
  recoverable, and checkpoint periodically.
- Additionally maintain a periodic consistent snapshot via the SQLite Online
  Backup API (for example `media-server.snapshot.db`) inside the data directory,
  so any directory copy always contains a known-good database even if the live
  file is mid-write.
- Validate restore by stopping the app, restoring the directory, and starting it.

If Hosty later adds an app-facing pre-backup lifecycle hook, the app can use it to
checkpoint on demand; until then the app cannot assume one exists.

The image cache is regenerable and self-bounding (the sweep above keeps it to what
the library still references); if backup size matters it can be excluded from
backup, but the default is to keep all app data in one backed-up directory.

## Catalog Roots

Catalog media folders are **not** app data and are not backed up by Hosty (the
operator owns that media and its own backups).

- Each catalog root is a host directory on a single filesystem holding a transient
  `.incoming/` staging area plus the canonical published media at the root (see
  [Catalogs](../catalogs.md)).
- Under **`dev`** (`localCommand`) roots are operator-configured host paths that
  the host process reads directly, with no volume mounts.
- Under **`docker`** (the default runtime) they are Hosty-managed external
  host-path mounts, declared as `externalMounts.catalogRoots` in `manifest.json`
  and injected as `HOSTY_MOUNT_CATALOGROOTS` (comma-separated `label=path`
  entries). `MediaServerSettings` parses them and `CatalogService` rejects a
  catalog root outside them (see
  [Hosty runtime app](../hosty-runtime-app.md)).

Either way path access is sandboxed to the configured roots (see
[File and directory management](../file-directory-management/feature.md)), and the media
lives outside the app data directory, so removing the app never deletes it.

## Single-Filesystem Constraint

A completed file is **moved** (not hardlinked) from `.incoming/` into the
canonical tree. Because both live under one `catalog.root` on one filesystem, the
move is atomic and copies no bytes. This is why a single `catalog.root` (rather
than two unrelated paths) is the configuration unit.

## Testing Expectations

Backend tests should use xUnit and Imposter. Required coverage:

- EF Core mapping for relational entities and JSON columns.
- App data paths resolved from `HOSTY_APP_DATA_DIR`.
- The image cache sweep: an unreferenced file is reclaimed, a file whose tag is
  still shared by another `ImageAsset` row is kept, live and superseded collection
  artwork are told apart, stale `.tmp` leftovers are reclaimed, and recently
  written files are left alone.
- Migration apply on startup and correct migration history after a simulated
  restore; failure path refuses to start half-migrated.
- Progress is not persisted; only state transitions are written and trigger
  downstream actions.
- Backup-consistency procedure (checkpoint / online backup) produces a readable
  database snapshot.
