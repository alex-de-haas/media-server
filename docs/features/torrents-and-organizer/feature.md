# Torrents and Organizer

Created: 2026-06-15
Updated: 2026-09-24

## Description

Media Server downloads torrents via the external, VPN-isolated `torrent-engine` app, then
runs each completed file through a single ingest pipeline that identifies it,
**places it in the catalog's canonical layout**, probes it, enriches metadata,
and publishes it. Torrent state, progress, and seeding status are streamed to the
UI in real time over SSE.

Media Server owns each catalog's `.incoming/<downloadId>` directory and gives its
path to the generic Torrent Engine. Import and seed retention are independent:
retained torrents keep the original tree and publish independent library copies;
other torrents are stopped and removed from the engine before files are moved.
There are no hardlinks and no separate Torrent Engine UI or ownership API.

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

- `.incoming/` holds downloading data and retained seed originals. Organize copies
  required videos when seeding is retained, or moves them after engine release.
  Companion tracks follow the same policy. Cleanup waits for required placements.
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
[Torrent engine app](../../ideas/torrent-engine-app.md).

Engine capabilities (driven through `ITorrentEngine`):

- Magnet links and `.torrent` files.
- Pause, resume, stop.
- Per-download directory under the catalog's `.incoming/<downloadId>/`, handed to
  the engine as a `mountLabel` + relative path against its matching downloads mount
  (so the post-download move stays on one filesystem — see the multi-mount note below).
- Rate limiting is the engine's own concern: Media Server sends no per-download
  limits, so `torrent-engine` applies its global `TORRENT_MAX_DOWNLOAD_SPEED` /
  `TORRENT_MAX_UPLOAD_SPEED` settings.

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
through a single VPN flow. See [Torrent engine app](../../ideas/torrent-engine-app.md).

### Engine health indicators

Two engine-wide signals are surfaced in the Activity header, next to each other,
because both are shared by every download rather than being per-torrent. Each is
seeded from a `GET` on mount and then kept live by an SSE event; each is `null` when
no engine reports it, and the UI hides the indicator entirely in that case.

| Signal | Seed | Event | Null when |
| --- | --- | --- | --- |
| VPN tunnel | `GET /api/vpn` | `vpnStatusChanged` | downloading is disabled (`DisabledTorrentEngine`) |
| DHT health | `GET /api/dht` | `dhtStatusChanged` | downloading is disabled, or the engine predates `torrent-engine` 0.7.0 and has no `/dht` |

`DhtStatus` is `{ enabled, running, state, nodeCount }`. It exists to separate three
situations that otherwise look identical from the outside:

- **off** — `enabled: false`. Deliberate configuration, shown muted.
- **idle** — `enabled` but not `running`. The engine is recycled while nothing is
  downloading, so DHT is simply not up. The badge is hidden: "idle" only repeats what
  an empty activity list already says.
- **enabled but not working** — `running` with `state: "NotReady"`. DHT is on but
  never found a peer, so magnet links without trackers quietly fail to resolve. This
  is the signal the indicator exists for, shown as a warning.

`state` is MonoTorrent's own value, and `Initialising` is a **healthy** start-up — the
failure case is `NotReady` specifically. Deriving it as `state != "Ready"` would flag
every bootstrap as broken.

`RemoteTorrentEngine` does not fan out every DHT update: `nodeCount` churns as the
routing table grows, so it is only reported when it crosses into or out of empty (an
empty table while running being exactly what a broken DHT looks like).

#### VPN profile picker

`VpnStatus` carries the engine's OpenVPN **profile trio** (`torrent-engine` 0.8.0+;
`null` against an older engine, which does not send it): `profile` — the profile the
engine runs, `pendingProfile` — the one a switch is moving to, `lastError` — why the
last start or switch failed. The pill reads `VPN · <profile> · <exit country>` while
up, `VPN · switching…` (amber) while a switch is in flight, and `VPN off` while down;
the tooltip adds the exit IP, the tunnel address, and the last error. The presentation
rules live in `@/lib/vpn` (`vpnKind` / `vpnLabel` / `vpnTooltip`).

For an **admin** the pill is a native button that opens a menu by click, Enter, or
Space; Escape closes the menu and returns focus to the button. Opening it lists the engine's profiles
(`GET /api/vpn/profiles`, fetched fresh on every open because the engine lists its
folder live) with the active one checked and disabled, and choosing another sends
`PUT /api/vpn/profile`. The engine only records the choice and answers `202` with its
*current* status; the switch itself arrives over `vpnStatusChanged` — `pendingProfile`
first, then the new `profile` once the tunnel is back — and every item is disabled
while one is pending. A user sees the indicator alone: where every download's traffic
exits is operator configuration, so both endpoints are admin-only. When the engine
reports no profiles (`null`: downloading disabled, or an engine without
`/vpn/profiles`) the menu says so instead of listing nothing; a refusal (an unknown
id, an engine without switching) surfaces the engine's own message as a toast.

`RemoteTorrentEngine` fans a VPN status out when connectivity, the tunnel address, the
exit IP/country, or the profile trio changes — never on `checkedAt` alone, which ticks
on every engine poll.

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
| `download` | ✓ | – | Wait for engine-confirmed completion, including rechecks after restart. Release non-seeding jobs with files retained; keep the durable Download owner. |
| `identify` | ✓ | ✓ (entry) | Parse name → provider search → create/reuse `MediaItem`. Low-confidence → `NeedsReview`. |
| `organize` | ✓ | ✓ | Copy or move each file to its canonical path derived from confirmed metadata. Copies use private temporary output, completed-byte verification and promotion. Several files mapped to one item (e.g. a black-and-white and a regular cut of an episode) get distinct version-tagged names. See *Version collisions* below. |
| `probe` | ✓ | ✓ | ffprobe each file in place → one `MediaSource` (+ `MediaStream`s) per file; multiple sources surface as selectable versions. |
| `enrich` | ✓ | ✓ | Fetch/cache provider metadata + images. |
| `publish` | ✓ | ✓ | Assign the stable public id; the item becomes browsable/playable. |

### Download ownership, policy and retention

`Download` persists the original staging root, info hash, source URI, policy, stop
intent, engine-release acknowledgement and cleanup attempts independently of
canonical `SourceFile.RelativePath`. `OriginalRelativePath` records the torrent
path; engine metadata events update existing files after they have moved.
The remote client uses the registered torrent's `/files` response for physical
save-relative paths, including after an idempotent add on restart. Add/inspection
descriptor paths do not enter that cache: older engines can prefix a single-file
torrent's name twice there, and a late add response must not overwrite live paths.

The add dialog inherits the catalog default unless explicitly overridden.
Downloading and paused Activity cards show an icon toggle alongside the other
right-aligned actions. Its on/off icon and tooltip reflect the current seeding
policy; the tooltip explains the next click. Clicking toggles the policy; it is
disabled while saving and once file placement starts. The toggle also supports
keyboard activation.
`PUT /api/torrents/{id}/seeding-policy` changes the policy before placement starts.
The pipeline, operator mutations and engine events share a writer gate; a policy
change cannot switch an active file transfer. Retargeting staging to another
catalog releases the engine first and ends seed retention.

With `keepSeeding=true`, completed downloads pass through Identify, Organize,
Probe, Sidecars, Enrich and Publish while the original tree continues seeding.
Samples, skipped files and other torrent payload stay intact. Published seeds
remain in Activity's Active tab with completed stages, upload statistics, retained
bytes and **In library / Seeding**. An upload error does not unpublish the media.

**Stop seeding and remove originals** persists stop intent, awaits engine stop and
removal with `deleteFiles=false`, then cleans only owned staging and obsolete
app-owned torrent metadata. Library files and history remain. Before publication,
**Stop seeding and continue** releases the engine but preserves required originals
until processing completes. Restart does not re-add torrents with stop intent.
A lost successful removal reply is reconciled through idempotent removal, including
an already-missing torrent. Unavailable engines cannot acknowledge release.

### Capacity recovery

Organize checks the remaining copy space on the destination volume. Insufficient
space parks the ingest as `AwaitingSpace` at Organize, with required/available bytes
and exactly two recovery actions: **Retry** or **Stop seeding and continue**.
The latter retries remaining placements with Move after engine acknowledgement.
A failed stop keeps the original data and the durable handle. Move failures remain
ordinary Organize failures; a move is not guaranteed to succeed on a full disk.

Disk-full errors during copying follow the same parked flow. Private partial output
is closed and deleted, completed per-file placements survive, and no incomplete
canonical output reaches Probe. Copy digests allow a completed promotion to be
recovered after an interrupted database update without adopting unrelated output.
Failed cleanup of partial output is reported instead of silently discarded.
`AwaitingSpace` does not consume automatic attempts and survives restart until an
operator action. Copies of companions use the same transfer helper.

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

## External audio tracks and subtitles (sidecars)

Releases often ship dubs and subtitles as separate per-episode files (a "Rus
Sound" folder of `.mka`s and a "RUS Subs" folder next to the episodes). Ingest
**keeps them as files**: after Probe, the `Sidecars` stage places each matched
companion next to its library file under a canonical name and records it as an
external `MediaStream`. See
[external-track-sidecars](../external-track-sidecars/feature.md).

Ingest used to merge them into the video here instead. That was lossy — a failed
mux, an absent transcode engine, or a batch whose videos were not present
destroyed the track — so merging is now a separate operation, run later and only
when asked, and it produces a new version rather than rewriting the original.

- Companions are **not organized** by the organizer: their names derive from the
  video's canonical one, so they are placed afterwards. Cleanup protects any root
  still holding required companion tracks.
- A track's language and title come from its own container when it has tags (a
  `.mka` carries both), and from its path otherwise (`AudioTrackLabeler`: "Rus
  Sound", `…rus.mka` → `rus`; a per-group folder such as `[AniDUB]` becomes the
  title).
- Placed rows become `Sidecar` — part of the library, swept by nothing. The old
  `Merged` status is no longer produced.
- A dub-only batch (tracks matched to items with no video in the ingest) **keeps**
  its tracks where they are, rather than discarding them as it used to.

## Organize (copy or move)

Organize names each assigned playable file using confirmed metadata and reserves
its destination. A retained seed uses an independent copy; other files use
`File.Move` without overwrite. Within one filesystem this keeps the rename fast
path. The extension and container remain unchanged.

Each successful placement updates `SourceFile` and `MediaItem.LibraryPath` before
the next file starts. Retrying preserves the chosen destination and edition;
completed files do not become new versions. The original path stays recorded.
A move interrupted between filesystem success and its database update fails safely
if its source is missing; broader automatic recovery of such moves remains outside
this feature. Byte count alone is not treated as proof of content integrity.

### Version collisions

Version labels normally come from `EditionLabeler`, which diffs the names of files that share one
ingest. Separate downloads of the same season and scans (one ingest per file) also produce distinct
versions in the canonical library folder:

- **Recover the label from the name.** A file already sitting at `<canonical stem> - <label>.<ext>`
  keeps its label and path. A title that itself contains `" - "` is unaffected. Retries retain the
  edition already assigned to each source file, including batches with multiple versions.
- **Allocate a free path.** If the canonical destination exists on disk or is claimed by another
  `MediaSource` or `SourceFile` in the catalog, the newcomer gets ` - Version 2`, ` - Version 3`,
  and so on. An existing edition gets a numeric suffix instead (for example ` - HDR 2`). Missing
  files' database claims and untracked files on disk also reserve their names. Comparisons follow
  the filesystem's case rules, including Unicode. Database claims are queried only for candidate
  destinations not already occupied on disk and cached by path within the batch; in-place retries
  do not load claims. The original file is never overwritten or renamed; each new version's
  `SourceFile` and probed `MediaSource` point at its actual library file.
- **Require a successful move before publishing.** Organize fails if an assigned playable file is
  missing or cannot be organized, and keeps staging roots containing unorganized playable files.
  Probe also rejects files still under `.incoming/` or missing on disk. Temporary paths do not become
  published versions that disappear when completed ingest history is cleared.

## Removal Semantics

With one tree, deletion is simple — every file backs exactly one item (an item may
have more than one file when it carries alternate versions):

- **Remove from library** (`DELETE /api/library/{id}`, `deleteFiles` option):
  removes the DB rows. With `deleteFiles = true` it also deletes the canonical
  file(s) from disk (freeing space). With `deleteFiles = false` the file stays on disk
  (orphaned) and a later **scan** can re-import it.
- **Remove part of a series** (`DELETE /api/library/episodes/{id}`,
  `DELETE /api/library/seasons/{id}`, same `deleteFiles` option): the same removal for
  one episode or one whole season, pruning the containers it empties. See
  [File and directory management](../file-directory-management/feature.md#removal-semantics).
- **Remove download** (`DELETE /api/torrents/{id}`) cancels unpublished work. Stop
  and remove are acknowledged before staging deletion, and failures retain the
  ownership record with retryable cleanup state. Published seeds require the
  explicit stop-seeding action before their Activity history can be removed.
- **Clear completed Activity** excludes every retained download, including cleanup
  failures. History deletion never recursively deletes legacy staging inferred
  only from source paths.

### Temporary download cleanup

Settings exposes an administrator-only **Temporary download files** section backed
by `GET /api/settings/temporary-downloads/` and
`POST /api/settings/temporary-downloads/clean` (`ids`). Analysis reports sizes,
ownership, purpose and eligibility. Preview lists the exact selected directories;
apply revalidates current ownership, use, root containment and link protection.
Active downloads, retained seeds, review and required staged files are protected.
Unknown legacy directories are report-only; their age, name or absence from Activity
is not deletion authority.

Cleanup failures remain in `Download` with an error, attempt count and next attempt.
The worker retries up to five times with bounded backoff; explicit settings cleanup
resets exhausted attempts. Owned leftover files such as `.nfo` are removed only
after required processing finishes. A cleanup failure does not invalidate published
media. All root deletion is preceded by acknowledged engine release.

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
[File and directory management](../file-directory-management/feature.md#move-semantics).

## Library Scan (import)

A per-catalog **Scan** action (Settings → Catalogs tab, admin) lets operators onboard files
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
  import state a torrent reaches while its retained download owns staging separately.)

Because import publishes the file in place (then renames to canonical), the
operator's original file becomes the library file. Deleting such an item with
`deleteFiles = true` deletes that original — the UI surfaces this.

## API Endpoints

Internal endpoints (under `/api`, behind Host identity):

```text
POST   /api/torrents/add           # { source, catalogId, keepSeeding? }
POST   /api/torrents/{id}/pause
POST   /api/torrents/{id}/resume
POST   /api/torrents/{id}/stop-seeding   # release seed; preserve required originals or clean published staging
PUT    /api/torrents/{id}/seeding-policy # change keepSeeding before placement starts
GET    /api/settings/temporary-downloads/ # analyze owned and unknown staging (admin)
POST   /api/settings/temporary-downloads/clean # revalidate and clean selected ids (admin)
DELETE /api/torrents/{id}
GET    /api/torrents
GET    /api/vpn                    # engine-wide VPN tunnel status (null when downloading is disabled)
GET    /api/vpn/profiles           # the engine's OpenVPN profiles + the active one (admin; null when unavailable)
PUT    /api/vpn/profile            # { id } → 202 + the current status; the switch arrives over SSE (admin)
GET    /api/dht                    # engine-wide DHT health (null when unavailable)
POST   /api/catalogs/{id}/scan     # import orphan media files under the catalog root
```

## Real-Time Updates

The SSE stream broadcasts progress, speed, ratio, and state/seeding changes plus
downstream pipeline stage transitions. The client subscribes once and receives
updates for all active torrents and ingests. Live progress, speed, ratio, and ETA
are streamed from the engine's in-memory state and are **not persisted**; only
state transitions are written to the database (see
[Storage and data](../storage-and-data/feature.md)).

Alongside the per-torrent frames it carries the two engine-wide health events,
`vpnStatusChanged` and `dhtStatusChanged` (see
[Engine health indicators](#engine-health-indicators)). Neither is persisted — both
are live indicators only.

## Testing Expectations

Backend tests should use xUnit. Required coverage:

- Add from magnet links and `.torrent` files with a chosen catalog; download lands
  under `.incoming/`.
- Pause, resume, stop-seeding, delete, and error-state transitions.
- Catalog-default and explicit seeding policy, placement cutoff, completion and control races.
- Remote add and restart use registered file paths; failed file-list refreshes never
  publish provisional descriptor paths or create phantom single-file duplicates.
- Publication with independent copies while original videos, companions and incidental files remain seedable.
- Engine stop/remove acknowledgement precedes Move and cleanup; failed or lost replies retain ownership.
- Capacity parking before and during Copy; no Probe or automatic retry exhaustion. Retry and partial Copy-to-Move fallback preserve completed paths and version names.
- Copy cancellation and restart retain sources, remove private partial output, and revalidate completion.
- Published seed teardown preserves library/history. Cleanup failures retain durable evidence and support explicit retry.
- Cleanup preview/apply protects active, review, incomplete, linked and unknown roots; apply rechecks current state.
- Move/copy preserve extensions, source mappings and multi-version/season-pack naming. Cleanup waits for required companion work.
- Successive downloads of the same season retain distinct file contents and canonical version paths,
  including after completed-ingest cleanup; retries preserve allocated names.
- Destination collisions preserve published versions, pending-ingest files, untracked files, and
  database claims whose files are missing. Claim queries filter candidate paths in SQL, cache repeated
  candidates, and preserve Unicode case rules without loading all catalog claims. Missing or staged
  files cannot pass through to publish.
- Post-publish remap moves/renames the canonical file without touching unrelated
  files.
- Catalog scan imports orphan root files (confident → published, low-confidence →
  review, already-published → skipped) and never touches `.incoming/`.
- Library removal with/without `deleteFiles` (file deleted vs left for re-scan),
  including single-episode and whole-season removal and the container pruning that
  follows.
- DHT status fan-out: the first status is reported, `nodeCount` churn alone is not,
  crossing into or out of an empty routing table is, and a state transition
  (`Initialising` vs `NotReady`, the difference between "starting" and "broken") is.
- `dhtStatusChanged` publishes camelCase JSON carrying MonoTorrent's own state value.
- VPN status fan-out: the profile trio (`profile`, `pendingProfile`, `lastError`)
  counts as a change; `checkedAt` alone does not.
- `RemoteTorrentEngine` against a stubbed engine: `/vpn/profiles` parses, a bare
  `404` from an older engine reads as `null`, `PUT /vpn/profile` sends `{ id }` and
  relays the engine's own message on refusal (a bare `404` explains the version gap).
- `vpnStatusChanged` publishes the profile trio in camelCase.
- Web (vitest): `vpnKind` / `vpnLabel` / `vpnTooltip` for up, down and switching,
  with and without a profile, an exit, and a last error. E2E: an admin opens the
  picker by click, Enter, and Space; Escape returns focus to its native button;
  the active profile is checked and disabled, choosing another sends the
  `PUT`; a user gets the indicator without a menu.
- Free-space pre-check refuses oversized `.torrent` downloads and notifies for
  magnets.
- Torrent speed/ratio remain live; placement byte progress and cleanup lifecycle are persisted.
- Web Activity renders completed stages plus retained seeding, capacity recovery actions and cleanup errors; Settings restricts temporary cleanup to administrators.
