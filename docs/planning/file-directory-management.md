# File and Directory Management

Status: Draft
Created: 2026-06-15
Updated: 2026-06-15

## Description

Media Server resolves and manipulates files only as part of catalog automation:
torrent intake, organizer hardlinks, streaming, cleanup, and library item
deletion. Every file operation must stay constrained to configured catalog roots
and reject directory traversal. v1 is not a general filesystem browser or manual
file manager.

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
- Create clean hardlinks from `files/` source files into `library/`.
- Remove or replace clean hardlinks during remap.
- Remove the `files/` seed copy when seeding is stopped or a download is removed.
- Delete a library item by removing its `library/` hardlink.
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
GET    /api/files/resolve?catalogId={id}&path=library/...   # internal/debug only
DELETE /api/library-items/{id}                              # removes clean library hardlink
```

Paths are expressed relative to a catalog root, never as absolute host paths.

## Open Questions

- What exact API shape should delete-library-item use, and should it optionally
  remove the source `files/` entry when no seeding is active?
  Recommendation: keep torrent removal and library item deletion as separate
  commands. Do not make `DELETE /api/torrents/{id}` delete watchable content.

## Testing Expectations

Backend tests should use xUnit and Imposter. Required coverage:

- Catalog root resolution and containment checks.
- Path sandboxing, traversal, and symlink-escape rejection.
- Hardlink create/remove behavior for organize, remap, stop-seeding, and library
  item deletion.
- Large-file streaming stays inside catalog roots.
