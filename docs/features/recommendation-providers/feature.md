# Recommendations

Created: 2026-07-25
Updated: 2026-08-14

## Description

A "what should I watch next" surface built entirely from what this instance
already knows: what the viewer watched, what they said about it, and what the
library holds. TMDb answers "what is like X" and supplies public metadata; every
judgement about *this* viewer is made locally.

There is **one engine**. There was briefly a second source — Trakt — and rank
fusion to merge them; both are gone. Registering a Trakt OAuth application now
requires VIP, so almost no operator could reach it, and the engine had grown a
shaped output (diversity, caps) that fusion could only flatten back into
positions and re-derive. Trakt **watched-history sync is a separate feature and
is untouched** — see [watch-history providers](../watch-history-providers/feature.md).

## Star ratings

A watched movie or series can be rated **1–5 stars**. This is the strongest
signal the feature has, and the schema had nothing like it before: the engine
could see a play, a favorite and a rewatch, so a film watched and endured seeded
the feed as loudly as one watched and loved.

**It is not a second favorite.** A favorite is curation — "keep this where I can
find it" — applies to any item including seasons, and travels to a connected
provider. A rating is a judgement on a work, stays local, and places the title in
no list. Neither writes the other, and a title can honestly be both a favorite
and two stars.

`UserItemData.Rating` is nullable because *unrated* and *one star* are opposite
statements. Clearing a rating is its own action, not a synonym for rating badly.

**A rating counts as having watched.** Nobody rates a film they have not seen, and
this instance is frequently not where they saw it — an imported library, or a
viewer grading their back catalogue, has ratings and no playback rows at all. So
a rating both **excludes** a title from the feed and **seeds** it, exactly as a
play does. Requiring a play alongside it would have left the strongest signal the
schema carries doing nothing at all, while the feed recommended back the very
titles the viewer had already graded.

### What each star means, and why the curve is not linear

| ★ | Meaning | Seed weight |
| --- | --- | --- |
| ★★★★★ | nothing to fault in it | ×6.5 |
| ★★★★ | loved it — where most favorites land | ×4.0 |
| ★★★ | a good film, no regrets | ×1.7 |
| *unrated watch* | — | ×1.0 |
| ★★ | worth it only with nothing else on | not seeded |
| ★ | disliked it; the time is the loss | not seeded |

Everything is priced in ordinary unrated watches, which keeps their weight at
1.0 — so an instance where nobody rates anything ranks exactly as it did before
ratings existed.

The mass of a five-point scale sits at 2–4 and the top is *reserved*, so the
qualitative break is between 3 and 4 (×2.35), not between 4 and 5 (×1.6). A
linear map would price "no regrets" at three fifths of "flawless".

**One and two stars do not seed.** Asking TMDb what is like a film the viewer
would not repeat spends one of twenty requests fetching candidates the scorer
then has to push back down. Their facets are still read — they are removed as
*sources of candidates*, not as evidence — and they are the strongest entries in
the negative profile: a hide judges a title never watched, a low rating is a
verdict after watching one.

A seed and its facets always carry the same sign, so the line between seeding
and not seeding is the same line as between positive and negative.

### A rating does not decay

```
w(s) = Rewatch(s) · ( Rating(s) ?? Decay(age) · (Favorite(s) ? 1.5 : 1.0) )
```

The 90-day half-life applies to the **unrated branch only**. A rating is a
standing statement about taste; the way to revise it is to re-rate or clear it,
which is the viewer saying so rather than the engine assuming after ninety days.
Left decaying, a 5★ from two years ago would be worth 0.02 against 1.0 for a film
watched yesterday and never thought about again.

Recency did not disappear — it moved into the tie-break. Rated weights are
discrete constants, so among forty films rated five stars the twenty watched most
recently take the seed slots.

**Four of the twenty seed slots are reserved for recency.** Without them, once
twenty titles are rated 3★ or better an unrated watch could never seed again —
not rarely, never — and the feed would stop noticing what the viewer watched last
week. Ratings own sixteen slots; recency keeps four.

### Where a rating is given

- `PUT /api/library/{id:guid}/rating` and `DELETE …/rating`, mirroring the
  favorite pair. Out of range, or a season or episode, is a 400 rather than a
  silently ignored write.
- The detail page carries a five-star control; clicking the lit star clears it.
  **Favorite is a heart**, not a star, so the two gestures do not share a mark.
- Ratings survive what they should: a delete tombstones rather than purges the
  row, a remap keeps the higher of two, and a removed title keeps its rating
  until an explicit clear — deleting a file does not retract a verdict.
- Nothing syncs a rating anywhere.

## The taste profile

Per user, built entirely from local data at zero request cost, over five facet
families: **genres, keywords, people, decade, original language**.

- **People are weighted by role**, not by presence: director 1.0, writer 0.6,
  cast by billing order. `MediaItemPerson` already carries `Order`, `Job` and
  `Department`, so this is a join.
- **Facets are IDF-damped against the library's own frequencies.** Without it
  "Drama" dominates every profile and every viewer looks alike; the point of a
  profile is what distinguishes this one.
- **Each family is normalized separately**, so a title carrying sixteen keywords
  cannot outvote one carrying four.
- **Liked and disliked are two vectors, not one signed vector.** A viewer can
  like thrillers and still have rejected three of them, and collapsing that would
  let a handful of one-star films erase a family they demonstrably enjoy.

Negative input comes from 1★ and 2★ ratings, abandonment (started, stopped under
15%, never finished) and hides. Positive input includes the watchlist at 0.4 of a
watch — wanting to see something is a weaker statement than having seen it — and
only for tracked titles that resolved to a library item, since fetching for a
pure wishlist row would break the zero-request promise.

Unlike the seed set the profile is **not capped**: seeds are capped because each
costs a request, facets cost a join.

### Caching, by stamp rather than invalidation

The profile and the library facet index are cached against a **stamp of their own
inputs** — play counts, the sum of `UserItemData.StateRevision`, hide and
watchlist counts, and a library generation (work count plus the latest
`UpdatedAt` and `FetchedAt`).

The alternative was hooking six write paths, where a forgotten hook fails
*silently*: the feed keeps answering, from a profile describing a viewer who no
longer exists. A stamp cannot be forgotten — either a new signal moves one of the
counts, or it was not an input.

Building the index parses every title's raw metadata for its keywords, because
nothing persists them. That parse is why the index is cached per library
generation rather than computed per request — and why the index **keeps** the
facets it parsed rather than only their frequencies. Discarding them meant the
profile, the `held` generator and the engine's facet attachment each re-parsed the
whole library on every request; sharing one parse took `held` from 430ms to 38ms
on a four-thousand-title library.

## The engine, in three stages

**Generate** — several strategies contribute candidates and a reason, with no
claim about global order. A generator that throws is skipped, so one dead
strategy costs its own contribution rather than the feed.

| Generator | Origin | TMDb cost |
| --- | --- | --- |
| `seeds` | `/{type}/{id}/recommendations` | 20 per user, cold, shared cache |
| `similar` | `/{type}/{id}/similar` for the top 8 seeds | +8 |
| `people` | `/person/{id}/{movie,tv}_credits` for the profile's top 5 | ~10, cached 30 days |
| `discover` | `/discover/{movie,tv}` from the profile's top genres | 2, cached by query hash |
| `collections` | local `MovieCollection` | **none** |
| `held` | local unwatched titles ranked by the profile | **none** |

Generators are **not** user-facing toggles: a viewer cannot meaningfully choose
between "seeds" and "discover". `/recommendations` and `/similar` are genuinely
different signals — behavioural versus content-based — which is why the cache is
keyed by generator as well as by title.

`discover` sorts by **vote count, not popularity**: asking TMDb for the most
popular titles in a genre returns the blockbusters every other path already
found, which is the bias it exists to escape.

**Score** — one scorer, one unit:

```
score(c) = CF(c) + 0.6·affinity(c) − 0.8·aversion(c) + 0.25·quality(c)
```

- **`CF(c) = ( Σ w(seed)/(pos+1) ) · (1 + ln(seeds))`**, normalized against the
  pool's own maximum. Breadth across a viewer's taste is a **factor, not a veto**
  — ranking by seed count first let two weak old seeds outrank the top pick of a
  film loved yesterday, and amplified popularity bias, since the titles in the
  most lists are by construction the most globally linked.
- **Aversion outweighs affinity.** "Not this" is more specific than liking
  something, and suggesting more of what was just rejected is the failure people
  notice.
- **`quality`** smooths the community score toward the mean by vote count, so
  10.0 from three votes stops outranking real acclaim.
- **Popularity de-bias** divides the collaborative term by `1 + γ·ln(1+popularity)`,
  with γ the per-user **Popular ↔ Deep cuts** dial. Zero is the default and
  reproduces the ordering the feed had before the dial existed.
- **A candidate with no features is scored on the terms it has.** Facet
  similarity drops out and an unknown vote count lands on the prior, never at the
  bottom. Absent evidence must not read as evidence against.

Facets for TMDb candidates come free: the cached list object carries `genre_ids`,
`original_language` and the year, which is three families at zero extra cost.
Local titles are read properly, with keywords and the person graph.

**Re-rank** — greedy MMR against the facets already picked, plus hard caps: two
per franchise, two per director, no genre past 40% of the list. Caps **discard
rather than reorder**, so the scored pool is six times the limit and a list that
runs out of allowed candidates stops short rather than breaking a cap.

The MMR keeps a **running** closeness per candidate, updated against each new pick
rather than recomputed against every pick, and compares pre-built facet sets. The
naive form — rebuild the sets inside the comparison, compare against all picked,
for every candidate, on every pick — is O(picks² × pool) with an allocating inner
loop, and on a four-thousand-title library one request took **110 seconds**. The
same request now takes about 0.4s.

## Reasons

Every card carries why it is there, as **structured data rather than a sentence**:
the server knows what produced a candidate, the client knows how its surface
phrases things and in what language.

A rated seed wins over a bare watch — "because you rated *Arrival* five stars" is
an argument the viewer already agreed with. Otherwise the strategy explains
itself: a franchise, a person, "already in your library", "matches what you
watch".

Rendered as a third line on the recommendations grid, where there is room, and as
a tooltip in the Home row, which keeps the two lines its tile was matched to.

## Cold start

The response names which question it answered, so a weaker answer is not
presented as the ordinary one.

1. **`history`** — built from what the viewer watched and said. The ordinary case.
2. **`library`** — for a viewer with no history: an operator chose to acquire
   every title here, and that is taste before anything is played.

With neither, the engine returns nothing. Trending filler would not be a
recommendation.

## Feed service

The engine deliberately knows nothing about library *state*; the feed service
answers what only the library can.

- **In library** is resolved by TMDb id across every movie and series. Several
  catalogs can hold the same title; all copies are kept, the oldest is what a
  card links to, and the **library's own title wins**. The link carries the
  media-item id, because the detail routes are `{id:guid}`.
- **Watched** excludes a played movie, and a series once *any* episode has been
  played. A play against any copy counts.
- **Hidden** titles are per user and keyed by TMDb identity, so a hide survives
  the title being added or removed.
- Ranking asks for four times the limit and filters afterwards, so excluding
  watched and hidden titles shortens nothing.
- Every candidate carries its own artwork; there is no poster lookup or cache.

## API

```http
GET    /api/recommendations?kind=&limit=
POST   /api/recommendations/hide          { kind, tmdbId }
DELETE /api/recommendations/hide?kind=&tmdbId=
PUT    /api/recommendations/popularity-bias { popularityBias }
PUT    /api/library/{id:guid}/rating      { rating }
DELETE /api/library/{id:guid}/rating
```

All authenticated and scoped to the caller; none accepts a user id, because a
feed is built from what someone watched and serving another user's would leak
exactly that. Hide and unhide are idempotent. `/native/v1/recommendations`
returns the same feed envelope.

## Surface

- A **Recommended for you** row on the home page, rendered only when there is
  something to say, with "See all" leading to `/recommendations`.
- The page filters by kind (`All | Movies | Series`) and availability
  (`Everything | In library | Not in library`), and carries the **Popular ↔ Deep
  cuts** slider.
- A card shows the title, a `Movie · 2010` caption, an amber check when the title
  is held, and its reason.
- A held title links to its detail page. A discovery's poster opens the
  [title preview](../title-preview/feature.md) and the card offers **Track**;
  acquisition stays in [Watchlist and discovery](../watchlist-and-discovery.md).
- Hiding is one click, so undo is one click: the toast carries it.

## Jellyfin surface — the Recommended view

The held half of the feed is also a library on the [Jellyfin
surface](../jellyfin-compatibility/feature.md), so a suggestion is something the
user can press play on.

- A synthetic **Recommended** `CollectionFolder` sits beside the catalog views,
  advertised only while the requesting user's shelf holds something. Its
  `CollectionType` is null (mixed content): the shelf holds series as well as
  films.
- `Items/Latest` returns **the shelf itself**, in rank order — the deliberate
  opposite of the Collections view, and the only per-library hook onto a client's
  home screen. Opening the view returns the same titles in the same order,
  bypassing the alphabetical browse path.
- **Held titles only.** A discovery card has no meaning where the only verb is
  Play.
- The row is labelled by the client from the view's name; Infuse renders it as
  *"Latest Recommended - Local"*.
- A newly appearing view reaches the home screen one refresh late, because the
  client builds it from its cached library list. Nothing to fix server-side.

### The shelf

`RecommendationShelfItem` stores an ordered list of media-item foreign keys per
user, and nothing else. Title, artwork and user data are read live, so the
snapshot pins only what is expensive to recompute and must stay still: which
titles, in what order — the row and the opened grid are two requests that have to
agree.

The `held` generator is what makes this work. The shelf used to be *the discovery
feed intersected with the library*, which could surface a local title only when
TMDb happened to link it to something watched; asking the library directly fills
it every time and costs no requests.

- **100 candidates**, an order of magnitude more than a row shows.
- **Watched and hidden are applied on read**, so a title leaves the moment it is
  played rather than when the generation expires.
- **The generation is recorded separately from the rows**, because an empty shelf
  is still an answer.
- **One-day TTL**, refreshed lazily: a missing shelf is built synchronously, a
  stale one served while a rebuild runs behind the request. Rebuilds are
  single-flight per user — a client fans `Items/Latest` across every library at
  once.

## Evaluation

Every weight above is a claim about how much a signal is worth, and the numbers
live in `RecommendationWeights` so they can be measured rather than asserted. The
offline harness (in the test project) holds out each user's most recent plays,
rebuilds the engine from the remainder inside a transaction it always rolls back,
and reports **recall@20** and **nDCG@20**; `SweepAsync` runs several
configurations over the same history so the numbers are comparable.

Two honest limits: it is only meaningful against a **real** history — a synthetic
one scores whatever rule generated it — and it runs the cached `seeds` lists
only, so it measures the ranking and the profile rather than the reach of the
network generators.

## Not included

Deliberately out of scope: episode-level recommendations, any direct hand-off
from a discovery card into torrent intake, syncing ratings to any provider, and
the two upper rungs of the cold-start ladder (borrowing another user's history,
which is a privacy question; and trending filtered through the profile).

## Testing Expectations

- `RecommendationSeedSelectorTests` — the rating curve per star, 1★/2★ never
  seeding, ratings not decaying while unrated watches do, recency ordering within
  a rating band, a rating superseding the favorite boost, unrated watches
  retiring as ratings accumulate, the reserved recency slots admitting a fresh
  unrated watch and refusing a disliked one, episode→series collapsing, undated
  marks seeding without a bonus, the seed cap, and another user's history never
  seeding this feed.
- `RecommendationEngineTests` — breadth as a factor not a veto, acclaim over a
  perfect score from three votes, a featureless candidate scored on the terms it
  has, the popularity dial demoting a blockbuster and doing nothing at zero,
  seeds never recommended back, held titles reaching the feed with TMDb silent,
  the rated-seed reason, and the cold-start rungs including silence at the bottom.
- `TasteProfileBuilderTests` — facets becoming a profile, IDF damping a common
  genre below a rare one, per-family normalization, role weighting, low ratings
  and abandonment and hides as negatives with a 1★ outweighing a hide, the
  watchlist counting less than a watch, affinity judging a candidate on the
  families it has, and another user's history never reaching this profile.
- `TasteProfileCacheTests` — every input moving the stamp (rating, favorite,
  play, hide, watchlist, library add, re-enrich) and another user's activity not.
- `RecommendationScorer` / `RecommendationRerankerTests` — score order preserved
  when nothing is alike, a slightly worse but different candidate beating a near
  duplicate, and the franchise, director and genre caps including stopping short
  rather than breaking one.
- `LocalGeneratorTests` / `RemoteGeneratorTests` — held offering unwatched
  matches and making no collaborative claim, collections offering the next film
  but never a tracked one, people asking about the right person and skipping one
  with no TMDb id, discover asking by genre and never by popularity, and the
  discovery signature being short, stable and distinct.
- `TmdbRecommendationSourceTests` — cache hits costing no request, TTL expiry,
  stale-payload fallback on an outage, the widened features round-tripping, two
  generators for one seed not overwriting each other, and an old payload version
  read as a miss rather than as a title with no votes.
- `RecommendationFeedServiceTests` — in-library marking and title precedence,
  watched and hidden exclusion, per-user isolation, filtering after ranking
  keeping the feed full, and multi-copy titles.
- `RecommendationShelfServiceTests` — held-only contents, rank preserved,
  read-time exclusion of watched and hidden titles, concurrent readers building
  once, an empty generation not rebuilt on every read but rebuilt once its TTL
  expires, and a deleted title leaving without breaking the rest.
- `RecommendationEvaluationTests` — the harness's arithmetic (recall, nDCG,
  the cut) and that a run leaves the history exactly as it found it.
- `JellyfinRecommendationsTests` — the view advertised only when the shelf has
  something, `Latest` returning it in rank order, browsing keeping rank, and the
  view never appearing in a flat scan.
- `e2e/recommendations.spec.ts` — the library/discovery split, the availability
  filter, hide-with-undo, the reason line, the popularity dial, the
  self-explaining empty state, and the conditional home row.
- `e2e/detail.spec.ts` — rating a movie and a series, and clearing a rating by
  clicking the lit star.
