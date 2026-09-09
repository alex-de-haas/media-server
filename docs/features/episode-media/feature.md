# Episode Media

Created: 2026-09-08
Updated: 2026-09-09

An episode has the media surface a movie has — its versions, the tracks inside
them, the sidecars beside them, and every conversion the
[Convert dialog](../convert-dialog/feature.md) composes — and that surface is one
component rather than a movie-only one with a series-shaped hole beside it.

Before this, three services refused an episode's version outright ("Only movies can
be transcoded for now"), and the Episodes tab said nothing about the file behind an
episode. Everything below those gates already worked for an episode: the pipeline
writes per-episode versions with editions, sidecars attach to an episode's source,
`GET /api/library/{episodeId}` returns its versions, a conversion's output is named
beside its input by path, and the move lock resolves an episode to its series. The
gates were what was left of "for now".

## One surface

The version cards, stream sections, sidecar list, the rename and delete dialogs and
the Conversions block live in `media-sources.tsx`, owned by an item — its id, kind
and title — rather than by "the movie". A movie's Media tab renders it exactly as
before; an episode's expanded row renders the same component fed by the episode's
own detail. The API accepts the same calls for either kind, and this is the one place
that offers them, so the two cannot drift.

Two things the owner decides:

- **`moving`** locks every control while a move is relocating these very files. An
  episode's move is its series', so a whole show's rows lock together. The API
  rejects those calls with a 409 anyway; they are simply not offered.
- **`onChanged`** runs after anything here changes a version, beyond the owner's own
  detail query. An episode row uses it to refresh the season's summary lines.

The rename dialog previews the locked stem by reading it off the file name —
what precedes the label's ` - ` — rather than composing `Title (Year)`, which is what
makes it right for an episode's `Show S01E03` too (`versionStem` in `format.ts`). The
server rebuilds the real stem from metadata regardless; a label that drifted from
the file leaves the whole base name as the stem rather than guessing. Copy lost the
word "movie": "Create a new version of this title", and the API's "This title already
has a version at…".

## The Episodes tab

- Every episode row leads with the episode itself — its still, title, air date,
  runtime and synopsis, from [metadata](../metadata/feature.md#episode-details-and-stills) —
  and carries a **media summary** beneath them: the picture's codec and
  height, its dynamic-range badges in the vocabulary the version cards use, the
  file's size, and the version count when it is more than one —
  `HEVC 2160p · Dolby Vision 7 · 38.2 GB · 2 versions`. With several versions the
  codec, height and size are the **default** version's, since that is the file a
  player starts on. An episode with no source on disk reads "No file". The summary
  rides on `EpisodeDto.media`, computed in one query for the whole listing —
  filtered through the episode rows rather than an id list, so a long-running show
  never runs into SQLite's parameter limit.
- Every row **expands**. Expanded, it fetches `GET /api/library/{episodeId}` — on
  expand, so a 200-episode listing does not carry 200 stream lists — and shows what a
  movie's Media tab shows: versions, tracks, sidecars, and for an admin the controls:
  pin default, rename, convert, extract, merge, delete version, remove sidecar. A
  viewer sees the media and none of the controls.
- A **Conversions** block sits above the seasons for an admin, listing the jobs of
  every episode of the series. A job card names its output file, which carries the
  episode code, so nothing is lost by not listing them per row. When the last active
  job finishes, the episode listing and every open row's detail refresh, as the movie
  tab refreshes its version list.
- **Refresh media data** in the series `⋮` menu fans out over the series' episodes
  on the server: a series row holds no file of its own, so a refresh asked of it is a
  refresh of its episodes' — one gesture per title, as on a movie. There is no
  per-episode control inside the expanded row.

## The grid

The Series grid caption carries the same format badges the Movies grid does,
aggregated across the series' episodes: the union of formats, deduplicated, in the
grid's order, with covers and audio skipped exactly as they are for a movie. A show
whose later seasons arrived in Dolby Vision says so on its card; one whose episodes
were never probed carries none.

## What the API accepts

- `TranscodeService.CreateAsync` and `TrackExtractionService.CreateAsync` accept a
  `Movie` or `Episode` source; `TranscodeTargets.RequireMovieOrEpisode` holds the
  rule for both. A series extra (`MediaKind.Video`) is refused **by name** — "this
  version belongs to a series extra" — because it has no surface it could be reached
  from, and admitting it would be a promise nothing displays. An episode's output
  lands as `Show S01E03 - HEVC 1080p.mkv` in the season folder; a merge reads the
  sidecar beside the episode; the job attaches to the episode.
- `LibrarySourceService.RenameVersionAsync` renames an episode's version through
  `LibraryNaming.ForEpisode`, loading the series row through `SeriesId`. The season
  folder and the `SxxEyy` / `SxxEyy-Ezz` token are as locked as a movie's
  `Title (Year)`; an episode whose series row cannot be found is `NotFound`, since
  there is nothing to name it by. `Unsupported` now names what it covers: a movie or
  episode version.
- `LibraryMaintenanceService.RefreshMediaAsync(seriesId)` fans out over the series'
  episodes and answers true when the series exists. External rows stay spared, as
  they are for a movie.
- `EpisodeDto.media` is `{ versionCount, videoCodec, height, hdrFormat, dolbyVision,
  sizeBytes, videoFormats }` or null. The picture follows the shared selection rule
  (`NativePlaybackResolver.PictureStream`), so a cover a muxer wrote as a video track
  is passed over; the default version by `MediaSourceOrdering`, so the row and the
  players agree on which file that is. `videoFormats` aggregates the selected
  picture format from every version, ignoring covers when a non-still video stream exists
  and external streams. It is independent of the default-source pin and uses the
  same normalized HDR/Dolby Vision names as library cards.
- `MediaStreamDto.resolutionLabel` carries the server-defined nominal resolution
  from `VideoResolution.Label(width, height)` for video streams. Non-video streams
  and unknown dimensions carry null; clients can display it without duplicating
  resolution buckets.
- `LibraryItemDto.videoFormats` is filled for `Series` items from their episodes'
  picture streams.
- The Jellyfin `GET /Shows/{seriesId}/Episodes` honours `Fields=MediaSources` the way
  the items listing does, so a client that builds its version picker from the
  episode listing sees an episode's versions
  ([jellyfin-compatibility](../jellyfin-compatibility/feature.md)).

## What stays as it was

- Deleting an episode's last version leaves the episode as a row with no file, which
  is what deleting a movie's last version does. Parity is kept here on purpose; that
  the row survives at all is a question of removal semantics, shaped in
  [file-directory-management](../file-directory-management/plan.md).
- Extras have no surface; nothing here adds one. The Apple client's episode screens
  are [apple-series-browsing](../apple-series-browsing/plan.md)'s.
- Log watch stays on movies; a whole title moves between catalogs, never one episode.

## Testing Expectations

- `TranscodeServiceConversionTests` — the whole conversion path for an episode
  against a real database and the recording engine: the output beside the episode in
  its season folder with the episode token in its name, a merge reading the sidecar
  beside it, a series extra refused by name, and a path a version already holds
  refused in words that fit an episode.
- `TrackExtractionTests` — an episode's track landing beside it in the season folder
  with the job attached to the episode; a series extra refused by name.
- `LibrarySourceServiceTests` — an episode renamed inside its season folder with the
  file moved and the item's library path following, cleared to its bare canonical
  name, the double-episode token kept, an episode with no series row `NotFound`, and
  a series extra `Unsupported` with the message naming what is allowed.
- `LibraryReadServiceTests` — the episode summary: version count, codec and height
  from the picture rather than a cover, formats, size, the default version's figures
  before and after a pin, all-version formats unaffected by pinning with cover and
  external formats excluded, an episode with no source carrying nothing, and the same
  summaries when scoped to a season; server-projected resolution labels for cropped
  widescreen, vertical video and missing dimensions; series cards carrying the union of their
  episodes' formats with covers skipped and a movie unaffected.
- `LibraryMaintenanceServiceTests` — a series refresh re-probing every episode's
  sources and sparing their sidecars.
- `JellyfinMappingTests` — the episode listing carrying versions only when the field
  asks for them.
- Vitest (`format.test.ts`) — the summary line's order and omissions, "No file"; the
  locked stem for a movie, an episode, no label, a drifted label, no extension.
- `detail.spec.ts` — the summary on a row and "No file" on one without a source,
  expanding fetching the episode's detail once and listing its versions, a viewer
  seeing the media and none of the controls, an admin opening Convert and Extract
  from an episode's version with copy that fits either kind, the rename preview
  carrying the episode's own stem, and an episode's job listed above the seasons.
  The movie media, convert, extract and sidecar cases pass unmodified against the
  shared component.
- `catalog-browsing.spec.ts` — a series card carrying its badges, and none for a
  series with no probed episode.
