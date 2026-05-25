# Media Libraries

## Description

Media Server manages movie and TV series libraries over configured storage
roots. It scans media files, parses names, fetches metadata from TMDb, and stores
stable media records for browsing and playback.

## Library Types

Supported library types:

- Movies.
- TV series.

Example library configuration:

```json
{
  "id": "{uuid}",
  "type": "movie",
  "name": "Movies Library",
  "paths": ["/mnt/media/movies"]
}
```

## Media Scanning

Scanning requirements:

- Manual scans.
- Scheduled scans.
- Scans constrained to attached directories.
- Detection of supported media formats.
- Filename parsing for title, year, season, and episode.

Supported initial formats:

- `.mp4`
- `.mkv`
- `.avi`
- `.mov`
- `.webm`

## Metadata Management

TMDb integration provides rich metadata for movies and TV series.

Capabilities:

- Fetch metadata for newly discovered files.
- Re-scan and refresh metadata.
- Manual match override.
- Local metadata cache to avoid excessive TMDb requests.

Metadata includes:

- Title.
- Original title.
- Description.
- Genres.
- Release date.
- Runtime.
- Posters and backdrops.
- Cast and crew.
- Seasons and episodes for series.

## Media Entity Model

Simplified media entity:

```json
{
  "id": "{uuid}",
  "type": "movie",
  "title": "Inception",
  "year": 2010,
  "path": "/mnt/media/movies/Inception (2010).mkv",
  "tmdbId": 27205,
  "metadata": {}
}
```

`tmdbId` should later become a provider dictionary so multiple metadata sources
can be represented without schema churn.

## Movie Entity Fields

Movie records contain:

- Id.
- OriginalTitle.
- OriginalLanguage.
- Title.
- Overview.
- VoteAverage.
- VoteCount.
- ReleaseDate.
- Budget.
- Revenue.
- PosterPath.
- BackdropPath.
- LogoPath.
- Genres.
- Crew.
- Cast.
- ReleaseDates.
- OfficialRating.

## Testing Expectations

Backend tests should use xUnit and Imposter.

Required coverage:

- Library configuration validation.
- Scanner behavior for supported formats.
- Filename parsing for movies and episodes.
- TMDb metadata fetch and cache behavior.
- Manual metadata match override.
- Storage root and library access constraints.
