# Metadata

## Description

Media Server enriches catalog items with metadata from external providers. TMDb
is the first provider, but the design is provider-agnostic from the start so
additional sources can be added without schema churn.

## Provider Abstraction

- An `IMetadataProvider` abstraction encapsulates search, match, fetch, and image
  retrieval. TMDb is the first implementation.
- Items carry a provider **dictionary** (`providers: { "tmdb": 27205 }`), not a
  single `tmdbId`, so multiple sources can coexist.
- A local cache stores fetched metadata and images to avoid excessive provider
  requests.

## Identification

- The pipeline's identify stage parses the library filename (title, year,
  `SxxEyy` for series) and queries the provider.
- High-confidence matches auto-apply. Low-confidence matches route to the review
  queue, where the operator confirms a candidate (manual match override).
- Match results are cached against the stable public item ID.

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
- Multi-language fetch and `provider + language` cache keying.
- Catalog language override and fallback ordering.
- Backfill on adding a language.
- Manual match override and refresh behavior.
