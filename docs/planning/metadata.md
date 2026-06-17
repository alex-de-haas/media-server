# Metadata

Status: Draft
Created: 2026-06-15
Updated: 2026-06-15

## Description

Media Server enriches catalog items with metadata from external providers. TMDb
is the first provider, but the design is provider-agnostic from the start so
additional sources can be added without schema churn.

## Provider Abstraction

- An `IMetadataProvider` abstraction encapsulates search, match, fetch, and image
  retrieval. TMDb is the first implementation.
- Items carry a canonical provider identity plus a provider **dictionary**
  (`providers: { "tmdb": 27205 }`). The canonical identity drives the stable
  Jellyfin item id; the dictionary lets multiple sources coexist as aliases or
  supplemental metadata.
- A local cache stores fetched metadata and images to avoid excessive provider
  requests.
- The name parser is selected by catalog `type`: the **Emby.Naming** engine (MIT)
  for `movie`/`series`, and the **AnitomySharp** parser for `anime`. Anime uses
  **absolute** episode numbering, normalized to the provider's season/episode model
  during identify; movies and series use aired-order `SxxEyy`.
- The metadata **provider** is **TMDb for all types in v1**, including anime;
  AnitomySharp handles only anime name parsing. There is no clean drop-in .NET
  AniDB library (AniDB's UDP API is rate-limited and requires client registration;
  Shoko Server is a full app, not a library), so a dedicated anime provider
  (TheTVDB or Shoko/AniDB) is deferred. The provider abstraction keeps it additive.

## Identification

- The pipeline's identify stage parses the torrent name and available file list
  before download where possible, then queries the provider. If a magnet link has
  no file list yet, file-level matching runs after torrent metadata is fetched.
- High-confidence matches auto-apply. Low-confidence matches route to the review
  queue, where the operator confirms a candidate (manual match override).
- Each playable source file must ultimately map to a movie or an episode.
- Match results are cached against the stable public item ID and the source-file
  assignment, so a later remap can rebuild clean paths and downstream metadata.

## Language Model

Metadata language is driven by a **global ordered list of supported languages**,
`SUPPORTED_LANGUAGES` (for example `ru-RU,en-US,ja`; the first entry is the
fallback).

- On enrich, metadata is fetched and cached for **every** supported language.
- The cache is keyed by `provider + language`, so selecting a display language is
  just choosing from already-cached data, with fallback down the list.
- A catalog may override the effective default with `metadataLanguage` (e.g.
  Anime → `ja`/`en`). Per-user language selection is a later, additive change
  because the cache is already multi-language.

Efficiency:

- Use the provider's bulk translations endpoint where available (TMDb
  `/{movie|tv}/{id}/translations` returns all translations in one request) instead
  of one request per language.
- Fetch language-tagged and neutral images together (TMDb
  `include_image_language=ru,en,null`).
- Always store `originalTitle` and `originalLanguage` so display is never locked
  to one language.
- Adding a language to the global list triggers a background **backfill** job to
  populate it for existing items.

## Metadata Fields

Common fields cached per language where applicable:

- Title, original title, original language, overview, genres.
- Release/premiere date, runtime, official rating, community rating.
- Posters, backdrops, and logos (language-tagged where provided).
- Cast and crew.
- For series: seasons and episodes.

## Caching and Refresh

- Metadata blobs are stored as JSON in the database (see
  [Storage and data](storage-and-data.md)); image binaries are cached on disk
  under the app data directory.
- Manual refresh re-fetches metadata for an item across all supported languages.
- Scheduled refresh can update changed records.

## Testing Expectations

Backend tests should use xUnit and Imposter (mock the provider client). Required
coverage:

- Provider search and match scoring (auto-match vs review threshold).
- Torrent-name/file-list parsing for movie, single-episode, and season-pack
  suggestions.
- Multi-language fetch and `provider + language` cache keying.
- Catalog language override and fallback ordering.
- Backfill on adding a language.
- Manual match override and refresh behavior.
