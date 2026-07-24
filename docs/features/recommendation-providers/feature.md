# Recommendation Providers

Created: 2026-07-25
Updated: 2026-07-25

## Description

A "what should I watch next" surface fed by two independent engines behind one
provider abstraction. The **built-in engine** works for every user with no
external account; the **Trakt provider** upgrades the feed for users who
connected one. When both are enabled their ranked lists are fused, and a title
both engines chose is boosted — two engines built on different data landing on
the same title is the strongest evidence this feature has.

## Provider boundary

`IRecommendationProvider` mirrors the watched-history provider pattern:
adapters are resolved by stable key, availability is asked **per user**, and
`RecommendationProviderRegistry` rejects duplicate or whitespace-padded keys at
startup rather than letting one source silently shadow another. A provider that
throws during its availability check is skipped, not propagated.

Providers know nothing about the local library. Everything the library alone
can answer — held, watched, hidden — lives in the feed service, so a provider
stays a pure source and the same rules apply to all of them.

## Built-in engine (TMDb + local history)

TMDb answers "what is like X", so all the personalization is in choosing the
X's:

- **Seeds** come from `PlaybackHistoryEntries`, capped at 20 (each is one
  request on a cold cache). An episode play seeds its **series**, so a binge
  cannot spend the whole budget on one show.
- **Weighting**: exponential recency decay on a 90-day half-life, ×1.5 for a
  favorite, ×1.25 for a rewatched movie. Undated marks still seed — a library
  migrated from aggregate counts would otherwise look unwatched — they simply
  earn no recency bonus. Items with no TMDb id are skipped; an id in the
  `Providers` map counts.
- **Aggregation** ranks a candidate by **how many seeds recommend it** before
  how strongly any one does: breadth across a viewer's own taste beats depth.
  Within a seed, TMDb's own order still carries information, so a contribution
  decays down the list.
- Seeds are never recommended back, and with nothing watched the engine returns
  nothing — trending filler would not be a recommendation.

Available whenever the instance has a TMDb key, which it needs anyway.

## Trakt provider

A thin adapter: Trakt runs its engine over a far wider history than this
instance's, so its order is taken as the rank rather than re-scored. Both kind
feeds are read and **interleaved**, because Trakt ranks movies and shows
separately and appending would bury every show below every movie. Both the
wrapped (`{ "movie": {...} }`) and bare response shapes are accepted.

Titles without a TMDb id are dropped: nothing downstream could merge or match
them. Availability requires a connection in `Connected` status — one awaiting
reconnection is not offered, which reads better than a source that is present
and always empty. Every failure path yields an empty list; this source is an
upgrade over the built-in engine, never a dependency of it.

## Fusion

Rank-based by necessity: Trakt returns positions without scores, TMDb returns
vote metadata on items, and mixing those scales would be inventing a common
unit. Reciprocal rank fusion (k=60) needs only position, which both genuinely
have.

A title present in more than one provider's list is multiplied by 1.5 per extra
agreeing provider — enough that two engines quietly agreeing near the bottom
outranks one shouting at the top. One provider listing a title twice is **not**
agreement with itself. Kind is part of identity, so a movie and a show sharing
a TMDb number never merge. Equal scores break by TMDb id, so the feed does not
reshuffle between identical requests.

## Feed service

Asks the available providers (narrowed by the user's source preference), fuses
generously — four times the limit — and only then filters, so excluding watched
and hidden titles shortens nothing.

- **In library** is resolved by TMDb id across every movie and series; a held
  title carries its local ids, and the **library's own title wins**, because
  that is the name shown everywhere else in the app.
- **Watched** excludes a played movie, and a series once *any* episode has been
  played — a part-watched show belongs to Next Up, not to discovery.
- **Hidden** titles are per user and keyed by TMDb identity rather than by
  local item, so a hide survives the title later being added or removed.
- **Posters** are backfilled from TMDb for cards whose source supplied none
  (Trakt supplies none at all), *after* the limit is applied so nobody pays for
  candidates they will not see. A title TMDb genuinely has no poster for is
  cached as a negative, so it costs one request ever rather than one per view.

## Caching

- `TmdbRecommendationCache` — per **seed title**, 7-day TTL enforced on read.
  Shared across users and safe to share: a row records what TMDb says about a
  public title, never who asked. An unreachable TMDb falls back to the stale
  payload, because a week-old list beats an empty feed.
- `TmdbPosterCache` — per title, 30-day TTL. An outage is never cached as "no
  poster"; that would blank a title for a month.

## API

```http
GET    /api/recommendations?kind=&limit=
POST   /api/recommendations/hide          { kind, tmdbId }
DELETE /api/recommendations/hide?kind=&tmdbId=
PUT    /api/recommendations/sources       { sources }
```

All authenticated and scoped to the caller; none accepts a user id, because a
feed is built from what someone watched and serving another user's would leak
exactly that. Hide and unhide are idempotent. The feed response carries the
items, **every** source available to the user (so the control can offer back
one that is currently off), and the selected set.

A stored preference naming only sources that have since disappeared falls back
to all available rather than silently emptying the feed.

## Surface

- A **Recommended for you** row on the home page, rendered only when there is
  something to say, with "See all" leading to `/recommendations`. No new
  top-level tab — navigation stays browse-and-manage.
- The page filters by kind (`All | Movies | Series`) and by availability
  (`Everything | In library | Not in library`). The source control appears only
  once a second source exists; turning the last one off is treated as "all"
  rather than leaving an unexplained empty feed.
- A held title links to its detail page. A discovery offers **Track**, handing
  off to the existing watchlist flow — this page never pretends playback is
  available for something the instance does not have, and acquisition stays in
  [Watchlist and discovery](../watchlist-and-discovery.md).
- Hiding is one click, so undo is one click: the toast carries it.
- A title both engines chose is badged **Both**.

## Not included

Deliberately out of scope: propagating a hide to Trakt's
`DELETE /recommendations/{type}/{id}`, episode-level recommendations, and any
direct hand-off from a discovery card into torrent intake.

## Testing Expectations

- `RecommendationSeedSelectorTests` — episode→series collapsing, recency and
  favorite and rewatch weighting, undated marks seeding without a bonus, the
  seed cap, TMDb id resolution including the providers map, and that another
  user's history never seeds this feed.
- `LibraryRecommendationProviderTests` — agreement across seeds outranking a
  single seed's favorite, TMDb order as tiebreak, seeds never recommended back,
  silence with no history, dense ranks, series seeds asking the series
  endpoint, and survivability when one seed cannot be answered.
- `TmdbRecommendationSourceTests` — cache hits costing no request, TTL
  expiry refreshing in place, stale-payload fallback on an outage, per-kind
  endpoints and rows, and malformed entries dropped.
- `RecommendationFusionTests` — agreement outranking a shouted single list,
  identity merge for one provider, no self-agreement, poster preservation
  across sources, kind as part of identity, and stable ordering.
- `TraktRecommendationProviderTests` — availability gating, interleaving,
  wrapped/bare shapes, TMDb-id-less titles dropped, and empty results on every
  failure path.
- `RecommendationFeedServiceTests` — in-library marking and title precedence,
  watched and hidden exclusion, per-user isolation of both history and hides,
  filtering after fusion keeping the feed full, source preference including the
  vanished-source fallback, and poster backfill only for surviving cards.
- `e2e/recommendations.spec.ts` — the library/discovery split, the Both badge,
  the availability filter, hide-with-undo, the conditional source control, the
  self-explaining empty state, and the conditional home row.
