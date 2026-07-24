# Metadata

Created: 2026-06-15
Updated: 2026-07-24

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
- Before parsing, operator-configured **custom release groups** (Settings page,
  persisted in the `AppSettings` row) are stripped from the name as whole words,
  case-insensitively — e.g. `LostFilm.TV`, `RARBG`. This keeps group/tag tokens out
  of the parsed title so a name like `Project.Hail.Mary.LostFilm.TV` matches cleanly.
- **Movie names also lose a leading track ordinal** (`01. `, `02 - `): franchise
  packs number their films, and the ordinal would otherwise poison the provider
  query. The dot is what separates an ordinal from a title that merely starts with
  digits, so the strip runs before dots are normalized to spaces — `8 Mile`,
  `1408.2007.1080p` and `24.2016.1080p` keep their titles. Series and anime names
  are left alone: there a leading number is an episode ordinal the parsers use.
- **Non-Latin queries search in their own script.** When a parsed title is written
  in the script of one of the configured `SUPPORTED_LANGUAGES` (Cyrillic, Greek,
  Hebrew, Arabic, Thai, CJK, Hangul), the provider search rides with that language
  so the returned titles come back in the query's script. Without it a Russian
  query scores zero title overlap against TMDb's English titles and can never
  reach the auto-match threshold. Each candidate is then scored on the **best of
  its display and original titles**, which also covers the reverse case (a film
  searched by its original-language name whose display title is English).
- High-confidence matches auto-apply. Low-confidence matches route to the review
  queue, where the operator confirms a candidate (manual match override). The review
  dialog pre-fills the parsed title and each file's `SxxEyy`, and auto-searches on
  open so the operator usually just picks a candidate.
- Each playable source file must ultimately map to a movie or an episode. A movie
  batch may resolve to **several different movies** — see
  [multi-movie-ingest](../multi-movie-ingest/feature.md).
- A localized match names the created item in that language, so its canonical
  library folder is localized too (`Назад в будущее (1985)/`). Library naming
  follows the matched title; it is not translated back to a canonical language.
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

- Title, original title, original language, overview, tagline, genres.
- Release/premiere date, runtime, official rating (certification), community
  rating and vote count.
- Posters, backdrops, and logos (language-tagged where provided).
- Cast and crew (directors for movies, creators for series).
- Networks and production companies/studios (with logos).
- External ids (IMDb), trailer (YouTube), keyword tags, homepage, production
  status, and — for movies — the collection/franchise the title belongs to.
- For series: seasons and episodes (counts from the provider).

The detail fetch uses TMDb `append_to_response`
(`credits,external_ids,videos,release_dates|content_ratings,keywords`) so the
single localized detail call already carries everything above — no extra
round-trips. The full payload is kept in the per-language `Raw` JSON blob. How a
field leaves that blob depends on whether it is a property of the one item or a
link between items:

- **Single-item display attributes** (overview, tagline, studios, keywords,
  trailer, …) are projected from `Raw` at display time rather than persisting a
  column per field. Each value is self-contained — it describes only the item
  whose `Raw` it came from — so the blob is a sufficient source of truth.
- **Cross-item structure** (people, and the planned movie
  collections) is persisted as normalized, provider-identified
  entities. Each item's `Raw` is independent and cannot express a link between
  two items, so "the same actor across these ten films" or "these titles share a
  franchise" only exists once a person/collection is deduplicated by provider
  identity into its own row and joined back to the items. Cast and crew are
  written to `Person` + `MediaItemPerson` on every enrich (see
  [Domain model](domain-model.md)); each `Person` is shared library-wide by
  `(Provider, ProviderId)`, and the join carries the per-item credit (character,
  job, billing order).

Official rating is the one display attribute promoted to its own column on
`MetadataRecord`, mapped from the region-specific certification (the region is
implied by the requested language, falling back to US).

The detail **cast** is read from the `Person`/`MediaItemPerson` tables, so each
cast member on a detail page carries its stable `(Provider, ProviderId)` and the
UI links straight to a person page. The remaining people-derived detail fields
(directors, creators) are still projected from `Raw`, which is sufficient since
they are plain names with no cross-item identity to resolve.

### Person details

The credits payload only carries a person's name, profile path, and known-for
department — not their biography or birth/death facts. Those come from a
dedicated person fetch (TMDb `/person/{id}`, api_key as a query/Bearer credential,
never logged) done **lazily**: the first time a person page is viewed, the
`/api/persons/{provider}/{providerId}` read fetches the details and caches them on
the `Person` row (`Biography`, `Birthday`/`Deathday`, `PlaceOfBirth`, refreshed
`ProfilePath`/`KnownForDepartment`). A `DetailsFetchedAt` marker — separate from
`UpdatedAt`, which credit syncs bump — drives a staleness window so the fetch
runs at most once per person until it goes stale, never on every request. A
provider miss leaves the row untouched and retries on the next view. The person
page's filmography is the person's `MediaItemPerson` credits joined back to
published library items, split into cast and crew (crew grouped by department).

## Caching and Refresh

- Metadata blobs are stored as JSON in the database (see
  [Storage and data](storage-and-data.md)); image binaries are cached on disk
  under the app data directory.
- Manual refresh re-fetches metadata for an item across all supported languages.
- Catalog-wide refresh re-runs the idempotent enrich for **every identified item**
  in a catalog as an admin-triggered background job (`catalog:refresh-metadata`).
  It enriches one item per DI scope (bounded change tracker over a large catalog)
  and paces items to stay under the provider's rate limit. Progress (0–100) streams
  over the realtime job feed (see [Background tasks](background-tasks.md)); only one
  refresh runs per catalog at a time, and a run stranded by a restart is reconciled
  to failed on startup.
- Scheduled refresh can update changed records.

## Testing Expectations

Backend tests should use xUnit and Imposter (mock the provider client). Required
coverage:

- Provider search and match scoring (auto-match vs review threshold), including
  the non-Latin path: the search language is picked from the query's script, and
  a candidate scores on the better of its display and original titles.
- Torrent-name/file-list parsing for movie, single-episode, and season-pack
  suggestions, plus movie-only ordinal-prefix stripping (and the digit-led titles
  it must not touch).
- Multi-language fetch and `provider + language` cache keying.
- Catalog language override and fallback ordering.
- Backfill on adding a language.
- Manual match override and refresh behavior.
