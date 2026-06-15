# Storage and Data

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

## App Data Directory

Everything Hosty should back up lives under `HOSTY_APP_DATA_DIR`:

- `media-server.db` (plus `-wal` / `-shm`).
- Metadata image cache.
- Torrent engine state (resume data, fast-resume).
- Background job and pipeline state.

### Backup Consistency

Because Hosty performs a directory-level backup, ensure the SQLite file is
consistent at backup time:

- Run SQLite in WAL mode and checkpoint before a backup, or
- Use the SQLite Online Backup API to write a consistent snapshot that the
  directory backup then captures.

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
  mount. Removing the app must never delete external media.

## Hardlink Constraint

The organizer relies on hardlinks between `files/` and `library/`, which requires
both to be on the same filesystem. Catalog configuration validates this
(compare `st_dev`) and rejects cross-filesystem roots. This is also why a single
`catalog.root` (rather than two unrelated paths) is the configuration unit.

## Testing Expectations

Backend tests should use xUnit and Imposter. Required coverage:

- EF Core mapping for relational entities and JSON columns.
- App data paths resolved from `HOSTY_APP_DATA_DIR`.
- Same-filesystem validation for catalog roots.
- Backup-consistency procedure (checkpoint / online backup) produces a readable
  database snapshot.
