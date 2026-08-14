# Recommendation Providers — TMDb-only redesign

Status: In Progress
Created: 2026-08-07
Updated: 2026-08-14

> Trakt development is wound down: a self-hosted deployment needs its operator to
> register an OAuth application, and doing that now requires Trakt VIP, so for
> any operator without it the provider can never reach `Connected` and the feed
> has exactly one source.
>
> **Updated 2026-08-14: Trakt recommendations are removed outright.** The plan
> originally kept them, to avoid breaking an operator for whom Trakt still
> works. Building the rest showed the fit was worse than that trade assumed: the
> engine now emits a *shaped* list — MMR plus franchise, director and genre caps
> — and rank fusion took that apart into positions and re-interleaved it with an
> opaque list, which can undo exactly the shaping the re-ranker had just applied.
> The agreement boost, meanwhile, claimed two engines of comparable authority
> where one is scored on features and the other is a black box. With one source
> RRF was order-preserving, so nothing was being damaged today — the cost was
> carried complexity for a case almost nobody can reach.
>
> Removed with it: `RecommendationFusion`, the **Both** badge, the source control
> and the stored source preference, and `TmdbPosterLookup` with its cache table,
> which existed only because a connected account returned no artwork. Trakt
> **watched-history sync is untouched** — see [watched-history
> providers](../watch-history-providers/feature.md); this was only ever about the
> recommendations feed.

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
4. **Nothing in the instance records whether a watched title was any good.**
   `RecommendationSeedSelector` reads a play, a favorite and a rewatch, and a
   favorite is the only opinion available — one bit, and one that also means
   "keep this where I can find it". A film watched and endured seeds the feed as
   loudly as one watched and loved, and there is no way at all to say the first
   thing.

Meanwhile the schema holds genres, keywords, a normalized person graph, movie
collections, community ratings, original language, runtime, watchlist entries and
abandonment state — and the engine reads none of them.

The wind-down turns all three from "worth fixing" into "the whole feature":
fusion across two engines is what [feature.md](feature.md) calls "the strongest
evidence this feature has", and for almost every operator it no longer happens —
which is why it is now gone rather than kept as a path nobody takes.

## Target behavior

Written as a diff against [feature.md](feature.md).

### There is one engine, and generators inside it

`IRecommendationProvider` used to mean both "an engine that produces candidates"
and "a thing the user can toggle in the source control". With Trakt gone the
second meaning has nothing to describe, so the interface, the registry and the
stored source preference go with it — a one-implementation abstraction plus a
setting nobody can express is cost without a reader.

What replaces it is a **generator**: an internal strategy inside the one engine.
Generators are deliberately not user-facing toggles; a viewer cannot meaningfully
choose between "seeds" and "discover", and their output is one scored list.

### The engine is three stages

One stage used to do everything: providers returned ranked lists, fusion merged
positions, the feed service filtered. That shape existed because a connected
account returned positions without scores, so rank was the only unit two sources
had in common.

- **Generate** — several generators contribute candidates and a reason, with no
  claim about global order.
- **Score** — one scorer ranks the pooled candidates in a single unit.
- **Re-rank** — diversity, exclusions and explanations shape the final list.

`RecommendationFusion` is **deleted**. With one source there is nothing to fuse,
and keeping it would mean flattening the engine's own shaped order back into
positions and re-deriving it — losing, in the process, the diversity the
re-ranker had just imposed.

### Star ratings — the explicit signal the schema never had

A watched movie or series can be rated **1–5 stars**. This is a new interaction,
not a ranking change, and it is what problem 4 above is missing: a graded, signed
statement about a title the viewer has actually seen.

**It is not a second favorite.** The two answer different questions and both
stay:

| | Favorite | Rating |
| --- | --- | --- |
| Question | "keep this where I can find it" | "was it any good" |
| Values | one bit | 1–5, or unrated |
| Applies to | any item, seasons included | movies and series only |
| Travels | pushed to a connected provider by `FavoritesRecorder` | stays local |
| Places the title in a list | yes | no |

Neither writes the other. A user who never rates anything keeps exactly today's
behavior, because *unrated* is the default and is scored as it is now.

**Rating movies and series only.** An episode rating has nowhere to go: the seed
selector already collapses an episode play into its series, so "more like episode
4" is not a question the engine can ask. A series is rated as a work.

**Unrated and 1★ are different states,** which is why the column is nullable:
unrated means "no statement", 1★ means "I watched this and it was bad", and the
engine reads them in opposite directions. Clearing a rating is therefore a
first-class action, not a synonym for one star.

#### What each star means

No curve is defensible until the scale is written down, because the weights follow
from the meanings rather than from the numbers:

| ★ | Meaning | Expected share |
| --- | --- | --- |
| ★★★★★ | nothing to fault in it | rare |
| ★★★★ | loved it — where most favorites land | common |
| ★★★ | a good film, no regrets about the time | common |
| ★★ | worth it only with nothing else to do | common |
| ★ | disliked it; the time is the loss | uncommon |

Most titles land in 2–4, and the top of the scale is *reserved* rather than merely
high. Two consequences set the whole shape:

- **The qualitative break is between 3 and 4**, not between 4 and 5. "No regrets"
  and "loved it" are different kinds of statement, while five stars is a stricter
  grade of the statement four stars already makes. So the largest step belongs at
  3→4, and 4→5 is comparatively modest — five is scarce, and scarcity does part of
  the work a multiplier would otherwise have to do.
- **Neutral sits between 2 and 3**, not at 3. "Only with nothing else to do" is
  faint praise, so two stars crosses into the negative.

#### The curve

Everything is expressed as a multiple of **an ordinary unrated watch, which stays
at ×1.0** — today's baseline, so an instance where nobody rates anything ranks
exactly as it ranks now, and every weight below reads as a sentence: a flawless
film is worth six and a half ordinary viewings.

| Stars | Seed weight | Facets in the profile |
| --- | --- | --- |
| ★★★★★ | ×6.5 | positive, full strength |
| ★★★★ | ×4.0 | positive, full strength |
| ★★★ | ×1.7 | positive, weak |
| *unrated watch* | ×1.0 | positive, weak |
| ★★ | not seeded | **negative**, mild |
| ★ | not seeded | **negative**, full strength |

A linear map (`r/3`) would make five stars 1.67× three stars, which is not what
this scale says: it would price "no regrets" at three fifths of "flawless". The
curve above prices the 3→4 step at ×2.35 and the 4→5 step at ×1.6, so a handful of
loved films genuinely drives the feed rather than merely leading it, while one
exceptional title cannot own it.

**Neither 1★ nor 2★ seeds.** Asking TMDb "what is like this" for a film the viewer
would not repeat spends one of the 20 seed requests fetching candidates the scorer
then has to push back down. Their facets are still read — they are removed as
*sources of candidates*, not as evidence, and they are the strongest evidence the
negative profile has: a hide is a judgement about a title never watched, a low
rating is a verdict after watching one.

**A seed and its facets always carry the same sign.** A title cannot coherently
contribute candidates as something the viewer liked while contributing facets as
something they did not, so the boundary between seeding and not seeding is the
same line as the boundary between positive and negative.

**Rewatch still compounds** (×1.25): it is behavioral evidence, a different kind
from a statement, so it multiplies rather than being absorbed.

**When a title is both favorited and rated, the rating wins.** It is the more
specific statement about the same feeling, and compounding ×1.5 with ×6.5 would
price one title at nearly ten ordinary viewings — two and a half loved films at
once, from a single row in the database. `FavoriteBoost` keeps its current meaning
for **unrated** titles, so nothing regresses for a user who does not rate. A
favorite rated 2★ stops seeding altogether, which is right: the shelf placement is
curation, the two stars are the judgement.

#### A rating does not decay at all

```
w(s) = Rewatch(s) · ( Rating(s) ?? Decay(age) · (Favorite(s) ? 1.5 : 1.0) )
```

The recency half-life now applies to the **unrated branch only**. A rating is a
standing statement about taste, taste is stable, and the way to revise it is to
re-rate the title or clear the rating — the viewer saying so — rather than the
engine assuming it after ninety days. Decay is the engine guessing that someone has
changed their mind; once they can simply state it, the guess is unnecessary.

On the 90-day half-life a 5★ from two years ago would have decayed to 0.02, far
below a film watched yesterday and never thought about again: the scale would have
been expressive for one season and then evaporated. Anchoring ratings outside time
is what makes them a profile rather than a mood.

A **favorite still decays**, exactly as today. It is the unrated branch's
modifier, and leaving it there is what keeps an instance with no ratings ranking
precisely as it ranks now.

**Recency does not disappear — it moves into the tie-break.** Rated weights are
discrete constants now, so every 5★ title weighs exactly 6.5, and
`RecommendationSeedSelector` already orders by `weight DESC, lastWatched DESC,
tmdbId`. Among forty films rated five stars, the twenty watched most recently take
the seed slots. That is the honest place for recency under this model: choosing
*between titles the viewer values equally*, rather than devaluing what they valued
long ago. It needs no new code — the existing sort does it the moment the weights
stop being continuous.

#### What that costs, and the four slots that buy it back

With no decay a 3★ (×1.7) outranks any unrated watch (at most ×1.0) permanently.
So once twenty titles are rated 3★ or better, an unrated watch **never** seeds
again — not rarely, never, however recent. The engine would stop knowing what the
viewer watched last week unless they graded it.

That follows from the model and is defensible: an unrated title is one the viewer
did not care enough to grade. But it deletes the only signal today's engine has,
and a "what should I watch next" that has not moved in a month is the failure mode
this surface is most likely to reach.

So the seed set becomes the top **16 by weight plus up to 4 slots** held for the
most recently watched seed-eligible titles that would not otherwise make the cut.
Twenty seeds as today, one budget, and the reserve is exactly where a film watched
last week and not yet rated lives. Ratings still own four fifths of the feed;
recency keeps a corner. If that trade is unwanted, deleting the reserve is a
one-line change and everything above stands without it.

#### Do unrated watches seed at all?

Yes, and at the unchanged ×1.0 — they are the unit everything else is priced in.
Three reasons, and the third settles it:

- **Every instance starts with no ratings**, including every existing one on the
  day this ships. An engine that needs ratings first has nothing to say until the
  user has done a stretch of manual work, and the cold-start ladder would be
  answering the ordinary case rather than the edge one.
- **Most of a library will never be rated.** Rating is manual labor, and a profile
  built from ratings alone would be a narrow sample of a wide history.
- **The seed cap retires them on its own, with no new machinery.**
  `RecommendationSeedSelector` sorts by weight and takes the top slots, so as
  ratings accumulate the unrated ones stop entering the seed set — gradually, in
  proportion to how much the viewer has actually said, and completely once twenty
  ratings exist. Nothing has to decide when the engine "has enough ratings"; the
  existing sort decides it, and the four reserved slots above are what keeps
  "completely" from meaning "the feed stops noticing this week".

In the **taste profile**, which is not capped, unrated titles keep the same ×1.0
and remain the bulk of the input by volume. That is not a problem, because IDF
damping is precisely the thing that strips out what is common to everything a user
watches. What survives the damping is what distinguishes this viewer — and that is
where the rated end lives.

#### What it touches beyond the engine

`UserItemData.Rating` (`int?`, 1–5) sits beside `IsFavorite`, which means every
path that already knows a favorite carries meaning has to learn the same about a
rating — each of these silently discards ratings otherwise:

- `LibraryDeleteService.CollectSignalIdsAsync` and `CatalogService` decide what to
  tombstone rather than purge from `IsFavorite || Played || position || playCount`.
  A rating is exactly such a signal and joins the predicate.
- `RemapService` ORs `IsFavorite` when two items become one; ratings merge as the
  **higher** of the two, the numeric reading of the same rule.
- `RemovedTitlesService.ClearFavoriteAsync` clears favorites across a tombstoned
  subtree. A rating is history, not curation, and **survives** — the same argument
  that keeps watch history on a tombstone. The "forget" action gets an explicit
  clear rather than being folded into the favorite one.
- `UserItemDataDto` gains the field, which reaches both the internal `/api`
  surface and the Jellyfin mapper. It is named `UserRating` rather than `Rating`
  because that DTO *is* the Jellyfin surface's `UserData`, where `Rating` is a
  0–10 double: a 4 emitted under that name would claim "four out of ten" to any
  client reading Jellyfin's schema.

#### Where a rating is given

- **`PUT /api/library/{id:guid}/rating` and `DELETE …/rating`**, mirroring the
  favorite pair the endpoints already expose. A value outside 1–5, or an item that
  is not a movie or series, is a 400 rather than a silently ignored write.
- **The detail page**, in the same action row as Favorite: five stars, and
  clicking the lit star clears the rating — the conventional gesture, and it keeps
  the control to one row.
- **The Favorite button currently *is* a star** (`Star` icon, `media-detail.tsx`).
  A five-star row beside a star-labelled button reads as two controls for one
  thing, so Favorite becomes a heart — which is also what the removed-titles list
  already uses for it. That is the only thing this addition alters on an existing
  surface; the alternative — giving the rating some other mark — spends the one
  shape everybody already reads as a score.
- **Nothing syncs.** Trakt carries ratings, and this deliberately does not push or
  pull them: the wind-down is no moment to add outbound sync to a provider almost
  nobody can connect, and an imported rating would overwrite a local verdict with
  a remote one. `FavoritesRecorder` is not the model to copy here.
- **The Apple clients are unaffected.** `/native/v1` has no favorite endpoint
  today, so ratings follow favorites there: nothing to build until that client
  surface asks for either.

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

- per-user events — a play, a favorite, a hide, a **rating set, changed or
  cleared**, and a **watchlist add or remove**, since `WatchlistEntry` /
  `TrackedTitle` feed the profile as explicit intent;
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
  threshold), the facets shared by hidden titles once enough hides exist to mean
  anything, and — carrying the most weight of the three — **the facets of 1★ and
  2★ titles**. A hide is already a thumbs-down; today it only filters. A low
  rating is the same gesture made after watching, which is why it needs no
  "enough of them exist" threshold to be trusted.
- **`w(s)` is where ratings reach the collaborative term**, so a 5★ seed's
  recommendations arrive close to four times as loud as a 3★ seed's without any
  change to `CF`'s shape. The personal rating and `Quality(c)` are separate
  terms: one is this viewer's verdict on a title they watched, the other is
  TMDb's crowd on a title they have not.

**A candidate with no features is scored on the terms it has.** Facet cosines
drop out, and the candidate rides its collaborative and quality terms. This is
the path a source-supplied candidate takes before enrichment, and it must stay
correct: the alternative — treating "no features" as "zero similarity" — would
sink every candidate that arrives bare — which is every candidate from a
generator that returns ids without features.

### Re-ranking

Greedy MMR against the facet vectors of the already-selected items, plus hard
caps: at most two titles per `MovieCollection`, two per director, and no single
genre past 40% of the top twenty. The current feed has no diversity control at
all, so a franchise marathon produces a feed of that franchise.

Every surviving card carries a **reason** — the seed, person or facet that
contributed most. The contributions are computed either way; keeping the argmax
costs nothing and is the difference between a list and an explanation. When the
winning seed is a rated one the reason says so ("because you rated *Arrival* five
stars"), which is both the most convincing sentence this feature can print and
free: the weight already knows.

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

1. history-based (today's behavior), which **ratings reach first**: a user with a
   handful of rated titles and no recent plays has a real profile, since a rating
   carries a sign and a magnitude where a bare play carries neither;
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

- [x] **Widen the cached candidate shape** to carry `genre_ids`, `vote_average`,
      `vote_count`, `popularity` and `original_language`, with a payload version
      so existing cache rows are treated as a miss rather than misread.
- [x] **Add the generator discriminator to the seed cache** — key, unique index
      and migration — so `/similar` and `/recommendations` for one seed stop
      colliding on `(Kind, TmdbId)`. Existing rows migrate in place as `seeds`.
- [x] **Replace the lexicographic sort** with `Score · (1 + ln(Seeds))`, in a
      `RecommendationScorer` that normalizes the collaborative term against the
      pool before adding quality — so the weights stay meaningful now that a 5★
      seed is worth 6.5 where the old maximum was about 1.9.
- [x] **Quality smoothing and popularity de-bias**, with the `γ` control
      persisted per user beside the existing source preference and surfaced as
      the **Popular ↔ Deep cuts** slider on the recommendations page.

### Phase 2 — star ratings

- [x] **`UserItemData.Rating`** (`int?`, 1–5) with its migration, and the field
      threaded through `UserItemDataDto` and the internal `/api` projection.
- [x] **Rate and clear endpoints** — `PUT`/`DELETE /api/library/{id:guid}/rating`,
      mirroring the favorite pair, rejecting out-of-range values and non-work
      kinds with a 400.
- [x] **A rating survives what it should** — tombstoned rather than purged by
      `LibraryDeleteService` / `CatalogService`, merged as the higher value by
      `RemapService`, and kept by `RemovedTitlesService` (which clears favorites)
      until an explicit clear of its own.
- [x] **The non-linear seed weight** in `RecommendationSeedSelector`: the curve
      table, the recency decay confined to the unrated branch, the unrated ×1.0
      baseline, the four reserved recency slots, and 1★ and 2★ excluded from the
      seed set while still counting as evidence.
- [x] **The rating control** on the detail page, with Favorite moved off the star
      icon so the two gestures stop sharing a mark.

### Phase 3 — the profile

- [x] **Taste profile builder** over genres, keywords, people (role- and
      billing-weighted), decade and original language, with IDF damping and
      per-family normalization. Keywords are the expensive family: nothing
      persists them, so they are parsed out of `MetadataRecord.Raw`, which is
      what makes the library index worth caching rather than computing per
      request.
- [x] **Profile cache** keyed per user, rebuilt from a **stamp of its own
      inputs** rather than by invalidation. Hooking six write paths would mean a
      seventh signal silently ranking against a profile that no longer matches;
      a stamp derived from the inputs cannot be forgotten. The library facet
      generation is part of it, so an added, removed or re-enriched item rebuilds
      the profiles damped against it.
- [x] **Negative signals** — low-rated facets, abandonment and hidden-title
      facets, with a 1★ weighing more than a hide and needing no volume threshold.
      Positive and negative are kept as two vectors, not one signed one: a viewer
      can like thrillers and still have rejected three of them.
- [x] **Positive signals currently ignored** — `WatchlistEntry` / `TrackedTitle`
      as explicit intent feeding the profile (never as output: a tracked title is
      already wanted). Only tracked titles that resolved to a library item carry
      facets; fetching for a pure wishlist row would break the promise that a
      profile costs no requests.

### Phase 4 — generators and scoring

- [x] **Split sources from generators** by adding an internal generator seam.
      Shipped first *behind* the provider, so the stored `library` preference and
      the source control kept their meaning; both were then removed outright with
      Trakt, leaving the seam and one engine.
- [x] **Three-stage pipeline** — generate, score, re-rank. A generator that
      throws is skipped, so one dead strategy costs its own contribution rather
      than the feed.
- [x] **New generators**: `similar`, `discover`, `people`, `collections`,
      `held`. `discover` sorts by vote count rather than popularity — asking
      TMDb for the most popular titles in a genre would return the blockbusters
      every other path already found, which is the bias it exists to escape.
- [x] **Unified scorer** implementing the formula above, including the
      featureless-candidate path. Candidate facets come free from the widened
      cache shape (genres, language, year), so every candidate can be compared
      against the profile at zero request cost; local titles are read properly
      instead.
- [x] **MMR re-rank plus franchise, director and genre caps.** Caps discard
      rather than reorder, so the pool scored is six times the limit and a list
      that runs out of allowed candidates stops short rather than breaking one.

### Phase 5 — surfaces

- [x] **Reasons on the card** in the web feed, and in `/native/v1/recommendations`,
      including the rating-seeded phrasing. Shipped as **structured data, not a
      sentence** — the server knows what produced a candidate, the client knows
      how its surface phrases things and what language the reader wants. The
      anatomy question resolved as: a third line on the recommendations grid,
      where there is room, and a tooltip in the Home row, which keeps the two
      lines it was deliberately matched to.
- [x] **Library-first Jellyfin shelf** — the `held` generator puts every
      unwatched library title into the candidate pool, so the shelf fills from
      the library rather than from *discovery ∩ library*, and costs no requests.
- [ ] **Cold-start ladder** — rungs 1 and 2 ship, and the response names which
      one answered (`history` / `library`). Rungs 3 and 4 do not:
  - [x] history-based, which ratings reach first;
  - [x] the library's own profile, for a viewer with no history;
  - [ ] other users' history on the same instance — a multi-user instance makes
        this a privacy question (whose taste is being borrowed, and do they
        know) that the plan never answered, and guessing it in code is the wrong
        place to decide;
  - [ ] trending filtered through the profile — needs a trending endpoint and
        its own cache, and it is the rung most likely to read as the filler this
        feature refuses to serve.

### Phase 6 — proof

- [x] ~~**Trakt still works when connected**~~ and ~~**a stored `library`-only
      preference survives the refactor**~~ — both dropped, because the surfaces
      they protected are gone. Trakt recommendations, fusion, the agreement
      boost, the **Both** badge, the source control, the stored source preference
      and the poster backfill were all removed; see the note at the top. Trakt
      watched-history sync is untouched and keeps its own suite.
- [x] **An instance where nobody rates anything ranks as it did** — unrated stays
      the ×1.0 unit and the favorite boost stays ×1.5 against it, or the addition
      is a silent ranking change for every existing user.
- [x] **Unrated watches retire as ratings accumulate** — with twenty titles rated
      3★ or better, no unrated watch enters the weighted slots, and with none rated
      they fill the set as they do now. The crossover is the sort's doing, so the
      test is on `RecommendationSeedSelector` alone.
- [x] **A rating does not fade and the reserve still turns over** — a 5★ from
      years ago seeds level with one from yesterday, order between them decided by
      the tie-break; and a film watched last week with no rating reaches the feed
      through a reserved slot even when every weighted slot is taken.
- [ ] **Offline evaluation harness** — hold out each user's most recent plays,
      rebuild the profile from the remainder, and report recall@20 and nDCG@20.
      `PlaybackHistoryEntries` is a genuine time-ordered evaluation set, and
      without this every weight in this document is a guess.
- [ ] **The curve is measured, not asserted** — the harness sweeps the five
      weights, the unrated baseline and the size of the recency reserve, and reports
      whether the rating-weighted profile beats the unweighted one on held-out
      plays. Ratings are sparse at first, so a flat result across a range is itself
      an answer: take the simplest point in it rather than the best-scoring one.
      The harness is also the only honest way to ask whether an undecayed profile
      predicts recent plays as well as a decayed one does — it is the question this
      design answers by conviction and the evaluation can answer with data.
- [ ] **Unit tests** across the new units, and the existing suites updated.
- [ ] **`feature.md` rewritten**, this plan deleted, index regenerated, and a
      minor version bump (from whatever `manifest.json` carries when the work
      lands — 0.58.2 at the time of writing — to the next minor).

## Open questions

- ~~**Should an explicit positive signal be added?**~~ Answered: star ratings, in
  the section above. What remains open is whether **`RecommendationHide` stays a
  separate gesture** now that 1★ exists. It should: a hide is about a title in the
  feed that was never watched, a 1★ is a verdict on one that was, and the two
  never apply to the same title — a watched title is already excluded from the
  feed. Worth re-checking once both are live rather than merging them on paper.
- **Does the Jellyfin surface get ratings?** The protocol has both a 0–10
  `UserData.Rating` and a `Likes` thumb, and `/Users/{id}/Items/{id}/Rating`
  mirrors the `FavoriteItems` endpoints already implemented — a 1–5 star maps to
  it by doubling. Whether Infuse writes or renders either needs checking against a
  real client before it becomes a deliverable; mapping a field no client touches
  is work with no reader. Until then the DTO carries `UserRating`, which no
  Jellyfin client interprets and so cannot misreport.
- **How does a rating merge when two copies of a work carry different ones?**
  `RemapService` ORs favorites, so the higher rating is the numeric reading of the
  same rule and is what shipped — but "the most recent statement wins" is equally
  defensible and would need the rated-at instant stored.
- **How many facet families earn their weight?** Five are proposed. Keywords and
  people are likely the discriminating ones; decade and language may be noise
  that the evaluation harness should be allowed to delete.
- **Should the shelf's rows become several Jellyfin views** ("because you watched
  X", "more from Y") rather than one *Recommended*? Each would land as its own
  row on the client's home screen, which is attractive — and each also adds a
  library tile the user did not ask for.
- **Is `/discover` worth its complexity** before the evaluation harness can show
  it beats `similar` at reaching the long tail?
- ~~**Does the agreement boost survive?**~~ Answered by deleting it. It was tuned
  for two engines of comparable authority, and there is one engine.

## Verification steps

1. `dotnet test` for the API test project; `pnpm test` and the Playwright suite
   for the web project.
2. Run the offline harness against a real history and record recall@20 and
   nDCG@20 for the current engine and the new one — the comparison, not the
   absolute number, is the result.
3. Confirm on a live instance that a franchise marathon no longer produces a
   single-franchise feed, and that each card names a reason.
4. Rate a watched film 5★ on a live instance and confirm its neighbours rise; rate
   another 1★ and confirm it stops seeding and that titles sharing its facets fall.
   Clear both and confirm the feed returns to where it started — a rating that
   cannot be taken back is a trap.
5. Delete a rated title with `deleteUserData` off, then confirm the rating is
   still there on the tombstone; run the "forget" action and confirm it goes.
6. Confirm the Jellyfin *Recommended* view fills from the library alone, with the
   TMDb key removed to prove it costs no requests.
7. Confirm a user with no playback history gets the library-profile rung rather
   than an empty feed, and that the response says which rung answered.
8. Confirm Trakt **watched-history sync** still connects, syncs and pushes
   favorites — the removal touched only the recommendations feed, and that is
   the claim worth checking rather than assuming.
9. Refresh a catalog and confirm the cached profiles rebuild rather than serving
   facets damped against the previous library.
