# Torrents and Organizer

Status: Implemented
Created: 2026-06-15
Updated: 2026-06-24

## Description

Media Server downloads torrents via the external, VPN-isolated `torrent-engine` app, then
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

Downloading is delegated to the external `torrent-engine` app — a **required**
cross-app dependency that runs the BitTorrent client (MonoTorrent) VPN-isolated in
its own container. Media Server drives it over the app's HTTP control API + SSE
stream through `RemoteTorrentEngine` (the `ITorrentEngine` abstraction), discovered
via the injected `HOSTY_DEPENDENCY_TORRENT_ENGINE_URL`. See
[Torrent engine app](../ideas/torrent-engine-app.md).

Engine capabilities (driven through `ITorrentEngine`):

- Magnet links and `.torrent` files.
- Pause, resume, stop.
- Per-download directory under the catalog's `.incoming/<downloadId>/`, handed to
  the engine as a `mountLabel` + relative path against its matching downloads mount
  (so the post-download move stays on one filesystem — see the multi-mount note below).
- Per-download speed limits forwarded from `TORRENT_MAX_DOWNLOAD_SPEED` /
  `TORRENT_MAX_UPLOAD_SPEED`.

`.torrent` parsing (info hash, size, file list) runs **locally** in Media Server
(`LocalTorrentInspector`) before a download row is created — it needs no network.

At add time the operator selects a **catalog** (not a raw path). The catalog
resolves the staging directory (`<catalog.root>/.incoming/`), the type used for
parsing, the naming template, and the default seeding policy. A per-torrent
`keepSeeding` flag overrides the catalog default.

Before delegating to the engine, Media Server checks free space on the catalog
volume against the torrent size. For `.torrent` files (size known up front) a
download larger than free space is **refused**. For magnet links the size is unknown
until metadata arrives, so the check runs after start (in the coordinator) and
surfaces a **notification** if the content will not fit.

**When the dependency is not configured** (e.g. dev without the engine), Media Server
registers a `DisabledTorrentEngine`: downloading is unavailable (add returns a clear
error) but the rest of the app — Jellyfin surface, library browsing, identify / probe
/ enrich — keeps working. This matches Hosty's advisory, non-blocking dependency model.

## Networking

Media Server holds **no** raw BitTorrent port — it speaks only the engine app's HTTP
control API. All peer connectivity (the fixed TCP/UDP listen port, DHT/PEX/LSD, MSE
encryption, port mapping) and the VPN tunnel + killswitch live in the `torrent-engine`
app and are configured there. This also sidesteps the docker bridge-NAT throughput
collapse that plagued the old in-process engine, by tunnelling all peer connections
through a single VPN flow. See [Torrent engine app](../ideas/torrent-engine-app.md).

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
| `organize` | ✓ | ✓ | **Move** each file from its current path to the canonical root path derived from the confirmed metadata (rename in place — no copy, no hardlink). Several files mapped to one item (e.g. a black-and-white and a regular cut of an episode) get distinct version-tagged names. See *Version collisions* below. |
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
- **External audio tracks** (`.mka`, `.ac3`, `.eac3`, `.dts`, `.flac`, `.aac`,
  `.opus`, `.mp3` — see `MediaFormats.AudioExtensions`) are admitted alongside the
  videos but never searched against the provider. After the videos resolve, each
  track matches the batch's own items: the single movie for a movie batch,
  otherwise the episode whose number the track's file name parses to (the season
  disambiguates when two seasons share a number). An unplaceable track parks the
  batch — in review the operator matches it to its episode (Extra is rejected for
  audio: it would publish an item with no playable source) or skips it.

## External audio tracks (mux)

Releases often ship dubs as separate per-episode audio files (a "Rus Sound"
folder of `.mka`s next to the episodes). Playback clients that direct-play one
container (Infuse plays `MediaSources[0]` as a single file) would never see them,
so before Organize the `Mux` stage merges each matched track into its video:

- A **stream-copy ffmpeg remux** (`-map 0 -map i:a -c copy`) into Matroska — no
  re-encode, I/O bound, run in-process (not on the transcode-engine). `-map 0`
  keeps everything the video already has (subtitles, chapters, attached fonts);
  only audio streams are taken from the track files (never an mp3's cover art).
  The staged video's extension becomes `.mkv`; Probe later sees all tracks in the
  one file, so nothing downstream changes.
- Appended streams keep their own language/title tags when present; untagged
  streams get a language inferred from unambiguous path tokens ("Rus Sound",
  `…rus.mka` → `rus`) and the dub folder's name as the track title
  (`AudioTrackLabeler`).
- Consumed audio rows flip to `Merged` (terminal, like `Skipped`) — persisted per
  item, so a re-driven stage never appends the same tracks twice. The freed audio
  files are swept with the `.incoming/` staging leftovers.
- Only staged (torrent) files are muxed — a scan-imported file is the operator's
  own library file and is never rewritten. A dub-only batch (tracks matched to
  episodes with no video in the ingest) logs a warning and discards the tracks:
  merging into already-published library files is a separate feature.
- The ffmpeg binary comes from `FFMPEG_PATH`, falling back to a PATH lookup (the
  Docker image installs the full ffmpeg package for ffprobe already). A mux
  failure parks the item as a retryable failure with the ffmpeg error.

## Organize (move/rename)

Because there is one tree on one filesystem, organize is a **move**, not a
hardlink:

1. Skip non-playable payload (samples/junk; archives are not extracted in v1).
2. For each assigned playable file, build the canonical catalog-root-relative path
   from the confirmed metadata (movie template, or `Show/Season NN/Show SxxEyy`),
   preserving the file's extension (organize never changes the container — playback
   is Direct Play/Stream only; a video that had external audio muxed in arrives
   here already as `.mkv`).
3. **Move** the file there. For torrent items the source is `.incoming/...`; for
   scanned items the source is wherever it currently sits in the root. The
   `SourceFile` path and the `MediaItem.LibraryPath` are updated to the canonical
   path.
4. After a torrent's playable files are moved out, delete its now-stale
   `.incoming/<downloadId>/` staging folder.

A move within one filesystem is atomic and frees no extra space (one copy exists
throughout). If a destination already exists from a prior run, organize is
idempotent (re-derives the same path; replace if needed).

### Version collisions

Version labels normally come from `EditionLabeler`, which diffs the names of files that share one
ingest. A **scan** queues one ingest per file, so files that identify as the same item never meet in
one group and each would derive the same unlabelled canonical name — the second one organized would
rename over the first. Two rules keep that from destroying media:

- **Recover the label from the name.** A file already sitting at `<canonical stem> - <label>.<ext>`
  is already canonical for that edition, so the organizer adopts `<label>` and leaves it in place
  instead of renaming it onto the plain canonical name. This is the exact suffix `LibraryNaming`
  writes and transcode-engine emits, read back. A title that itself contains `" - "` is unaffected:
  its canonical stem carries the hyphen and matches exactly.
- **Never overwrite a claimed path.** The organizer refuses to move onto a path that backs a
  published `MediaSource` *or* that another ingest's `SourceFile` still owns — the latter is what a
  scan over a pre-existing library looks like before anything reaches probe, when `MediaSources` is
  still empty. A refused file also pins its `.incoming/` staging root against the recursive cleanup,
  which would otherwise delete the file the refusal just preserved.

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

## Moving Between Catalogs

Moving relocates a published movie/series into another type-compatible catalog. It
moves the file(s) into the target catalog's canonical layout and repoints every
durable row (`MediaItem`/`MediaSource`/`SourceFile`/`IngestItem`), re-minting
`PublicId` (the Jellyfin id changes). When the target catalog has no such identity
the rows are re-pointed as-is (internal id preserved, so `UserData`/metadata
survive); when it already holds the identity the sources merge onto the existing
item as extra versions and the source rows are pruned. A same-volume move is an
atomic rename; a cross-volume move runs as a background job that copies then deletes
the source, with a free-space pre-check. See
[File and directory management](file-directory-management.md#move-semantics).

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
