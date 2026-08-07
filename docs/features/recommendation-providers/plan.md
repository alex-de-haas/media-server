# Recommendation Providers — TMDb-only redesign

Status: Draft
Created: 2026-08-07
Updated: 2026-08-07

> Trakt development is wound down: a self-hosted deployment needs its operator to
> register an OAuth application, and doing that now requires Trakt VIP, so for
> any operator without it the provider can never reach `Connected` and the feed
> has exactly one source. The code stays — see [watched-history
> providers](../watch-history-providers/feature.md) — so this plan makes the
> built-in engine carry the feature alone **without** breaking the VIP operator
> for whom Trakt still works.

## Goal

Turn the recommendation feed from "TMDb's per-title lists, aggregated" into a
ranked result over a taste profile built from data this instance already holds —
without asking TMDb for meaningfully more than it is asked for today.

Three problems drive it, all visible in the current code:

1. **Ranking is lexicographic, not weighted.**
   `LibraryRecommendationProvider` sorts by the *number* of seeds that
   recommended a candidate and only then by score, so a title reached by two weak
   old seeds unconditionally outranks the top recommendation of a favorite
   watched yesterday.
2. **That amplifies popularity bias.** TMDb's `/recommendations` already leans
   popular, and the titles appearing in the most seed lists are by construction
   the most globally linked ones. Ranking by list membership puts them first, so
   every user's feed converges on the same blockbusters.
3. **The candidate pool has a hard ceiling of 20 seeds × 20 results**, heavily
   overlapping. `RecommendationFeedService` already documents what this costs the
   Jellyfin shelf: after the in-library filter the pool is "a handful". A shelf
   built as *discovery ∩ library* can only surface a local title when TMDb
   happens to link it to something watched.

Meanwhile the schema holds genres, keywords, a normalized person graph, movie
collections, community ratings, original language, runtime, watchlist entries and
abandonment state — and the engine reads none of them.

The wind-down turns all three from "worth fixing" into "the whole feature":
fusion across two engines is what [feature.md](feature.md) calls "the strongest
evidence this feature has", and for almost every operator it no longer happens.

## Target behavior

Written as a diff against [feature.md](feature.md).

### Sources and generators become different things

Today `IRecommendationProvider` means both "an engine that produces candidates"
and "a thing the user can toggle in the source control". Splitting them is what
lets the engine grow without turning the UI into a panel of knobs:

- A **source** is an entry in the source control, with a stable key and a stored
  preference. There are two: the built-in engine, which **keeps the `library`
  key it has today**, and Trakt.
- A **generator** is an internal strategy *inside* the built-in source. Generators
  are not user-facing toggles; a user cannot meaningfully choose between "seeds"
  and "discover", and their output is one scored list carrying one key.

The built-in engine staying a source is load-bearing, not cosmetic. Reserving
`IRecommendationProvider` for external accounts alone would break three things at
once:

- `SelectedSourcesAsync` filters the stored preference against the available
  keys and falls back to "everything" when nothing survives. A user who had
  narrowed the feed to `library` would have their preference read as *vanished*
  and Trakt switched **on** — the opposite of what they chose.
- The source control appears only once a second source exists. With Trakt as the
  only source there is never a second, so a VIP operator could never turn it off
  again.
- Fusion needs two ranked lists to have anything to fuse, and the **Both** badge
  needs two source keys to report agreement between.

So the control still appears only once Trakt is connected, exactly as now, the
stored `library` preference keeps meaning what it meant, and for the common
single-source instance the feed simply gets better.

### The engine becomes three stages

Today one stage does everything: providers return ranked lists, fusion merges
positions, the feed service filters. That shape existed because Trakt returned
positions without scores, so rank was the only common unit.

- **Generate** — several generators contribute candidates and a reason, with no
  claim about global order.
- **Score** — one scorer ranks the pooled candidates in a single unit.
- **Re-rank** — diversity, exclusions and explanations shape the final list.

`RecommendationFusion` is **not** deleted. A connected source still arrives as
positions without features, and rank fusion remains the honest way to merge that
with a scored list: the scorer emits a rank, the source has a rank, and RRF plus
the agreement boost combines them as it does today. What changes is that fusion
is no longer how the built-in engine ranks *within itself*.

### The taste profile

New, per user, built entirely from local data at zero request cost. Each watched
title contributes its existing seed weight to facet families: genres, keywords,
people, decade, original language.

- **People are weighted by role**, not merely by presence: director 1.0, writer
  0.6, cast by billing order — a lead says more about a viewer's choice than the
  eleventh credit. `MediaItemPerson` already carries `Order`, `Job` and
  `Department`, so this is a join, not a fetch.
- **Facets are IDF-damped against the library's own frequencies** before use.
  Without it "Drama" dominates every profile and every user's profile looks
  alike; the point of a profile is what distinguishes this viewer.
- Each family is normalized separately, so a title with forty keywords cannot
  outvote one with four.

The profile is cached per user and invalidated by **every input it is built
from**, which is more than the per-user signals:

- per-user events — a play, a favorite, a hide, and a **watchlist add or
  remove**, since `WatchlistEntry` / `TrackedTitle` feed the profile as explicit
  intent;
- **library and metadata changes** — an item added, removed or re-enriched moves
  the IDF denominator the whole profile is damped against, and a re-identified
  title changes the facets of a seed already counted.

Library-wide changes invalidate every user's profile, so they are folded into a
cheap **library facet generation** counter rather than fanned out per user: the
profile records the generation it was built against and is rebuilt when it no
longer matches. Without this the cache goes stale indefinitely — nothing else in
the design would ever notice a catalog refresh.

### Scoring

```
score(c) = β_cf·CF(c) + Σ_family β_f·cos(profile_f, facets_c) + β_q·Quality(c) − Penalty(c)
```

- **`CF(c) = ( Σ_seeds w(s)/(pos+1) ) · (1 + ln(seedCount))`** replaces the
  lexicographic sort. Breadth across a viewer's taste still wins — that judgement
  in the current design is right — but as a factor rather than as a veto.
- **`Quality(c)`** is the vote average smoothed toward the mean by vote count.
  TMDb reports 10.0 on three votes, and the engine currently has no way to
  distinguish that from a genuinely acclaimed title.
- **Popularity de-bias** divides the collaborative term by `1 + γ·ln(1+popularity)`.
  `γ` is exposed as a **Popular ↔ Deep cuts** control: on a self-hosted instance
  the operator is the right person to hold that dial, and there is no defensible
  single default.
- **`Penalty`** covers what the engine currently ignores entirely: a title
  started and abandoned (`PlaybackPositionTicks > 0 && !Played` below the resume
  threshold), and the facets shared by hidden titles once enough hides exist to
  mean anything. A hide is already a thumbs-down; today it only filters.

**A candidate with no features is scored on the terms it has.** Facet cosines
drop out, and the candidate rides its collaborative and quality terms. This is
the path a source-supplied candidate takes before enrichment, and it must stay
correct: the alternative — treating "no features" as "zero similarity" — would
sink every Trakt suggestion to the bottom and quietly disable the source for the
operator paying for it.

### Re-ranking

Greedy MMR against the facet vectors of the already-selected items, plus hard
caps: at most two titles per `MovieCollection`, two per director, and no single
genre past 40% of the top twenty. The current feed has no diversity control at
all, so a franchise marathon produces a feed of that franchise.

Every surviving card carries a **reason** — the seed, person or facet that
contributed most. The contributions are computed either way; keeping the argmax
costs nothing and is the difference between a list and an explanation.

### Candidate generators

All of these live inside the `library` source; the keys below name generators,
not source-control entries.

| Key | Endpoint or origin | TMDb cost | Reaches |
| --- | --- | --- | --- |
| `seeds` | `/{type}/{id}/recommendations` (unchanged) | 20 per user, cold, shared cache | behavioural neighbours |
| `similar` | `/{type}/{id}/similar` for the top seeds | +8, same table once keyed apart | content neighbours — a different signal from `/recommendations`, not a synonym |
| `discover` | `/discover/{movie,tv}` from the profile's top facets | 4–8, cached by facet signature | the long tail nothing links to |
| `people` | `/person/{id}/{movie,tv}_credits` | ~5, cached 30 days | "more from this director" |
| `collections` | local `MovieCollection` + `TrackedTitle` | **none** | the next film in a franchise already watched |
| `held` | local unwatched items scored against the profile | **none** | the Jellyfin shelf |

**The seed cache needs a discriminator before `similar` can exist.**
`TmdbRecommendationCacheEntry` is unique on `(Kind, TmdbId)` and
`TmdbRecommendationSource` reads by exactly those two, so a `/similar` payload
and a `/recommendations` payload for the same seed cannot coexist: whichever was
written first would answer both generators, and the second signal would silently
become a copy of the first. The key gains the generator, with a migration — and
the existing rows are `seeds` rows, so they migrate in place rather than being
discarded.

### The Jellyfin shelf becomes library-first

The shelf stops being *the discovery feed intersected with the library* and
becomes *the library ranked by the profile*. It fills its hundred slots every
time, costs no requests, and every row is playable — which is the only verb that
surface has. Discovery-only titles remain a web concern, exactly as
[feature.md](feature.md) already argues.

### Cold start

Today a user with no history gets nothing, on the reasoning that trending filler
is not a recommendation. That reasoning holds for filler; it does not hold for
the two rungs below it, which the instance can answer honestly:

1. history-based (today's behavior);
2. **profile built from what the library holds** — an operator chose to acquire
   those titles, and that is taste before anything is played;
3. other users' history on the same instance, where more than one user exists;
4. trending *filtered through* the profile, labelled as such.

### Free fidelity from the existing request

`TmdbRecommendationSource.Read` keeps `id`, `title`, `year` and `poster_path` and
discards the rest of each result — but a TMDb list object also carries
`genre_ids`, `vote_average`, `vote_count`, `popularity` and `original_language`,
and the cache persists only the projection, so the data cannot be recovered
without a refetch. Widening the cached shape gives the scorer features for every
candidate at **zero** additional requests, and is a prerequisite for the quality,
popularity and genre terms above.

For candidates that arrive without features — a source's, or a generator's that
returns bare ids — the enrichment path already exists: `TmdbTitleDetailCacheEntry`
holds full `append_to_response` payloads for titles the library does not own,
shared with the title-preview surface. Only the top candidates after first-pass
scoring are enriched, so the cost is bounded by what a user will actually see.

`TmdbPosterLookup` and `TmdbPosterCacheEntry` **stay**: they exist because a
connected source returns no artwork, and that operator still exists. What changes
is that they become a path the common instance never takes, since every generator
returns `poster_path` inline.

## Deliverables

Implemented on one branch as one PR, per AGENTS.md.

### Phase 1 — foundations

- [ ] **Widen the cached candidate shape** to carry `genre_ids`, `vote_average`,
      `vote_count`, `popularity` and `original_language`, with a payload version
      so existing cache rows are treated as a miss rather than misread.
- [ ] **Add the generator discriminator to the seed cache** — key, unique index
      and migration — so `/similar` and `/recommendations` for one seed stop
      colliding on `(Kind, TmdbId)`. Existing rows migrate in place as `seeds`.
- [ ] **Replace the lexicographic sort** with `Score · (1 + ln(Seeds))`.
- [ ] **Quality smoothing and popularity de-bias**, with the `γ` control
      persisted per user beside the existing source preference.

### Phase 2 — the profile

- [ ] **Taste profile builder** over genres, keywords, people (role- and
      billing-weighted), decade and original language, with IDF damping and
      per-family normalization.
- [ ] **Profile cache** keyed per user, invalidated by play, favorite, hide and
      watchlist mutation, plus a library facet generation so an added, removed or
      re-enriched item rebuilds the profiles damped against it.
- [ ] **Negative signals** — abandonment and hidden-title facets.
- [ ] **Positive signals currently ignored** — `WatchlistEntry` / `TrackedTitle`
      as explicit intent feeding the profile (never as output: a tracked title is
      already wanted).

### Phase 3 — generators and scoring

- [ ] **Split sources from generators** by adding an internal generator seam
      *behind* the built-in provider, which keeps its `library` source key so
      stored preferences, the source control's second-source condition and the
      agreement badge all keep their current meaning.
- [ ] **Three-stage pipeline** — generate, score, re-rank.
- [ ] **New generators**: `similar`, `discover`, `people`, `collections`,
      `held`.
- [ ] **Unified scorer** implementing the formula above, including the
      featureless-candidate path.
- [ ] **MMR re-rank plus franchise, director and genre caps.**

### Phase 4 — surfaces

- [ ] **Reasons on the card** in the web feed, and in `/native/v1/recommendations`.
      The card's anatomy is now fixed — title, then a `kind · year` caption, then
      the amber held check — so a reason is a third line competing with two that
      were deliberately matched to an ordinary poster tile, and may belong in the
      preview rather than on the tile.
- [ ] **Library-first Jellyfin shelf.**
- [ ] **Cold-start ladder**, with the fallback rung labelled in the response so
      the UI can say which question it answered.

### Phase 5 — proof

- [ ] **Trakt still works when connected** — fusion, the agreement boost, the
      **Both** badge, the source control and poster backfill all exercised
      against a stubbed connected source, so the wind-down does not become a
      silent regression for a VIP operator.
- [ ] **A stored `library`-only preference survives the refactor** — with Trakt
      connected it must keep Trakt off, not be read as a vanished source and fall
      back to enabling everything.
- [ ] **Offline evaluation harness** — hold out each user's most recent plays,
      rebuild the profile from the remainder, and report recall@20 and nDCG@20.
      `PlaybackHistoryEntries` is a genuine time-ordered evaluation set, and
      without this every weight in this document is a guess.
- [ ] **Unit tests** across the new units, and the existing suites updated.
- [ ] **`feature.md` rewritten**, this plan deleted, index regenerated, and a
      minor version bump (from whatever `manifest.json` carries when the work
      lands — 0.52.1 at the time of writing — to the next minor).

## Open questions

- **Should an explicit positive signal be added?** `RecommendationHide` is a
  thumbs-down used only as a filter. A paired "more like this" would be the
  cheapest strong signal available, and there is no user rating anywhere in the
  schema today — but it is a new interaction, not a ranking change.
- **How many facet families earn their weight?** Five are proposed. Keywords and
  people are likely the discriminating ones; decade and language may be noise
  that the evaluation harness should be allowed to delete.
- **Should the shelf's rows become several Jellyfin views** ("because you watched
  X", "more from Y") rather than one *Recommended*? Each would land as its own
  row on the client's home screen, which is attractive — and each also adds a
  library tile the user did not ask for.
- **Is `/discover` worth its complexity** before the evaluation harness can show
  it beats `similar` at reaching the long tail?
- **Does the agreement boost survive?** It was tuned for two engines of
  comparable authority. With one scored engine and one rank-only source that
  almost nobody can connect, 1.5 per extra agreeing provider may now be
  overstating a source the scorer cannot inspect.

## Verification steps

1. `dotnet test` for the API test project; `pnpm test` and the Playwright suite
   for the web project.
2. Run the offline harness against a real history and record recall@20 and
   nDCG@20 for the current engine and the new one — the comparison, not the
   absolute number, is the result.
3. Confirm on a live instance that a franchise marathon no longer produces a
   single-franchise feed, and that each card names a reason.
4. Confirm the Jellyfin *Recommended* view fills from the library alone, with the
   TMDb key removed to prove it costs no requests.
5. Confirm a user with no playback history gets the library-profile rung rather
   than an empty feed, and that the response says which rung answered.
6. With a stubbed connected source, confirm the feed still fuses, badges
   agreement, and backfills posters — the VIP path, which no local instance can
   exercise for real — and that a preference stored as `library` still means
   "built-in only" rather than falling back to every source.
7. Refresh a catalog and confirm the cached profiles rebuild rather than serving
   facets damped against the previous library.
