# Torrents and Organizer

## Description

Media Server downloads torrents with MonoTorrent as a background service and
organizes completed content into each catalog's clean `library/` structure using
hardlinks. Torrent state, progress, and seeding status are exposed to the UI in
real time through SignalR.

## Torrent Engine

MonoTorrent runs as a hosted service. Capabilities:

- Magnet links and `.torrent` files.
- Pause, resume, stop.
- Sequential download (for future partial-streaming use cases).
- Per-torrent download directory (always a catalog's `files/`).
- Per-torrent and global speed limits (`TORRENT_MAX_DOWNLOAD_SPEED`,
  `TORRENT_MAX_UPLOAD_SPEED`).

At add time the operator selects a **catalog** (not a raw path). The catalog
resolves the download directory (`<catalog.root>/files/`), the type used for
parsing, the naming template, and the default seeding policy. A per-torrent
`keepSeeding` flag overrides the catalog default.

## Lifecycle and Seeding

Seeding after completion is optional, per the `keepSeeding` policy.

```text
Queued → Downloading → Completed → (keepSeeding ? Seeding : StopSeeding) → Stopped
                                                         ↘ Error
```

- `keepSeeding = true`: the torrent stays in `Seeding`, visible in the downloads
  list with ratio/upload, and the operator can **Stop seeding** at any time.
- `keepSeeding = false`: seeding is stopped automatically after completion.

Each torrent stores: info hash, name, catalog id, progress, download/upload
speed, ratio, ETA, state, `keepSeeding`, and the `files/` save path.

## Organizer (hardlink)

Because each catalog keeps `files/` and `library/` on one volume, the organizer
uses a **single hardlink-based strategy** with no data copying or moving:

1. Pick the relevant media file(s) from the completed download (skip samples and
   junk; a season pack yields several files).
2. Create hardlinks under `library/` with clean names that preserve the original
   extension (see [Catalogs](catalogs.md)).
3. Seed (when `keepSeeding`) directly from the `files/` copy.

Stopping seeding (immediately, when `keepSeeding = false`, or later on operator
action) simply **unlinks the `files/` entry**. The data persists in `library/`
through the remaining hardlink — the inode link count keeps the bytes alive. No
copy, no move, everything stays within the volume.

| Scenario | Operation | Data copied |
| --- | --- | --- |
| keep seeding | download → `files/`, hardlink → `library/`, seed from `files/` | 0 |
| stop seeding (now or later) | unlink `files/` entry; `library/` link keeps data | 0 |
| never seed | hardlink → `library/`, then unlink `files/` | 0 |

A hardlink is a second directory entry pointing to the same inode; it works only
within one filesystem, which is guaranteed because `files/` and `library/` share
a catalog root (validated on configuration).

> Cross-volume catalogs (a future Docker concern, where `files/` and `library/`
> could be different mounts) would require copying; v1 forbids that configuration.

## API Endpoints

Internal endpoints (under `/api`, behind Host identity):

```text
POST   /api/torrents/add           # { source, catalogId, keepSeeding? }
POST   /api/torrents/{id}/pause
POST   /api/torrents/{id}/resume
POST   /api/torrents/{id}/stop-seeding
DELETE /api/torrents/{id}
GET    /api/torrents
```

## Real-Time Updates

The SignalR hub broadcasts progress, speed, ratio, and state/seeding changes. The
client subscribes once and receives updates for all active torrents and for the
downstream pipeline activity.

## Testing Expectations

Backend tests should use xUnit and Imposter. Required coverage:

- Add from magnet links and `.torrent` files with a chosen catalog.
- Pause, resume, stop-seeding, delete, and error-state transitions.
- `keepSeeding` policy resolution (per-torrent override of catalog default).
- Organizer hardlink creation, extension preservation, and season-pack handling.
- Stop-seeding unlink preserves library data (link-count behavior).
- Save path validation against catalog `files/` roots.
- Progress and state events published through the application event model.
