# File and Directory Management

Status: Implemented
Created: 2026-06-15
Updated: 2026-06-21

## Description

Media Server resolves and manipulates files only as part of catalog automation:
torrent intake, organizer moves, import scans, streaming, cleanup, and library
item deletion. Every file operation must stay constrained to configured catalog
roots and reject directory traversal. v1 is not a general filesystem browser or
manual file manager.

## Sandbox

All file access is sandboxed to configured catalog roots (see
[Catalogs](catalogs.md) and [Storage and data](storage-and-data.md)).

- Resolve and normalize every path, then verify it is contained within a
  configured root before any operation.
- Reject symlink escapes and `..` traversal.
- The UI cannot select arbitrary host directories. Under v1 `dev` runtime,
  catalog roots are configured host paths. Under future `docker` runtime, roots
  must come from Hosty-owned mounts.

## Supported Operations

- Resolve catalog-relative paths to absolute paths.
- Move a completed file from `.incoming/` into its canonical place at the root.
- Move (rename) the canonical file during remap.
- Move a published item's file(s) into **another catalog** (see Move Semantics).
- Clear a download's `.incoming/` staging folder once its file moves out, or when
  the in-flight download is removed.
- Delete a library item by removing its canonical file.
- Import scan: enumerate the catalog root (excluding `.incoming/`) for orphan
  media files.
- Stream large files without whole-file buffering.

## Unsupported v1 Operations

- Arbitrary upload, copy, move, and rename from the UI.
- Recursive directory management as a standalone user workflow.
- Direct editing of raw torrent folders outside the organizer/remap flow.
- Extracting archived/multi-part releases (`.rar`/`.zip`); v1 organizes only plain
  video files.

## API Endpoints

Internal endpoints (under `/api`, behind Host identity):

```text
GET    /api/files/resolve?catalogId={id}&path=...   # internal/debug only
DELETE /api/library/{id}?deleteFiles={bool}         # removes the published item (and file if asked)
POST   /api/catalogs/{id}/scan                       # import scan of the catalog root
POST   /api/library/{id}/move                        # move the item into another catalog → { jobId }
```

Paths are expressed relative to a catalog root, never as absolute host paths.

## Removal Semantics

With one tree, one file backs one item:

- **Remove from library** (`DELETE /api/library/{id}`): drops the DB rows;
  `deleteFiles=true` also deletes the canonical file, `deleteFiles=false` leaves it
  on disk for a later import scan to re-adopt.
- **Remove download** (`DELETE /api/torrents/{id}`) only applies while a download
  exists (before the download→identify hand-off); it clears the `.incoming/` data
  and the in-flight ingest. After the hand-off there is no download — removal goes
  through the library.

## Move Semantics

**Move to another catalog** (`POST /api/library/{id}/move`, admin) relocates a
published top-level **movie or series** into another **type-compatible** catalog
(`movie` items → a `movie` catalog; `series`/`anime` interchange). It runs as a
background job (its id is returned; progress rides the realtime stream) because a
cross-volume move copies bytes:

- **Re-point** (the target catalog does not already hold this identity): the
  existing rows move as-is — `MediaItem.CatalogId` changes and `PublicId` is
  re-minted (the Jellyfin id changes, clients re-sync), and files move into the
  target's canonical layout. The internal `Id` is preserved, so
  `UserData`/metadata/credits survive.
- **Merge** (the target already holds this identity): the source's media sources are
  reassigned onto the existing target item as additional versions (edition-labelled
  to keep paths distinct) — for a series, decided per episode — and the orphaned
  source rows are pruned, mirroring remap.

A move within one volume is an atomic rename; across volumes it copies then deletes
the source after the database commit, with a free-space pre-check on the target
volume (mirroring the torrent add check). The owning `IngestItem.CatalogId` follows
the item. Only movies and series (not episodes, seasons, or unmatched videos) move
in v1.

## Testing Expectations

Backend tests should use xUnit and Imposter. Required coverage:

- Catalog root resolution and containment checks.
- Path sandboxing, traversal, and symlink-escape rejection (including refusing to
  delete inside `.incoming/`).
- Move behavior for organize and remap; staging cleanup on hand-off and download
  removal; canonical-file deletion on library item removal.
- Import scan skips `.incoming/` and already-known files.
- Large-file streaming stays inside catalog roots.
