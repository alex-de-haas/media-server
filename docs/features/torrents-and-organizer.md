# Torrents and Organizer

Status: Implemented
Created: 2026-06-15
Updated: 2026-06-21

## Description

Media Server downloads torrents with MonoTorrent as a background service, then
runs each completed file through a single ingest pipeline that identifies it,
**moves it into the catalog's canonical layout**, probes it, enriches metadata,
and publishes it. Torrent state, progress, and seeding status are streamed to the
UI in real time over SSE.

> **Model change (2026-06-21):** the previous design kept two on-disk trees per
> catalog — `files/` (the seed copy) and `library/` (clean hardlinks) — sharing
> inodes via hardlinks so a torrent could seed indefinitely while the library
> showed clean names. That is gone. There is now **one** tree: published media
> lives in canonical folders **directly at the catalog root**, and downloads use a
> transient `.incoming/` staging directory. No hardlinks, no `files/`, no
> `library/`. Seeding exists **only during the download stage**. This is a
> backward-incompatible change: there is no migration — existing databases and
> on-disk layouts are discarded.

## On-Disk Layout

Each catalog root contains:

```text
<catalog.root>/
  .incoming/              # transient: in-flight torrent data + seed copy
    <downloadId>/...      # one subfolder per active download
  Movies/                 # published, canonical (movie catalogs)
    Title (Year)/Title (Year).mkv
  <Show> (Year)/          # published, canonical (series/anime catalogs)
    Season 01/<Show> S01E01.mkv
```

- `.incoming/` is the only transient area. A file lives there while it downloads
  and (optionally) seeds. The moment the pipeline advances past download, the
  chosen playable file is **moved** out of `.incoming/` into its canonical path;
  the rest of that download's `.incoming/<downloadId>/` folder is deleted.
- Everything outside `.incoming/` is the durable library and is the **only**
  subtree the read/scan/Jellyfin surfaces expose. A file is "in the library" iff a
  published `MediaSource` row points at it — the distinction is database state, not
  a folder name.
- One filesystem per catalog root (a move within it is atomic and zero-copy).

## Torrent Engine

MonoTorrent runs as a hosted service. Capabilities:

- Magnet links and `.torrent` files.
- Pause, resume, stop.
- Sequential download (for future partial-streaming use cases).
- Per-download directory under the catalog's `.incoming/<downloadId>/`.
- Per-torrent and global speed limits (`TORRENT_MAX_DOWNLOAD_SPEED`,
  `TORRENT_MAX_UPLOAD_SPEED`).

At add time the operator selects a **catalog** (not a raw path). The catalog
resolves the staging directory (`<catalog.root>/.incoming/`), the type used for
parsing, the naming template, and the default seeding policy. A per-torrent
`keepSeeding` flag overrides the catalog default.

Before starting, the engine checks free space on the catalog volume against the
torrent size. For `.torrent` files (size known up front) a download larger than
free space is **refused**. For magnet links the size is unknown until metadata
arrives, so the check runs after start and surfaces a **notification** if the
content will not fit.

## Networking

The torrent engine needs inbound connectivity for good peer performance and for
fetching magnet metadata via DHT. It is a raw TCP/UDP listener, not an HTTP
endpoint that Hosty proxies, and BitTorrent's connection churn collapses under the
docker bridge NAT (and, on Docker Desktop/WSL2, the VM network relay), so the `api`
service runs in **host networking** mode (`network: "host"` in the manifest). The
container shares the host's network namespace — no NAT, no per-port publishing — and
the engine binds the injected `HOSTY_PORT_TORRENT` (default `6881`) directly on the
host for both TCP and UDP. Router port-forwarding remains the operator's
responsibility; on Windows/WSL2 the host also needs WSL2 mirrored networking (Core
warns when it does not). See [Hosty runtime app](hosty-runtime-app.md) and the
docker-host **Host networking** feature.

- Fixed listen port from the injected `HOSTY_PORT_TORRENT` (TCP + UDP).
- **DHT** enabled (required to fetch metadata for magnet links without trackers),
  plus **PEX** and **LSD** for peer discovery.
- **Protocol encryption (MSE/PE)** enabled to reduce ISP throttling.
- Optional **UPnP / NAT-PMP** automatic port mapping
  (`TORRENT_ENABLE_PORT_MAPPING`, default on); when the router does not support
  it, the operator forwards the port manually.
- Optional bind to a specific interface/address (`TORRENT_BIND_ADDRESS`) for VPN
  setups.

## Pipeline

The ingest pipeline is a single ordered set of stages driven per `IngestItem`. It
has **two entry points** that converge at **identify**:

1. **Torrent add** — the full pipeline, starting at `intake`/`download`.
2. **Catalog scan** — for files already present in the catalog root (a hand-copied
   collection, or content imported out of band). Scan creates an `IngestItem`
   **starting at identify**, pointing at the existing file. There is no download,
   no seeding, and the file is moved/renamed into canonical form in place.

Stages (skipped individually via `IngestItem.StagesCompleted` for resume):

| Stage | Torrent entry | Scan entry | What it does |
| --- | --- | --- | --- |
| `intake` | ✓ | – | Ensure catalog layout exists. |
| `download` | ✓ | – | Wait for the torrent. While `keepSeeding`, the item **parks here** (file stays seedable in `.incoming/`) until the operator stops seeding. |
| `identify` | ✓ | ✓ (entry) | Parse name → provider search → create/reuse `MediaItem`. Low-confidence → `NeedsReview`. |
| `organize` | ✓ | ✓ | **Move** each file from its current path to the canonical root path derived from the confirmed metadata (rename in place — no copy, no hardlink). Several files mapped to one item (e.g. a black-and-white and a regular cut of an episode) get distinct version-tagged names. |
| `probe` | ✓ | ✓ | ffprobe each file in place → one `MediaSource` (+ `MediaStream`s) per file; multiple sources surface as selectable versions. |
| `enrich` | ✓ | ✓ | Fetch/cache provider metadata + images. |
| `publish` | ✓ | ✓ | Assign the stable public id; the item becomes browsable/playable. |

### The download → identify hand-off

A `Download` is a **transient transfer object**. It exists only while the torrent
is downloading or seeding. When the pipeline advances from `download` to
`identify`:

- The torrent is stopped (seeding ends).
- The `Download` row is **deleted**. Ownership of the file transfers to the
  `IngestItem`: `SourceFile` rows are owned by the `IngestItem` (not the download)
  and their path becomes catalog-root-relative (initially `.incoming/...`).

So after download there is no torrent and no seeding — just a file in `.incoming/`
attached to an ingest, moving through identify → organize → publish.

### Seeding

Seeding is bound to the download stage and is mutually exclusive with being in the
library:

- `keepSeeding = false` (default per catalog, overridable per torrent): the moment
  the download completes, the pipeline advances to identify (torrent stopped).
- `keepSeeding = true`: the ingest **parks at the download stage** and the torrent
  keeps seeding from `.incoming/`. The item is **not yet in the library**. When the
  operator clicks **Stop seeding**, the ingest advances to identify and the rest of
  the pipeline runs.

This is the deliberate trade-off of the single-tree model: you can seed a fresh
grab, or have it in the library, but not both at once. (Private-tracker users who
must hold ratio keep `keepSeeding` on and accept the title is library-visible only
after they stop seeding.)

## Identify

When a file enters identify (post-download, or via scan):

- The file name (and, for torrents, the torrent name as a fallback) is parsed.
- For movie catalogs, the file maps to one movie; for series/anime catalogs, to
  one concrete episode.
- High-confidence matches are accepted automatically and create/reuse the
  canonical `MediaItem` hierarchy (series → season → episode for episodes).
- Low-confidence matches park the item at `NeedsReview`; the operator confirms a
  match (or remaps later). Identify is idempotent — re-running reuses items by
  identity.

## Organize (move/rename)

Because there is one tree on one filesystem, organize is a **move**, not a
hardlink:

1. Skip non-playable payload (samples/junk; archives are not extracted in v1).
2. For each assigned playable file, build the canonical catalog-root-relative path
   from the confirmed metadata (movie template, or `Show/Season NN/Show SxxEyy`),
   preserving the original extension (the container is never changed — playback is
   Direct Play/Stream only).
3. **Move** the file there. For torrent items the source is `.incoming/...`; for
   scanned items the source is wherever it currently sits in the root. The
   `SourceFile` path and the `MediaItem.LibraryPath` are updated to the canonical
   path.
4. After a torrent's playable files are moved out, delete its now-stale
   `.incoming/<downloadId>/` staging folder.

A move within one filesystem is atomic and frees no extra space (one copy exists
throughout). If a destination already exists from a prior run, organize is
idempotent (re-derives the same path; replace if needed).

## Removal Semantics

With one tree, deletion is simple — every file backs exactly one item (an item may
have more than one file when it carries alternate versions):

- **Remove from library** (`DELETE /api/library/{id}`, `deleteFiles` option):
  removes the DB rows. With `deleteFiles = true` it also deletes the canonical
  file(s) from disk (freeing space). With `deleteFiles = false` the file stays on disk
  (orphaned) and a later **scan** can re-import it.
- **Remove download** (`DELETE /api/torrents/{id}`) only applies while a download
  exists (download/seeding stage). It stops the torrent and clears its
  `.incoming/` data and in-flight ingest. After the download→identify hand-off
  there is no download to remove — the item is governed by library removal.

There is no more "removing a torrent leaves watchable content via the other
hardlink" subtlety: once published the canonical file(s) are governed by library
removal.

## Remapping

Remapping corrects a movie/episode assignment after publish. It updates the
source-file → `MediaItem` mapping, **moves/renames** the canonical file to match
the new identity (no hardlink rebuild), prunes the now-orphaned old item, and
re-runs downstream probe/enrich/publish as needed.

## Library Scan (import)

A per-catalog **Scan** action (Catalogs page, admin) lets operators onboard files
that were not downloaded through the app — e.g. an existing collection copied into
the catalog root.

- Scan enumerates the catalog root (excluding `.incoming/`) for playable media
  files that have **no** published `MediaSource` row.
- For each orphan it creates an `IngestItem` **at the identify stage** pointing at
  the file. The normal pipeline tail runs: identify → organize (rename into
  canonical form) → probe → enrich → publish.
- Confident matches publish automatically; low-confidence files park at
  `NeedsReview` for the operator. Already-published files are skipped (idempotent).
- Imported files are indistinguishable from torrent-published ones afterwards: a
  canonical file + `MediaItem` + `MediaSource`, with no `Download`. (This is the
  same end state a torrent reaches after the download→identify hand-off.)

Because import publishes the file in place (then renames to canonical), the
operator's original file becomes the library file. Deleting such an item with
`deleteFiles = true` deletes that original — the UI surfaces this.

## API Endpoints

Internal endpoints (under `/api`, behind Host identity):

```text
POST   /api/torrents/add           # { source, catalogId, keepSeeding? }
POST   /api/torrents/{id}/pause
POST   /api/torrents/{id}/resume
POST   /api/torrents/{id}/stop-seeding   # advances a parked, seeding ingest into identify
DELETE /api/torrents/{id}
GET    /api/torrents
POST   /api/catalogs/{id}/scan     # import orphan media files under the catalog root
```

## Real-Time Updates

The SSE stream broadcasts progress, speed, ratio, and state/seeding changes plus
downstream pipeline stage transitions. The client subscribes once and receives
updates for all active torrents and ingests. Live progress, speed, ratio, and ETA
are streamed from the engine's in-memory state and are **not persisted**; only
state transitions are written to the database (see
[Storage and data](storage-and-data.md)).

## Testing Expectations

Backend tests should use xUnit. Required coverage:

- Add from magnet links and `.torrent` files with a chosen catalog; download lands
  under `.incoming/`.
- Pause, resume, stop-seeding, delete, and error-state transitions.
- `keepSeeding` policy resolution (per-torrent override of catalog default) and the
  "park at download while seeding" behavior.
- The download→identify hand-off deletes the `Download` and re-parents source files
  to the ingest.
- Organize **moves** the file to the canonical path (extension preserved,
  source-file mapping, season-pack handling) and clears the `.incoming/` staging
  folder.
- Post-publish remap moves/renames the canonical file without touching unrelated
  files.
- Catalog scan imports orphan root files (confident → published, low-confidence →
  review, already-published → skipped) and never touches `.incoming/`.
- Library removal with/without `deleteFiles` (file deleted vs left for re-scan).
- Free-space pre-check refuses oversized `.torrent` downloads and notifies for
  magnets.
- Progress/speed/ratio are not persisted; only state transitions are written.
