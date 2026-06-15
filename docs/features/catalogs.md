# Catalogs

## Description

A **catalog** is an operator-configured destination for content. There can be
many catalogs. When adding a torrent, the operator picks one catalog from the
configured list; that choice drives everything downstream: filename parsing,
target paths, naming, seeding policy, and metadata language. `movie` and `series`
are catalog **types**, not catalogs themselves — multiple catalogs can share a
type (for example "Movies 4K", "Anime", "Series RU").

This replaces the earlier single movie/TV "library" model.

## Catalog Model

```jsonc
{
  "id": "{uuid}",
  "name": "Movies 4K",
  "type": "movie",                 // movie | series
  "root": "/mnt/media/movies-4k",  // one volume; contains files/ and library/
  "namingTemplate": "{Title} ({Year})",
  "defaultKeepSeeding": false,
  "metadataLanguage": null         // optional override of SUPPORTED_LANGUAGES default
}
```

- `type` drives filename parsing (movie vs `SxxEyy`), the Jellyfin
  `CollectionType` (`movies` / `tvshows`), and the naming layout.
- `root` is a single host directory on one filesystem (see Layout below).
- `defaultKeepSeeding` seeds new downloads in this catalog unless overridden at
  add time (see [Torrents and organizer](torrents-and-organizer.md)).
- `metadataLanguage` optionally overrides the global default for this catalog
  (e.g. Anime → `ja`/`en`), see [Metadata](metadata.md).

## On-Disk Layout

Each catalog root contains two sibling subtrees on the **same volume**, so the
organizer can hardlink between them:

```text
<catalog.root>/
  files/        # physical files: torrent download target and seeding source
    Inception.2010.1080p.BluRay.x264/Inception.2010.1080p.mkv
  library/      # clean structure: hardlinks into files/ (scanned by the catalog)
    Inception (2010)/
      Inception (2010).mkv      # same inode as the file under files/
```

- Media Server scans and exposes only `library/`. `files/` is internal.
- The clean name preserves the **original file extension** (the container is
  never changed — playback is Direct Play / Direct Stream only). Resolution and
  quality are read from the file by probing, not encoded in the filename, except
  as a version qualifier when multiple versions of one title exist (a later
  multi-version feature).
- Series layout: `library/<Show> (<Year>)/Season 01/<Show> S01E02.<ext>`.

Configuration validation must reject a catalog whose `files/` and `library/` are
on different filesystems (compare `st_dev`), because hardlinks cannot cross
filesystems.

## Jellyfin Mapping

- Each catalog surfaces as a Jellyfin `CollectionFolder` with `CollectionType`
  from its `type`. Infuse shows each catalog as a separate library.
- Items map to `Movie`, `Series`, `Season`, `Episode`, or `Video` (unmatched).
- Public item IDs are stable across rescans and independent of physical path,
  provider id, and database row id. See
  [Jellyfin compatibility](jellyfin-compatibility.md).

## Item Model

```jsonc
{
  "id": "{stable-public-id}",
  "catalogId": "{uuid}",
  "type": "movie",
  "title": "Inception",
  "year": 2010,
  "libraryPath": "library/Inception (2010)/Inception (2010).mkv",
  "providers": { "tmdb": 27205 },   // provider dictionary, not a single tmdbId
  "metadata": { /* per-language cached blobs */ },
  "mediaSources": [ /* from ffprobe */ ]
}
```

`providers` is a dictionary so additional metadata sources can be added without
schema churn.

## Scanning

- Manual and scheduled scans, constrained to catalog roots.
- Detect supported formats: `.mp4`, `.m4v`, `.mov`, `.mkv`, `.webm`, `.avi`,
  `.ts`, `.m2ts`.
- Parse title, year, season, and episode from names in `library/`.
- Scanning is idempotent: re-scanning an unchanged catalog produces no duplicate
  items and preserves stable public IDs.

## Testing Expectations

Backend tests should use xUnit and Imposter. Required coverage:

- Catalog configuration validation, including same-filesystem enforcement.
- Scanner behavior for supported formats and idempotency.
- Filename parsing for movies and episodes.
- Stable public ID assignment across rescans.
- Catalog-to-Jellyfin `CollectionFolder` mapping.
