# Catalogs

Created: 2026-06-15
Updated: 2026-07-27

## Description

A **catalog** is an operator-configured destination for content. There can be
many catalogs. When adding a torrent, the operator picks one catalog from the
configured list; that choice drives everything downstream: filename parsing,
target paths, naming, seeding policy, and metadata language. `movie`, `series`,
and `anime` are catalog **types**, not catalogs themselves — multiple catalogs
can share a type (for example "Movies 4K", "Anime Subbed", "Series RU").

This replaces the earlier single movie/TV "library" model.

## Catalog Model

```jsonc
{
  "id": "{uuid}",
  "name": "Movies 4K",
  "type": "movie",                 // movie | series | anime
  "mountLabel": "media",           // Hosty catalog-root mount; the durable location
  "mountRelativePath": "movies-4k",// path within that mount ("" = the mount root)
  "root": "/mnt/catalogRoots/media/movies-4k",  // resolved for the current runtime
  "namingTemplate": "{Title} ({Year})",
  "defaultKeepSeeding": false,
  "metadataLanguage": null         // optional override of SUPPORTED_LANGUAGES default
}
```

- `type` drives the name parser and metadata provider, filename parsing (movie
  vs `SxxEyy` vs anime absolute numbering), the Jellyfin `CollectionType`
  (`movies` for `movie`; `tvshows` for `series` and `anime`), and the naming
  layout. `series` and `anime` differ mainly in parser/provider and episode
  ordering (aired vs absolute), not in Jellyfin collection type.
- `mountLabel` + `mountRelativePath` are where the catalog lives, and the only
  durable record of it — see [Mount Anchoring](#mount-anchoring).
- `root` is a single directory on one filesystem (see Layout below), resolved
  from the mount for the runtime the app currently runs under.
- `defaultKeepSeeding` seeds new downloads in this catalog unless overridden at
  add time (see [Torrents and organizer](../torrents-and-organizer/feature.md)).
- `metadataLanguage` optionally overrides the global default for this catalog
  (e.g. Anime → `ja`/`en`), see [Metadata](../metadata/feature.md).

## Mount Anchoring

Hosty gives the app one absolute path per catalog-root mount in
`HOSTY_MOUNT_CATALOGROOTS`, and **that path depends on the runtime profile**:
host paths under `dev` (`localCommand`), container paths under `docker`. The
mount **label** is the same in both. So a catalog's identity is its label plus
the path within that mount, and its absolute `root` is derived state:

```text
label        dev (localCommand)        docker
media        /srv/media                /mnt/catalogRoots/media
```

- Every process start re-resolves `root` from the label, after schema migrations
  and before any worker reads a catalog (`CatalogAnchorService`). Mounts arrive
  as environment and only change across a restart, so startup is the only point
  that has to re-resolve. Switching an app between runtimes therefore keeps its
  catalogs working, in either direction.
- A catalog that has no label yet but whose stored `root` still falls inside a
  mount records that label on the same pass. This is what carries catalogs
  created before anchoring existed, on the first start under the runtime whose
  paths still match.
- A catalog whose label the current runtime does not provide — the mount was
  removed or renamed, or the catalog was created under the other runtime profile
  and never anchored — is reported as **unanchored**. Its `root` is left exactly
  as stored (no path is guessed at), file-backed actions are unavailable as when
  offline, and the operator re-anchors it onto a configured mount from the
  Catalogs page (`POST /api/catalogs/{id}/anchor`). Media files are never moved
  by anchoring; only the record of where the catalog lives changes. For a
  catalog that has a label, this is decided by whether that label resolves, not
  by where its root happens to sit: a mount renamed while keeping its path
  leaves the stored label dead, and it has to be reported now rather than on the
  next runtime switch.
- Re-anchoring a catalog to a **different** directory is refused while it has a
  download the torrent engine is working on (queued, downloading, or seeding):
  the engine holds the old staging path and would keep writing there. This never
  blocks repairing an unanchored catalog, whose root is unreachable anyway.
- `mountLabel` is stored as the mount's own label and `mountRelativePath` in
  canonical form (`.`/`..` resolved away, forward slashes, no leading or
  trailing separator). Both are the catalog's identity, so one directory has to
  reduce to exactly one stored value — otherwise `films` and `movies/../films`,
  or `media` and `MEDIA`, would slip past the uniqueness check as two catalogs
  owning the same media. A path that climbs out of its mount is rejected.
- A download's staging directory (`<root>/.incoming/<downloadId>`) is derived
  from the root, so it follows the catalog whenever the root is rewritten.
- Standalone runs where Hosty injects no mounts keep free-text absolute roots
  (`mountLabel` is null) and are never reported as unanchored — there is nothing
  to be anchored to.

## On-Disk Layout

Each catalog root holds a transient `.incoming/` staging directory plus the
canonical, published media tree **directly at the root**. There is no `library/`
subtree and no hardlinking — a completed file is **moved** from `.incoming/` into
its canonical place (an atomic, zero-copy move within the one filesystem):

```text
<catalog.root>/
  .incoming/                          # transient: in-flight torrent data + seed copy
    <downloadId>/Inception.2010.1080p.BluRay.x264/Inception.2010.1080p.mkv
  Inception (2010)/
    Inception (2010).mkv              # canonical, published
```

- Media Server scans and exposes everything **except** `.incoming/`. A file is
  "in the library" iff a published `MediaSource` row points at it — the
  distinction is database state, not a folder name.
- The clean name preserves the **original file extension** (the container is
  never changed — playback is Direct Play / Direct Stream only). Resolution and
  quality are read from the file by probing, not encoded in the filename, except
  as a **version qualifier** when multiple versions of one title exist (a later
  multi-version feature). The reserved layout is
  `{Title} ({Year}) - [{Version}].<ext>` — Jellyfin groups files that share the
  base name in one folder as alternate versions of a single item (see
  [Jellyfin compatibility](../jellyfin-compatibility/feature.md)); reserving it now avoids a
  path migration later.
- Series layout: `<Show> (<Year>)/Season 01/<Show> S01E02.<ext>`.

The catalog root is a single filesystem, so the move from `.incoming/` into the
canonical tree is atomic and copies no bytes.

## Free Space

Each catalog reports the free space on its `root` volume. The UI shows this when
the operator picks a destination catalog for a download, and the engine uses it
for the pre-download space check (see
[Torrents and organizer](../torrents-and-organizer/feature.md)).

## Jellyfin Mapping

- Each catalog surfaces as a Jellyfin `CollectionFolder` with `CollectionType`
  from its `type` (`movie` → `movies`; `series` and `anime` → `tvshows`). Infuse
  shows each catalog as a separate library.
- Items map to `Movie`, `Series`, `Season`, `Episode`, or `Video` (unmatched).
- Public item IDs are stable across rescans and based on the catalog plus the
  canonical provider identity, not on physical path or database row id. See
  [Jellyfin compatibility](../jellyfin-compatibility/feature.md).

## Browser UI Mapping

- Movies and Series expose catalogs as an optional filter rather than a separate
  catalog gallery. The filter is shown only when more than one catalog applies to
  the current media kind.
- The Movies page offers `Movie` catalogs. The Series page offers both `Series`
  and `Anime` catalogs because both publish top-level series items.
- The selected catalog is stored in the `catalog` URL query parameter, applied by
  the internal library API, and preserved when opening a detail page and returning
  to the grid.
- Offline catalogs remain selectable and are labelled `Offline`; their published
  database items remain browsable even while file-backed actions may be unavailable.
- The admin Catalogs page keeps its configuration role and provides a
  `Browse media` action that opens the matching filtered Movies or Series page.

## Item Model

```jsonc
{
  "id": "{stable-public-id}",
  "catalogId": "{uuid}",
  "type": "movie",
  "title": "Inception",
  "year": 2010,
  "libraryPath": "Inception (2010)/Inception (2010).mkv",
  "identityProvider": "tmdb",
  "identityProviderId": "27205",
  "providers": { "tmdb": 27205 },   // provider dictionary, not a single tmdbId
  "metadata": { /* per-language cached blobs */ },
  "mediaSources": [ /* from ffprobe */ ]
}
```

`identityProvider` / `identityProviderId` define the canonical identity used for
the stable Jellyfin item id. `providers` is a dictionary of aliases/additional
metadata sources, so additional providers can be added without schema churn or
changing the canonical identity automatically.

## Scanning

The database is the **source of truth**. Items are created by the pipeline's
Publish stage (see [Automation pipeline](../automation-pipeline.md)). Two scan flows
operate over the catalog root (always excluding `.incoming/`):

- a **reconcile** pass that compares published `MediaSource` rows against disk and
  flags files that have gone missing;
- an **import** scan (the per-catalog *Scan* action) that ingests media files
  with no `MediaSource` row through the pipeline from the identify stage, for
  onboarding a hand-copied collection (see
  [Torrents and organizer](../torrents-and-organizer/feature.md)).

- Manual and scheduled scans, constrained to catalog roots.
- Detect supported formats: `.mp4`, `.m4v`, `.mov`, `.mkv`, `.webm`, `.avi`,
  `.ts`, `.m2ts`.
- Parse title, year, season, and episode from file names. The parser is
  selected by catalog `type`: a Jellyfin-compatible naming engine for
  `movie`/`series`, and a dedicated anime parser (AnitomySharp) for `anime`,
  which understands absolute episode numbering and release-group tags. See
  [Metadata](../metadata/feature.md).
- Scanning is idempotent: re-scanning an unchanged catalog produces no duplicate
  items and preserves stable public IDs.

### Offline And Missing Files

- If a catalog `root` is unreachable (unmounted volume), the catalog is marked
  **Offline** and its items are left untouched — a scan never purges items while
  the root is unavailable.
- An **unanchored** catalog (see [Mount Anchoring](#mount-anchoring)) is also
  marked offline and blocked from file-backed actions, but is reported
  separately and does not raise the "volume is unreachable" operator
  notification: the fix is to re-anchor it, not to reconnect a volume.
- If the root is reachable but an individual file is gone, the item is marked
  **Missing/Unavailable** (soft), not deleted, so `UserData`/watched state
  survives a temporary mount glitch or a rename.
- Hard deletion of an item happens only by explicit operator action (see
  [File and directory management](../file-directory-management/feature.md)).

## Testing Expectations

Backend tests should use xUnit and Imposter. Required coverage:

- Catalog configuration validation.
- Mount anchoring: label↔path translation in both directions (including the
  mount root itself, a sibling directory sharing a path prefix, an unknown
  label, dot-segment reduction, label casing, and a relative path that would
  escape its mount); the startup pass over each case (re-anchor, backfill,
  unanchored, standalone) including the staging directories of in-flight
  downloads; the operator re-anchor action, including its refusal to move a
  catalog with an active download and to accept a location another catalog
  already owns by a different spelling.
- Free space and offline reporting, including that an unanchored catalog is
  reported as such rather than as an unreachable volume.
- Parser/provider selection by catalog type (movie / series / anime).
- Scanner behavior for supported formats and idempotency.
- Offline-root handling and soft "missing" marking without purging items.
- Filename parsing for movies, episodes, and anime absolute numbering.
- Stable public ID assignment from canonical provider identity across rescans.
- Catalog-to-Jellyfin `CollectionFolder` mapping.

## Links

- [Catalog library browsing idea](../../ideas/catalog-library-browsing.md)
- [Frontend application](../frontend-application/feature.md)
