# Apple Series Browsing

Created: 2026-09-08
Updated: 2026-09-09

The tvOS series detail screen presents a horizontal season selector above a
horizontal rail of episode cards. Focusing or selecting a season updates the rail
in place. Seasons are sorted numerically, with season zero labeled Specials;
empty seasons are omitted. There is no series-level Play action.

Cards use 16:9 episode stills and show numbering (including multi-episode ranges),
title, short synopsis, duration, air date, watched status, and resume progress when
available. A compact badge on the still shows Dolby Vision if any available
version has it, otherwise HDR for HDR10/HDR10+/HLG/HDR. SDR and unknown formats
have no badge. This uses `media.videoFormats` across all versions, independently
of the pinned/default source; older servers without this aggregate omit the badge.
Codec, resolution, file size and version count are absent from the episode rail;
codec, resolution, file size and detailed dynamic range appear for each variant
on the episode's version selection screen.
The short card synopsis opens into the full synopsis on the episode detail screen;
the series synopsis remains on the series screen. Air dates use UTC calendar dates to avoid shifting to the previous day.
Missing still URLs fall back to the series backdrop, then a title placeholder.

An episode opens the existing title detail screen with its own ID. Versions,
Play/Resume, audio/subtitle selection and progress reporting use that episode ID.
Returning to the series preserves the season, card focus and horizontal position;
the episode list is reloaded to refresh playback state. Older requests cannot
replace a newly selected season, even when transport ignores cancellation.

Loading, empty results, request failures with retry, and a missing endpoint or
removed season have explicit states. A 404 advises reloading the series or updating
the server because older servers and removed items share that response.

## Native API

`GET /native/v1/items/{seriesId}/episodes?seasonId={seasonId}` requires the same
Hosty user identity as other native reads. The series and optional season must be
published, not removed, and related; invalid identities return 404. Only published,
non-removed episodes in visible seasons with stored media sources are returned.
Native series details likewise include only seasons containing such episodes,
with counts computed from that same availability rule.

Each result wraps the shared `EpisodeDto` as `episode`, with optional `still` and
`durationTicks`. `EpisodeDto.airDate` comes from localized episode metadata. Still
URLs point to authenticated instance image routes and carry cache tags. Artwork
and default-source duration are projected in batches, without one detail fetch
per card. Runtime falls back to the default source when metadata lacks it; a
multi-episode file uses its full source duration when known.

The existing library sync continues to populate only movie/series grids. Episode
reads do not insert entries into those grids. The web episode media projection
and playback/source selection remain shared with
[Episode Media](../episode-media/feature.md).

## Episode metadata and existing libraries

The enrichment pipeline includes episodes alongside movies and series. The
provider receives series identity plus season and episode coordinates, preferring
stored identity coordinates with display-number fallback for older records.
TMDb's [episode details](https://developer.themoviedb.org/reference/tv-episode-details)
and [episode images](https://developer.themoviedb.org/reference/tv-episode-images)
provide localized titles, synopses, runtimes, air dates and stills. Stills are stored
as Backdrop assets and served through the existing image cache. After a non-empty
still response is saved, obsolete Backdrop assets from that provider are removed
from the episode, including legacy show artwork. Empty responses retain existing
artwork; other providers and poster selections are preserved. A combined episode
file retains one identity and uses the first episode's metadata.

New imports receive episode enrichment automatically. Existing libraries receive
it through the catalog's Refresh metadata action. There is no automatic full
library re-enrichment at upgrade. A provider without episode support returns no
episode metadata instead of copying the whole show's title and artwork.

## Testing Expectations

- `NativeEpisodeTests`: route authentication/public-surface metadata, series/season
  validation, publication/removal/source visibility, per-user progress, cached
  still URLs, source-duration fallback and available-season counts.
- `TmdbEpisodeTests`: episode-specific requests, metadata/still mapping,
  idempotency, multi-episode identity and refresh of an existing published episode.
- MediaKit series tests: season mapping/order/Specials, episode ranges, artwork
  fallback, progress, empty/404/retry states, request races, native authentication,
  grid isolation, episode playback identity, all-version badge priority/omissions,
  and per-version resolution from the picture rather than cover art.
- Build the tvOS target and run `--cinema-preview --series-preview` in a simulator
  to exercise the three-season, 18-episode fixture, horizontal focus, episode
  details, source controls and Back restoration.
- Real-device playback, resume persistence and audio/subtitle acceptance remain
  tracked in [the plan](plan.md); simulator checks do not establish those results.
