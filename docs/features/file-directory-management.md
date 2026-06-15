# File and Directory Management

## Description

Media Server manages files and directories across configured catalog roots. Every
file operation must stay constrained to those roots and reject directory
traversal. This supports the organizer and operator file management; it is not a
general filesystem browser.

## Sandbox

All file access is sandboxed to configured catalog roots (see
[Catalogs](catalogs.md) and [Storage and data](storage-and-data.md)).

- Resolve and normalize every path, then verify it is contained within a
  configured root before any operation.
- Reject symlink escapes and `..` traversal.
- The UI cannot select arbitrary host directories; catalog roots are configured
  through app settings, and (under `docker`) through Hosty-owned mounts.

## File Operations

- Upload, copy, move, delete, rename.
- Operations restricted to catalog roots.
- Use atomic operations where the OS supports them; hardlink creation for the
  organizer is atomic.
- Stream large files; support multi-file operations where practical.

## Directory Operations

- Create, copy (recursive), move, delete (recursive), rename.
- Long operations report progress through SignalR.
- All paths validated against traversal.

## API Endpoints

Internal endpoints (under `/api`, behind Host identity):

```text
GET    /api/files?catalogId={id}&path=/library
POST   /api/files/upload
POST   /api/files/copy
POST   /api/files/move
DELETE /api/files
POST   /api/directories
```

Paths are expressed relative to a catalog root, never as absolute host paths.

## Testing Expectations

Backend tests should use xUnit and Imposter. Required coverage:

- Catalog root resolution and containment checks.
- Path sandboxing, traversal, and symlink-escape rejection.
- Copy, move, delete, rename, create-directory behavior.
- Long-running operation progress reporting.
