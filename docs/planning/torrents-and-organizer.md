# Torrents and Organizer

Status: Draft
Created: 2026-06-15
Updated: 2026-06-15

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

Before starting, the engine checks free space on the catalog volume against the
torrent size. For `.torrent` files (size known up front) a download larger than
free space is **refused**. For magnet links the size is unknown until metadata
arrives, so the check runs after start and surfaces a **notification** if the
content will not fit. The UI shows each catalog's free space when the operator
picks a destination (see [Catalogs](catalogs.md)).

## Networking

The torrent engine needs inbound connectivity for good peer performance and for
fetching magnet metadata via DHT. This is configured by the operator, not by
Hosty port assignment, because it is a raw TCP/UDP listener and not an HTTP
endpoint that Hosty proxies (see [Hosty runtime app](hosty-runtime-app.md)).

- Fixed listen port from `TORRENT_LISTEN_PORT` (TCP + UDP).
- **DHT** enabled (required to fetch metadata for magnet links without trackers),
  plus **PEX** and **LSD** for peer discovery.
- **Protocol encryption (MSE/PE)** enabled to reduce ISP throttling.
- Optional **UPnP / NAT-PMP** automatic port mapping
  (`TORRENT_ENABLE_PORT_MAPPING`, default on); when the router does not support
  it, the operator forwards the port manually.
- Optional bind to a specific interface/address (`TORRENT_BIND_ADDRESS`) for VPN
  setups. A full VPN with kill-switch is the operator's OS/network responsibility
  in v1 (a Docker network sidecar is a future option).

## Intake Matching

When a torrent is added, Media Server parses the torrent name and, when
available, the torrent file list before the payload is fully downloaded.

- For movie catalogs, each playable source file should map to one movie.
- For series catalogs, each playable source file should map to one concrete
  episode. A torrent may represent a whole show, one or more seasons, a single
  episode, or a mixed folder.
- High-confidence matches can be accepted automatically. Ambiguous matches are
  shown to the operator before organize/publish proceeds.
- The operator can confirm suggested mappings at intake time, or later remap a
  specific source file if the automatic match was wrong.

## Lifecycle and Seeding

Seeding after completion is optional, per the `keepSeeding` policy.

```text
Queued → Downloading → Completed → (keepSeeding ? Seeding : StopSeeding) → Stopped
                                                         ↘ Error
```

- `keepSeeding = true`: the torrent stays in `Seeding`, visible in the downloads
  list with ratio/upload, and the operator can **Stop seeding** at any time.
- `keepSeeding = false`: seeding is stopped automatically after completion.

Each torrent **persists**: info hash, name, catalog id, state, `keepSeeding`, and
the `files/` save path. Live progress, download/upload speed, ratio, and ETA are
in-memory engine values broadcast over SignalR, not database columns; only state
transitions are written, and a transition is what triggers downstream actions
(see [Storage and data](storage-and-data.md)).

## Organizer (hardlink)

Because each catalog keeps `files/` and `library/` on one volume, the organizer
uses a **single hardlink-based strategy** with no data copying or moving:

1. Pick the relevant playable **video** media file(s) from the completed download
   (skip samples and junk; a season pack yields several source files). v1 handles
   only plain video files: archived or multi-part releases (`.rar`/`.r00`/`.zip`)
   are **not** extracted, and non-video payloads are ignored.
2. Ensure each selected source file is assigned to a movie or episode.
3. Create hardlinks under `library/` with clean names derived from the confirmed
   metadata and preserving the original extension (see [Catalogs](catalogs.md)).
4. Seed (when `keepSeeding`) directly from the `files/` copy.

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

## Removal Semantics

Because `files/` and `library/` are hardlinks to the same data, deletion is two
distinct actions:

- **Remove download** (`DELETE /api/torrents/{id}`, the `deleteFiles` option):
  stops the torrent and removes its `files/` entry (the seed copy). The published
  library item is unaffected — its `library/` hardlink keeps the data alive — so
  **disk space is not freed**, because the data is still referenced by `library/`.
- **Delete library item** (a catalog action, not a torrent action): removes the
  `library/` entry. Space is freed only once the **last** hardlink to the data is
  gone (both `files/` and `library/` removed).

So removing a download never deletes watchable content, and freeing disk space
requires removing the library item too. The UI must make this distinction explicit
to avoid the counter-intuitive expectation that removing a torrent frees space.

## Remapping

Remapping is a metadata/source assignment operation, not a raw filesystem rename.
When an operator corrects a movie or episode assignment, the app updates the
source-file mapping, removes or replaces the previous clean hardlink, creates the
new clean hardlink, and re-runs downstream probe/enrich/publish work as needed.

## Open Questions

- Should v1 keep long-lived torrent state for seeding, torrent refresh, and
  incremental torrent updates that add new files into an existing torrent folder?
  Recommendation: keep the raw `files/` + clean hardlink architecture now because
  it supports seeding and future refresh without a storage migration, but defer
  torrent refresh/delta-update behavior until the base ingest path is working.

- Should v1 support non-torrent manual file imports?
  Recommendation: defer. Keep v1 centered on torrent intake, source-file mapping,
  and clean hardlink publication.

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
downstream pipeline activity. Live progress, speed, ratio, and ETA are streamed
from the engine's in-memory state and are **not persisted**; only state
transitions are written to the database (see [Storage and data](storage-and-data.md)).

## Testing Expectations

Backend tests should use xUnit and Imposter. Required coverage:

- Add from magnet links and `.torrent` files with a chosen catalog.
- Pause, resume, stop-seeding, delete, and error-state transitions.
- `keepSeeding` policy resolution (per-torrent override of catalog default).
- Organizer hardlink creation, extension preservation, source-file mapping, and
  season-pack handling.
- Post-publish remap rebuilds clean hardlinks without modifying unrelated source
  files.
- Stop-seeding unlink preserves library data (link-count behavior).
- Save path validation against catalog `files/` roots.
- Free-space pre-check refuses oversized `.torrent` downloads and notifies for
  magnets.
- Progress/speed/ratio are not persisted; only state transitions are written and
  trigger downstream actions.
- Progress and state events published through the application event model.
