# Catalogs

Status: Implemented
Created: 2026-06-15
Updated: 2026-07-12

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
  "root": "/mnt/media/movies-4k",  // one volume; holds .incoming/ + canonical media
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
- `root` is a single host directory on one filesystem (see Layout below).
- `defaultKeepSeeding` seeds new downloads in this catalog unless overridden at
  add time (see [Torrents and organizer](torrents-and-organizer.md)).
- `metadataLanguage` optionally overrides the global default for this catalog
  (e.g. Anime → `ja`/`en`), see [Metadata](metadata/feature.md).

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
  [Jellyfin compatibility](jellyfin-compatibility.md)); reserving it now avoids a
  path migration later.
- Series layout: `<Show> (<Year>)/Season 01/<Show> S01E02.<ext>`.

The catalog root is a single filesystem, so the move from `.incoming/` into the
canonical tree is atomic and copies no bytes.

## Free Space

Each catalog reports the free space on its `root` volume. The UI shows this when
the operator picks a destination catalog for a download, and the engine uses it
for the pre-download space check (see
[Torrents and organizer](torrents-and-organizer.md)).

## Jellyfin Mapping

- Each catalog surfaces as a Jellyfin `CollectionFolder` with `CollectionType`
  from its `type` (`movie` → `movies`; `series` and `anime` → `tvshows`). Infuse
  shows each catalog as a separate library.
- Items map to `Movie`, `Series`, `Season`, `Episode`, or `Video` (unmatched).
- Public item IDs are stable across rescans and based on the catalog plus the
  canonical provider identity, not on physical path or database row id. See
  [Jellyfin compatibility](jellyfin-compatibility.md).

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
Publish stage (see [Automation pipeline](automation-pipeline.md)). Two scan flows
operate over the catalog root (always excluding `.incoming/`):

- a **reconcile** pass that compares published `MediaSource` rows against disk and
  flags files that have gone missing;
- an **import** scan (the per-catalog *Scan* action) that ingests media files
  with no `MediaSource` row through the pipeline from the identify stage, for
  onboarding a hand-copied collection (see
  [Torrents and organizer](torrents-and-organizer.md)).

- Manual and scheduled scans, constrained to catalog roots.
- Detect supported formats: `.mp4`, `.m4v`, `.mov`, `.mkv`, `.webm`, `.avi`,
  `.ts`, `.m2ts`.
- Parse title, year, season, and episode from file names. The parser is
  selected by catalog `type`: a Jellyfin-compatible naming engine for
  `movie`/`series`, and a dedicated anime parser (AnitomySharp) for `anime`,
  which understands absolute episode numbering and release-group tags. See
  [Metadata](metadata/feature.md).
- Scanning is idempotent: re-scanning an unchanged catalog produces no duplicate
  items and preserves stable public IDs.

### Offline And Missing Files

- If a catalog `root` is unreachable (unmounted volume), the catalog is marked
  **Offline** and its items are left untouched — a scan never purges items while
  the root is unavailable.
- If the root is reachable but an individual file is gone, the item is marked
  **Missing/Unavailable** (soft), not deleted, so `UserData`/watched state
  survives a temporary mount glitch or a rename.
- Hard deletion of an item happens only by explicit operator action (see
  [File and directory management](file-directory-management.md)).

## Testing Expectations

Backend tests should use xUnit and Imposter. Required coverage:

- Catalog configuration validation.
- Parser/provider selection by catalog type (movie / series / anime).
- Scanner behavior for supported formats and idempotency.
- Offline-root handling and soft "missing" marking without purging items.
- Filename parsing for movies, episodes, and anime absolute numbering.
- Stable public ID assignment from canonical provider identity across rescans.
- Catalog-to-Jellyfin `CollectionFolder` mapping.

## Links

- [Catalog library browsing idea](../ideas/catalog-library-browsing.md)
- [Frontend application](frontend-application/feature.md)
