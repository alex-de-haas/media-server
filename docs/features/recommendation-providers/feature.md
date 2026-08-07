# Recommendation Providers

Created: 2026-07-25
Updated: 2026-08-06

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

- **In library** is resolved by TMDb id across every movie and series. Several
  catalogs can hold the same title (a 4K edition beside a regular one); all
  copies are kept, and the oldest is the one a card links to, so adding a second
  edition does not change a link a user already follows. The **library's own
  title wins**, because that is the name shown everywhere else in the app. The
  link carries the **media-item id**: the detail routes are declared
  `{id:guid}` and resolve by it, so a public id would never match.
- **Watched** excludes a played movie, and a series once *any* episode has been
  played — a part-watched show belongs to Next Up, not to discovery. A play
  against *any* copy counts: watching the 4K edition means you watched it.
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
- A card carries the same two lines as a library poster tile: the title under
  the art, then a `Movie · 2010` caption. A title already held is marked with
  an amber check beside that caption — the same mark the tracked drawer and the
  calendar use — and a discovery carries no mark at all.
- A held title links to its detail page. A discovery's poster opens the
  [title preview](../title-preview/feature.md), and the card offers **Track**,
  handing off to the existing watchlist flow — this page never pretends playback
  is available for something the instance does not have, and acquisition stays
  in [Watchlist and discovery](../watchlist-and-discovery.md).
- Hiding is one click, so undo is one click: the toast carries it.
- A title both engines chose is badged **Both**.

## Jellyfin surface — the Recommended view

The held half of the feed is also a library on the [Jellyfin
surface](../jellyfin-compatibility/feature.md), so a suggestion is something the
user can press play on rather than read about.

- A synthetic **Recommended** `CollectionFolder` sits beside the catalog views
  and Collections, advertised only while the requesting user's shelf holds
  something — an empty library tile explains nothing. Its `CollectionType` is
  null (Jellyfin's mixed content): the shelf holds series as well as films.
- `Items/Latest` for this view returns **the shelf itself**, in rank order. This
  is the deliberate opposite of the Collections view, which returns empty —
  "recently added to a franchise" means nothing, whereas for a shelf the current
  selection *is* the latest thing about it. It is also the only per-library hook
  a client offers onto its home screen.
- Opening the view returns the same titles in the same order. The listing
  bypasses the regular browse path, which ends in an alphabetical sort and would
  otherwise replace rank with the alphabet before a client ever saw it. An
  explicit `IncludeItemTypes` is still honored.
- **Held titles only.** A discovery card has no meaning on a surface whose only
  verb is Play; acquisition stays in the web UI.
- **The row is labelled by the client, from the view's name.** Infuse renders it
  as *"Latest Recommended - Local"* — its own template around the library name,
  which is why the view is called `Recommended` rather than anything longer.
- **A newly appearing view reaches the home screen one step late.** The home
  screen is built from the client's cached library list, not from a fresh
  `/UserViews`: the fan-out of `Items/Latest` goes out before the new list is
  read, and the new library's row is fetched as a follow-up request right after.
  So the first time a user's shelf becomes non-empty, the library is browsable
  immediately while its row appears on the next library-list refresh. Nothing to
  fix server-side — the client drives both.
- Rows are ordinary `MediaItem`s, so playback, artwork, resume and watched state
  work unchanged, and a film appears both here and in its own catalog — how
  Jellyfin models a title belonging to two views.

### The shelf

`RecommendationShelfItem` stores an ordered list of media-item foreign keys per
user, and nothing else. Title, artwork, sources and user data are read live, so
the snapshot pins only what is expensive to recompute and must stay still: which
titles, in what order. The row and the grid are two separate requests that have
to agree, and anything with an independent expiry could lapse between them.

- **100 candidates**, an order of magnitude more than a row shows. The build asks
  providers for far more than the web feed does, because held titles are a small
  fraction of any list — and it costs nothing, since the built-in engine fetches
  every seed either way and only trims at the end.
- **Watched and hidden are applied on read**, not by invalidating the shelf, so a
  title leaves the moment it is played rather than when the generation expires.
  A series counts as seen once any episode has been played, and a play against
  *any* local copy counts — the shelf pins one copy, but watching the 4K edition
  still means you watched it.
- **The generation is recorded separately from the rows**, because an empty shelf
  is still an answer. A user whose feed legitimately yields nothing — no history
  yet, or no overlap between the suggestions and the library — would otherwise
  have nothing saying the question was asked, and every view listing would
  rebuild from scratch: for a Trakt-backed user, an upstream call per refresh.
- **One-day TTL**, refreshed lazily: a missing shelf is built synchronously, a
  stale one is served while a rebuild runs behind the request. Rebuilds are
  single-flight per user — a client fans `Items/Latest` across every library at
  once, and without that each would start its own.
- No poster lookup ever happens on this path; every surviving row has local
  artwork.

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
  vanished-source fallback, poster backfill only for surviving cards, and
  multi-copy titles — a play on any copy excluding the title, and a stable
  representative for the link.
- `RecommendationShelfServiceTests` — held-only contents with the library filter
  applied before the limit; no poster lookup on this path; rank preserved as
  stored; read-time exclusion of watched, part-watched series and hidden titles,
  including a play against another copy of a stored title; another user's plays
  and hides not touching this shelf; a stale generation served rather than
  rebuilt in the request, and rebuilt behind it; an empty generation not rebuilt
  on every read but still rebuilt once its TTL expires; concurrent readers
  building once; a deleted title leaving without breaking the rest; a shelf whose
  every title is watched counting as empty; a rebuild replacing the generation
  rather than appending.
- `JellyfinRecommendationsTests` — the view advertised only when the shelf has
  something, as mixed content, and never to an anonymous caller; its id
  resolving as a view only while non-empty; `Latest` returning the shelf in rank
  order and passing its limit through, while the Collections view still returns
  empty; browsing keeping rank rather than sorting by title, honoring a type
  filter and paging; and the view never appearing as an item in a flat scan.
- `e2e/recommendations.spec.ts` — the library/discovery split, the Both badge,
  the availability filter, hide-with-undo, the conditional source control, the
  self-explaining empty state, and the conditional home row.
