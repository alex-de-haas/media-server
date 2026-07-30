# Recommendations on the Jellyfin Surface — plan

Status: Draft
Created: 2026-07-30
Updated: 2026-07-30

The feature ships on the web and is described in [feature.md](feature.md). This
document covers one addition: exposing the part of the feed this instance
actually holds to native clients, as its own virtual library.

## Goal

Make a recommendation something the user can play on the device they watch on.
Today the feed lives only in the web UI, where the useful half of it — the
titles already in the library — is one click away from a detail page and no
clicks away from a player that is not there.

## Why a separate view, and not Jellyfin's own mechanism

Jellyfin's protocol does have recommendation endpoints: `/Movies/Recommendations`
(categorized rows built from shared people and genres), `/Items/{id}/Similar` with
its per-type variants, and `/Items/Suggestions`. Two findings rule them out.

**Infuse never calls them.** Six weeks of the app's own request log
(`requests.log`, written by `RequestLoggingMiddleware` whenever
`PLAYBACK_DIAGNOSTICS` is on) contain zero requests to any of them. What Infuse
does call for discovery is narrow and specific:

| Call | Frequency observed | Meaning |
| --- | --- | --- |
| `GET /UserViews` | on launch/foreground, then ~hourly | the list of libraries |
| `GET /Items/Latest?parentId=…&limit=20` | fanned out across **every** view within the same millisecond, right after each `/UserViews` | the home screen's per-library row |
| `GET /Items?parentId=…&recursive=true&startIndex=0&limit=50` | only when a library is opened — 41 times in six weeks | the library grid |
| `GET /Shows/NextUp`, `GET /UserItems/Resume` | with the home screen | continue-watching |

The legacy `/Users/{userId}/Views` path form was never requested; Infuse uses
`/UserViews`. Both are already mapped
([JellyfinUserEndpoints.cs](../../../src/api/MediaServer.Api/Jellyfin/Endpoints/JellyfinUserEndpoints.cs)).

**And their engine is weaker than ours.** Jellyfin derives those rows from local
metadata — shared genres, shared cast and crew. This app already fuses TMDb
similarity over recency-weighted seeds with Trakt's own engine and boosts what
both agree on. Reimplementing Jellyfin's heuristic would be writing a worse
answer into an endpoint nobody asks.

A library view is therefore the only hook Infuse offers, and its `Latest` row is
what reaches the home screen.

## Target behavior

A diff against [feature.md](feature.md), whose **Surface** section currently ends
at the web page.

- A synthetic **Recommended** `CollectionFolder` appears in `/UserViews`
  alongside the catalog views and the Collections view — present only while the
  requesting user's shelf is non-empty, so Infuse never shows an empty library.
  This mirrors the rule the Collections view already follows.
- `Items/Latest` for that view returns **the shelf itself**, in rank order. This
  is where the Collections view and this one deliberately differ: "recently added
  to a franchise" is meaningless, so that one returns empty
  ([JellyfinLibraryService.cs](../../../src/api/MediaServer.Api/Jellyfin/JellyfinLibraryService.cs)),
  whereas for a shelf, "latest" *is* the current selection.
- Opening the view returns the same items, honoring `IncludeItemTypes` — Infuse
  asks for `Movie` and `Series` in separate requests, so a mixed view must not
  answer one with the other.
- **In-library titles only.** A discovery card has no meaning on a surface whose
  only verb is Play; acquisition stays in the web UI and in
  [Watchlist and discovery](../watchlist-and-discovery.md).
- Items are ordinary `MediaItem`s, so playback, artwork, resume and watched state
  work unchanged, and a film appears both here and in its own catalog — exactly
  how Jellyfin models a title belonging to two views.

## Storage: the shelf

The expensive part of the feed is **already** cached in the database —
`TmdbRecommendationCacheEntry` holds each seed title's TMDb list for 7 days, and
is shared across users because it records only public data
([RecommendationEntities.cs](../../../src/api/MediaServer.Api/Data/RecommendationEntities.cs)).
A warm rebuild is a handful of queries and an in-memory merge. So this shelf is
not a TMDb cache; it exists for two other reasons:

1. **The fan-out.** Eleven parallel `Items/Latest` requests must not each start
   the same computation.
2. **Agreement between the row and the grid.** The user sees the row, opens the
   view, and the two must contain the same titles. Anything with an independent
   expiry can lapse between those two requests; a dated snapshot cannot.

### What is stored

An ordered list of foreign keys, and nothing else:

```text
RecommendationShelfItem
  AppUserId    int
  Rank         int
  MediaItemId  Guid    // FK → MediaItem
  GeneratedAt  DateTimeOffset
  (unique index on (AppUserId, Rank); index on AppUserId)
```

Title, artwork, media sources, watched state and version pins are read from the
library at request time and are therefore always current. The snapshot pins only
what is expensive to recompute and must stay still: **which titles, in what
order**. Unlike the web feed's DTO, no TMDb id, poster URL or title is stored —
every row here is by definition held locally, so `MediaItem` is the better source
for all three.

### Filtering happens on read, not by invalidation

The shelf holds **candidates** — an order of magnitude more than a row shows.
`watched` and `hidden` are applied on every read by joining `UserItemData` and
`RecommendationHides`.

The alternative — a ready-made shelf invalidated when the user marks something
played — was rejected: it couples `/UserPlayedItems` to this feature, and it
still shows a just-watched film until the invalidation lands. With read-time
filtering, a title disappears the moment it is played, and the TTL is left
answering the only question it is good at: *has the user's taste moved?*

### Refresh

Lazy, on read, like the existing caches:

- No shelf at all → build it synchronously.
- Shelf present but past its TTL → serve it and refresh behind the request, so
  the hourly `/UserViews` never pays for a rebuild.
- Concurrent readers → single-flight per user, or the eleven parallel `Latest`
  calls start eleven rebuilds.

No periodic background job. The infrastructure exists (`BackgroundService` is
used in seven places), but the natural trigger is the client's own request, and
computing shelves for users who never open Infuse is waste.

## Deliverables

- [ ] **`RecommendationShelfItem` entity + migration**, with the indexes above and
      a cascade from `MediaItem` so a deleted title cannot leave a dangling rank.
- [ ] **In-library-only feed mode.** `RecommendationFeedService.ProjectAsync`
      applies its limit while walking the fused list, so filtering to held titles
      afterwards would return a nearly empty shelf. The in-library filter must run
      *before* the limit, and `WithPostersAsync` must be skipped entirely — every
      surviving card has local artwork.
- [ ] **`RecommendationShelfService`**: build, TTL, single-flight,
      stale-while-revalidate, and read-time `watched`/`hidden` filtering.
- [ ] **Jellyfin identity and mapping**: `JellyfinIds.RecommendationsView()` and
      `JellyfinItemMapper.MapRecommendationsView()`.
- [ ] **View wiring.** `JellyfinLibraryService.GetViewsAsync` gains the acting
      user (it takes none today — catalog views are global, a shelf is not) and
      appends the view when the shelf is non-empty; `GetViewAsync` resolves its id.
      All three routes that surface views — `/UserViews`, `/Library/MediaFolders`,
      `/Library/VirtualFolders` — go through it.
- [ ] **Browsing.** `ListItemsAsync` branches on the view id, honors
      `IncludeItemTypes` and paging, and returns real `MediaItem` DTOs.
- [ ] **`GetLatestAsync`** returns the shelf for this view — the opposite of the
      early-return it uses for the Collections view and BoxSets.
- [ ] **Ordering.** Decide and implement how rank survives the grid (see open
      questions).
- [ ] **Tests** (below).
- [ ] **Docs.** A new surface section in `feature.md`; the view recorded in
      [jellyfin-compatibility](../jellyfin-compatibility/feature.md); `docs/root.md`
      index regenerated; this file deleted on completion. That document has already
      been migrated out of its legacy flat location, so this work only edits it.
- [ ] **Version bump** to `0.45.0` (new functionality) in the same commit as the
      work.

## Phases

One branch, one PR — individual phases deliver nothing usable on their own.

1. **Shelf** — entity, migration, in-library feed mode, shelf service, unit tests.
   Verifiable without any client.
2. **Surface** — ids, mapper, view wiring, browsing, `Latest`, Jellyfin tests.
3. **Live verification and ordering** — the open questions below can only be
   closed against a real Infuse, and the ordering decision depends on what it
   does.

## Open questions

- **`CollectionType` for a mixed view.** `movies` is wrong (the shelf holds
  series too) and `boxsets` lies. `null`/mixed is the likely answer, but some
  clients gate behavior on the library type, so it needs a live check.
- **Does Infuse re-sort the grid?** It asks for
  `sortBy=SortName,ProductionYear&sortOrder=Ascending`, which the server ignores
  today. If Infuse honors its own request locally, rank survives only in the
  `Latest` row and the grid becomes alphabetical. Encoding rank into `SortName`
  would defeat that, at the cost of a visible lie in a field clients also display.
  Decide only after observing the client.
- **TTL.** Long enough that the shelf is not a slot machine, short enough to
  follow taste. A day is the starting proposal.
- **Shelf size.** 100 candidates is the starting proposal: enough that read-time
  filtering still leaves a full row of 20 after a heavy watching session.
- **Row title.** Infuse labels a library's `Latest` row itself; if it reads
  "Recently Added in Recommended", naming the view is the only lever available.

## Verification steps

1. `dotnet test` — the suites below.
2. With `PLAYBACK_DIAGNOSTICS` on, connect Infuse and confirm from `requests.log`
   that the view appears in `/UserViews` and that `Items/Latest` is called for it
   with the others.
3. Confirm the home row and the opened view contain the same titles in the same
   order, and note whether the grid is alphabetical.
4. Play a title from the shelf; confirm it disappears on the next refresh without
   waiting out the TTL, and that playback, resume and watched state behave exactly
   as they do from the title's own catalog.
5. Confirm the view is absent for a user with no playback history, rather than
   present and empty.
6. Confirm a series with one played episode never appears (it belongs to Next Up),
   and that opening the view as "Movies" does not return series.

## Testing Expectations

- `RecommendationShelfServiceTests` — in-library filtering applied before the
  limit; no TMDb poster lookup on this path; TTL expiry rebuilding; a stale shelf
  served rather than an empty one; single-flight collapsing concurrent builds;
  read-time exclusion of watched and hidden titles; per-user isolation; a deleted
  media item leaving the shelf without a hole in the ranks.
- `JellyfinRecommendationsTests` — the view appears only when the shelf is
  non-empty; `Latest` returns the shelf in rank order while the Collections view
  still returns empty; browsing honors `IncludeItemTypes` and paging; the view id
  resolves through `GetViewAsync`; the view itself never appears as an item in a
  flat recursive scan.
