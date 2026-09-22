# Movie Detail Context

Created: 2026-09-19
Updated: 2026-09-22

## Movie page

The web movie page combines the retained description with personal viewing history
and related movies. The history section sits below the synopsis, followed by a
persistent Cast carousel and a Media section. Collection and similar-movie rows
follow Media, with Tags as the final section on the page. All sections remain
visible without switching tabs. Series pages share the Cast carousel, followed
by Episodes and finally Tags.

The Cast carousel shows one row of portraits, names, and character labels in
billing order. Links open the corresponding person page. Previous/next controls,
arrow keys, and touch dragging reveal more actors; keyboard focus also brings an
offscreen actor into view. Cards adapt to the viewport without widening the page.
Portraits load lazily and missing photos retain the person placeholder. An empty
cast shows **No cast information available.** Removed movies omit Media and
retain Tags at the bottom of the page.

## Viewing history

**Watch history** appears only with at least two history entries and shows the caller's three latest dated watches in a horizontal
ReUI timeline, newest first from left to right. Dates and times sit below each
point, with time correction and deletion beside the time. Connecting lines run
only between points, so a single viewing has no trailing line. The timeline
scrolls horizontally inside the section on narrow screens and for long histories;
keyboard users can reach every viewing's actions. **Date unknown** marks appear
in a separate list below the timeline. **Show all** expands the dated timeline;
**Load more** retrieves subsequent pages of 20. The count comes from actual
history entries, independently of the legacy playback aggregate. Equal timestamps
remain separate viewings, with stable ordering by entry ID.

Dates and times use the browser's locale and timezone. Each entry offers time
correction and deletion. **Log watch** is available in both the section and the
movie overflow menu, including on retained removed movies. With zero or one entry,
the section is hidden; status beside the hero controls shows the latest dated
watch and provides Log watch when no playback is in progress. A failed history
read still offers retry. Single records can be corrected or deleted in the calendar.

The page reuses the calendar's [time editor](../watch-history-manual-entries/feature.md)
and [deletion confirmation](../watch-history-deletion/feature.md). It uses the
same existing mutation endpoints and aggregate rules. Failed mutations retain the
dialog and input. Changes invalidate movie history, calendar, detail, library,
removed-title, related-movie, and playback-rail queries.

`GET /api/library/{id}/watch-history?offset=0&limit=20` returns `entries`,
`undated`, `total`, `datedTotal`, `offset`, and `limit`. Entries contain `id` and
nullable `watchedAt`. Undated marks are returned separately on every page so
pagination cannot hide them. The web list renders them once. Limits are 1–100;
offset must be nonnegative. Unknown, non-movie, and inaccessible removed seeds
return 404. All history reads and writes belong to the authenticated caller.

## Related movies

Both rows contain available library movies only and link to normal movie detail
pages, preserving the current catalog and removed-list filters across related
details and the return to the grid. Empty rows are hidden. Reads are independent,
so a slow or failed TMDb request does not block the hero, history, or collection row.

- `GET /api/library/{id}/related/collection` reads other published movies from
  the seed's stored collection across catalogs, sorted by release year and title,
  unknown years last. A single available sibling is enough, including for a
  removed seed. The Collections overview retains its two-owned-movies threshold.
- `GET /api/library/{id}/related/similar` combines TMDb recommendations and similar
  results, recommendations first, intersects by movie provider identity with the
  published library, and preserves provider order. It excludes the seed, removed
  or unpublished titles, series, duplicate provider identities, and collection
  siblings. Watched, rated, favorited, and tracked titles remain eligible.

Responses contain `collectionName` and library-card `items`. Related seed access
uses the same rule as detail. Missing TMDb identity yields an empty similar row.
The [existing recommendation source](../recommendation-providers/feature.md)
provides the first page of each TMDb list with its seven-day cache and stale-cache
fallback on provider failure. There is no per-card remote metadata fetch and no
claim of exhaustive TMDb matching. The personal recommendation feed's ranking and
exclusions do not apply to these movie-specific relations.

## Removed movies

A movie under **Show removed** opens the same `/movies/{id}` route as an available
movie. Catalog and removed-list URL state survive navigation back to the grid.
The page uses retained metadata, artwork, credits, and personal data, and shows
**Removed from library**. Media, playback, resume actions, source controls,
conversions, moves, metadata/media refresh, poster editing, and remapping are hidden.
Cast, tags, trailer, external links, tracking, rating, favorite, history, and
related movies remain available. Removed series still use their existing dialog.

The caller can set/change/clear a rating or favorite and log/correct/delete
watches. Clearing a rating or favorite asks for confirmation explaining that the
movie leaves the caller's removed list after their last mark. Deletion of a
viewing explains the same consequence. Admins retain an explicitly confirmed
permanent-delete action that removes all users' data for that title.

Removed detail and personal writes require the caller's own retained history,
favorite, or rating. A title retained solely by another user returns 404. After
clearing the last personal mark, a refetched 404 takes the viewer back to the
movie list. [Tombstone purging](../library-item-tombstones/feature.md) still occurs
only when no user has any retaining signal. Revival retains the internal route
and personal data and restores normal available controls.

`LibraryDetailDto` carries nullable `catalogId` and `removedAt`; a removed movie
can outlive its catalog. Shared services allow removed reads/writes only through
an explicit web opt-in. Normal browse, search, collection membership, playback,
Jellyfin, and native item access continue to exclude removed titles. The shared
native detail schema includes these additive/nullable fields, but native reads
still return published items with a catalog and a null `removedAt`.

## Testing Expectations

- `MovieDetailContextTests`: bounded stable history pages, equal-time rewatches,
  undated entries, caller isolation, retained synopsis and cast without a catalog,
  web opt-in, foreign-write refusal, rating validation, logging, last-mark purge
  through web handlers, another user's retained history, revival, single-member
  collection rows, release ordering, provider-order matching, watched candidates,
  unavailable/series/duplicate filtering, and missing seed or collection-member
  provider identity.
- Existing watch-history tests cover timestamp/aggregate semantics and local/UTC
  conversion across daylight-saving changes. Tombstone/native/Jellyfin regression
  suites preserve ordinary visibility and playback exclusions. Existing TMDb
  source tests cover cache and outage behavior.
- `movie-detail-context.spec.ts`, `removed-titles.spec.ts`, `detail.spec.ts`, and
  `calendar.spec.ts`: inline expansion/pagination, undated correction, logging on
  removed movies, cancelled/failed deletion, rating editing and final-mark
  navigation, removed controls and role gating, independent related loading,
  filtered navigation through both related rows, keyboard navigation, horizontal
  history scrolling without page overflow on narrow layouts, and existing
  calendar/detail interactions.
