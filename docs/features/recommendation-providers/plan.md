# Recommendation Providers

Status: Draft
Created: 2026-07-24
Updated: 2026-07-25

## Goal

A "what should I watch next" surface fed by two recommendation providers behind
one abstraction: a **built-in engine** that seeds TMDb per-title recommendations
from the user's own per-play history (works for every user, no external
account), and a **Trakt provider** over the existing per-user connection. When
both are enabled their ranked lists are fused, and a title both engines agree on
is boosted — agreement between independent engines is the strongest signal
available.

Promoted from the exploratory idea document; the rejected alternatives (TMDb
only, Trakt pass-through only) and the live API probes that grounded them
(2026-07-24: Trakt personalized recommendations work on a **free** account and
return TMDb IDs; TMDb `/similar` is noise and is rejected in favor of
`/recommendations`) live in this file's git history.

## Target Behavior

New behavior; no recommendations surface exists today.

### Surface

- The home page gains a **Recommended for you** row, capped (~12 cards), with a
  "See all" link to a dedicated `/recommendations` page. No new top-level tab:
  navigation stays browse-and-manage as it is.
- Each card: poster, title, year, and its source badge only in detail/hover —
  the feed reads as one list, not two competing ones.
- **In your library** and **Not in your library** are visually separated (badge
  plus a filter), never silently mixed: an in-library recommendation is one
  click from playback, a not-in-library one is a discovery.
- A library card opens the item's detail page. A not-in-library card opens a
  metadata preview (TMDb data) whose action is **Track** — the existing
  watchlist/release-tracking flow. Acquisition (torrent search/grab) stays in
  [Watchlist and discovery](../watchlist-and-discovery.md); this surface never
  pretends playback is available.
- Every card can be **hidden** (with an undo toast). Hiding is local-only in
  v1; propagating a hide to Trakt's `DELETE /recommendations/{type}/{id}` is
  explicitly deferred.
- A source control on the page — `All | Library | Trakt` — narrows the feed;
  the choice is a per-user server-side preference, defaulting to all available
  sources. The control shows only when a Trakt connection exists.
- The feed covers **movies and series** (never individual episodes), with the
  same `All | Movies | Series` kind filter the rest of the app uses.

### Built-in provider (TMDb + local history)

- **Seeds**: the user's most recent distinct watched titles, capped at 20.
  Episode plays seed their **series**. Recency-weighted (a ~90-day half-life);
  favorites and rewatches weigh extra. Seed selection reads
  `PlaybackHistoryEntries` (exact plays) plus `UserItemData` favorites.
- **Fan-out**: one TMDb `/movie/{id}/recommendations` or
  `/tv/{id}/recommendations` call per seed, through a persistent per-title
  cache — the lists move slowly, so entries live for days and refresh lazily.
  A cold user costs at most ~20 requests; a warm one costs none.
- **Aggregation**: candidates are ranked by how many seeds recommend them,
  weighted by those seeds' recency, breaking ties with TMDb's own order.
- **Exclusions**: everything the user has watched (a played movie; a series
  with any play — a mid-watch series belongs to Next Up, not discovery),
  everything hidden, and the seeds themselves.

### Trakt provider

- Available when the user's Trakt connection exists and is healthy; both
  recommendation feeds (`movies`, `shows`) are read and mapped through the
  existing identity machinery by TMDb ID. Trakt's own order is the rank.
- Trakt already excludes watched/collected titles on its side; local hides and
  the watched/in-library classification still apply after mapping.
- A broken connection degrades silently to built-in-only — the feed must never
  error because an optional upgrade went away.

### Merge stage

- Each enabled provider yields a bounded ranked list (top ~50). Lists are fused
  by reciprocal rank (RRF), and a title present in more than one list gets a
  fixed agreement boost on top of its fused score. One provider enabled — the
  merge is identity.
- Fusion is rank-based by design: Trakt returns an ordered list without
  scores, TMDb returns vote metadata on items; the scales are incommensurable.

## Data and API

- `GET /api/recommendations?kind=&source=` — the merged feed for the caller:
  identity (TMDb ID + kind), title, year, poster, `inLibrary` (with the library
  item's id/publicId when present), `sources` (`builtin`/`trakt`), and rank.
  Authenticated, user-scoped, computed on demand over the caches below.
- `POST /api/recommendations/hide` + `DELETE /api/recommendations/hide` — hide
  and unhide by identity (undo needs the inverse). Hides persist per user.
- New tables:
  - `RecommendationHides` — `(AppUserId, Provider-neutral TmdbId, Kind,
    CreatedAt)`, unique per user+identity.
  - `TmdbRecommendationCache` — `(Kind, TmdbId)` → payload JSON + `FetchedAt`;
    TTL enforced on read (~7 days), refreshed lazily on miss.
- The Trakt feed is cached in-memory per user for ~1 hour — it changes slowly
  and a page refresh must not spend the rate limit.
- The per-user source preference is stored server-side (a small preferences
  table; not browser storage).
- No changes to watch-history tables; seed selection is read-only over them.

## Deliverables

- [ ] `IRecommendationProvider` + registry + availability model, mirroring the
      watched-history provider pattern — with tests.
- [ ] Built-in engine: seed selection (episodes→series, recency/favorite
      weighting), TMDb fan-out through the persistent per-title cache,
      aggregation and ranking, watched/hidden/seed exclusions — with tests.
- [ ] Trakt provider over the existing connection: both feeds, TMDb-ID mapping,
      health-gated availability, silent degradation — with tests.
- [ ] Rank fusion: bounded inputs, RRF, agreement boost, identity merge for a
      single provider — with tests.
- [ ] Feed and hide endpoints, hide persistence, per-user source preference —
      with tests.
- [ ] Web: home-page row + `/recommendations` page, library/not-in-library
      split, Track handoff for discoveries, hide with undo, source and kind
      filters — unit tests for the pure logic, e2e for the surface.
- [ ] Live verification against real TMDb and a connected Trakt account (both
      credentials exist on the dev instance).
- [ ] `feature.md` written from shipped reality; this plan deleted; index
      regenerated — in the completing PR.

## Phases

One branch, one PR.

1. **Provider core and the built-in engine** — abstraction, registry, seeds,
   TMDb cache and fan-out, aggregation. Verifiable headlessly: the feed API
   returns a built-in-only list.
2. **Trakt provider and fusion** — the adapter over the stored connection, RRF
   with the agreement boost.
3. **API surface and UI** — feed/hide endpoints, preference, the home row and
   the page, filters, Track handoff, hide/undo.
4. **Live verification and docs** — real-account checks, `feature.md`, plan
   deletion, index.

## Open Questions

Recommended answers baked into Target Behavior above; each needs sign-off in
chat before this plan can go Ready:

- **Surface**: home row + `/recommendations` page reached from it, no new
  top-level tab. Confirm?
- **Discovery handoff**: not-in-library cards go to the Track (watchlist) flow,
  never directly to torrent intake — acquisition stays in Watchlist and
  discovery. Confirm?
- **Hide scope**: local-only in v1; no Trakt hide propagation. Confirm?
- **Sources default**: all available sources by default, per-user server-side
  override, control visible only with a Trakt connection. Confirm?
- **Seed constants** (20 seeds, ~90-day half-life, ~50 per provider before
  fusion, ~7-day TMDb cache TTL): treated as implementation-tunable defaults,
  documented in `feature.md` when shipped, not re-approved individually.
  Confirm?

## Verification

- `dotnet test` — seed selection (episode→series, weighting, exclusions),
  cache TTL behavior, fusion math (RRF order, agreement boost, single-provider
  identity), hide round-trip, feed scoping to the caller.
- Web: `vitest` for the pure feed/grouping logic; `next build`; Playwright e2e
  for the row, the page, the filters, hide/undo, and the Track handoff.
- Live: a cold user's feed populates from history alone; connecting Trakt
  changes the feed (fusion visible); disconnecting degrades silently;
  rate-limit sanity — a page refresh issues no TMDb calls on a warm cache.

## Links

- [Watched-history provider planning](../../planning/trakt-watched-state-sync.md)
  — the connection, identity mapping, and per-play history this feature reads.
- [Watch-history calendar](../watch-history-calendar/feature.md) — the sibling
  consumer of the same history.
- [Watchlist and discovery](../watchlist-and-discovery.md) — where acquisition
  belongs; this surface hands off to tracking, never to intake.
- Trakt: `GET /recommendations/movies|shows`,
  `DELETE /recommendations/{type}/{id}` (deferred).
- TMDb: `GET /movie/{id}/recommendations`, `GET /tv/{id}/recommendations`.
