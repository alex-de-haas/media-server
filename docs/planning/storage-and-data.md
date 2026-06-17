# Storage and Data

Status: Draft
Created: 2026-06-15
Updated: 2026-06-15

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
  The torrent engine tracks them in memory and broadcasts them over SignalR; only
  **state transitions** (e.g. Completed) are persisted, and a transition is what
  triggers downstream pipeline actions (hardlink, probe, enrich, publish).
- The orchestrator claims an ingest item with a lease (`LeaseOwner`/`LeaseUntil`)
  and uses an optimistic-concurrency token, so the reconciler and operator actions
  never double-drive the same item (see [Domain model](domain-model.md)).
- No write transaction is held open across I/O (ffprobe, provider HTTP): do the
  long operation first, then a short write.

## App Data Directory

Everything Hosty should back up lives under `HOSTY_APP_DATA_DIR`:

- `media-server.db` (plus `-wal` / `-shm`).
- Metadata image cache.
- Torrent engine state (resume data, fast-resume).
- Background job and pipeline state.

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

The image cache is regenerable; if backup size matters, it can be excluded from
backup in a later refinement, but the default is to keep all app data in one
backed-up directory.

## Catalog Roots

Catalog media folders are **not** app data and are not backed up by Hosty (the
operator owns that media and its own backups).

- Each catalog root is a host directory on a single filesystem containing
  `files/` and `library/` (see [Catalogs](catalogs.md)).
- **v1 (`localCommand`):** roots are operator-configured host paths; the host
  process accesses them directly, with no volume mounts. Path access is sandboxed
  to configured roots (see [File and directory management](file-directory-management.md)).
- **Future (`docker`):** each catalog root becomes an external host-path bind
  mount once Hosty supports the required mount model. Removing the app must never
  delete external media.

## Hardlink Constraint

The organizer relies on hardlinks between `files/` and `library/`, which requires
both to be on the same filesystem. Catalog configuration validates this
(compare `st_dev`) and rejects cross-filesystem roots. This is also why a single
`catalog.root` (rather than two unrelated paths) is the configuration unit.

## Open Questions

- What Hosty mount/bind model should replace plain host paths for Docker
  runtime?
  Recommendation: keep v1 on `dev` / `localCommand` with explicit configured
  host paths, then design Hosty-owned external catalog mounts separately before
  enabling Docker as the default runtime.

## Testing Expectations

Backend tests should use xUnit and Imposter. Required coverage:

- EF Core mapping for relational entities and JSON columns.
- App data paths resolved from `HOSTY_APP_DATA_DIR`.
- Same-filesystem validation for catalog roots.
- Migration apply on startup and correct migration history after a simulated
  restore; failure path refuses to start half-migrated.
- Progress is not persisted; only state transitions are written and trigger
  downstream actions.
- Backup-consistency procedure (checkpoint / online backup) produces a readable
  database snapshot.
