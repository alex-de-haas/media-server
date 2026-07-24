# Recommendation Providers

Status: Draft
Created: 2026-07-24
Updated: 2026-07-24

## Motivation

Media Server now records a complete per-play watch history locally
(`PlaybackHistoryEntries`: exact timestamps, origins, provider links). That history
is exactly the input a recommendation surface needs, but the application offers no
"what should I watch next" answer anywhere — users leave the app to find the next
title, then come back to add it.

Two usable sources already exist behind credentials the instance holds today:

- **TMDb** exposes per-title `GET /movie/{id}/recommendations` and
  `GET /tv/{id}/recommendations` (collaborative "watchers of X also watched Y"),
  reachable with the instance's existing metadata key. Verified live on 2026-07-24.
  The `/similar` endpoints were evaluated and rejected: metadata-based similarity
  returns thousands of weakly ranked results and noise at the top.
- **Trakt** exposes account-personalized `GET /recommendations/movies|shows` for a
  connected user, driven by the history this app already exports. Verified live on
  2026-07-24 on a **free** account (an earlier 401 was a dying token, not a VIP
  gate). Trakt also supports hiding a recommendation
  (`DELETE /recommendations/{type}/{id}`).

Both return TMDb ids, so results map onto the library through the existing
identity machinery (`WatchHistoryIdentity`, `PublicIdFactory` precedence).

## Current Constraints

- TMDb recommendations are **per title**, not per account: personalization has to
  be synthesized locally by seeding from the user's own history. TMDb v4 account
  recommendations exist but require a TMDb *user* account, which this app
  deliberately does not manage.
- Trakt recommendations require that user's connected Trakt account, which is
  optional and per-user (`WatchHistoryConnections`). Free Trakt accounts allow one
  connected Community App, so a Trakt-only design would exclude users who keep
  their single slot for another app.
- Trakt returns an ordered list without scores; TMDb returns paged results with
  vote metadata per seed. Merging needs a rank-based rule, not raw score math.
- The TMDb path fans out one request per seed title. TMDb rate limits are
  generous but not free; per-title recommendation lists change slowly.
- Recommendations are per app user (per-user history, per-user Trakt connection),
  so caching and hide lists must be user-scoped. Nothing may leak across users.
- Recommended titles will usually **not** be in the library. The surface must be
  honest about that and should hand off to the existing add flows rather than
  pretend playback is available.

## Possible Approaches

### Approach A: Single Built-In Engine (TMDb Only)

One local engine: seed from recent/frequent local plays, fan out to TMDb per-title
recommendations, aggregate, rank, filter.

Pros:

- Works for every user with zero configuration and no external account.
- One code path, no merge rules.

Cons:

- Ignores the strictly better personalized signal available to Trakt-connected
  users — their whole cross-app history feeds Trakt's engine, not just what this
  instance saw.
- A future third source (another tracker) would have nowhere to plug in.

### Approach B: Trakt Pass-Through Only

Render Trakt's recommendation feed for connected users; nothing otherwise.

Pros:

- Cheapest implementation; Trakt does all ranking.

Cons:

- No recommendations at all for users without Trakt (including free-account users
  whose single Community App slot is taken by another client).
- Reuses none of the local history investment.

### Approach C: Recommendation Provider Abstraction (chosen direction)

A provider boundary mirroring the watched-history design: an
`IRecommendationProvider` with a capability descriptor, two v1 implementations,
and a per-user merge stage.

- **Built-in provider (TMDb + local history).** Always available. Seeds = this
  user's recently watched and most-rewatched items (recency-weighted); one TMDb
  recommendations call per seed (cached per title); aggregate candidates, rank by
  how many seeds recommend them and how fresh those seeds are; drop everything
  the user has already watched.
- **Trakt provider.** Available when that user's Trakt connection exists and is
  healthy; reads both recommendation feeds; maps ids locally; surfaces Trakt's
  own order as the rank.
- **Merge stage.** Each enabled provider yields a ranked list; lists are fused
  rank-based (reciprocal-rank style), and a title present in **more than one
  provider's list gets boosted** — agreement between independent engines is the
  strongest signal available. One provider enabled = merge is identity.

Pros:

- Every user gets recommendations; Trakt upgrades them instead of gating them.
- Matches the proven watched-history provider pattern (registry, capabilities,
  per-user connections) — same mental model, same testing shape.
- The merge stage is where a future provider (or a local ML ranker) plugs in
  without touching the UI.

Cons:

- Two providers plus a merge stage is the most code of the three approaches.
- Rank fusion needs care so one provider's long tail does not drown the other's
  head (bounded list lengths per provider before fusion).

## Risks

- **TMDb fan-out cost.** N seeds × 1 request on a cold cache. Mitigation: cap
  seeds (e.g. 20), cache per-title recommendation lists for days (they move
  slowly), refresh the user's feed on a schedule or explicit pull, never on every
  page view.
- **Feedback loop.** Recommending what the library already holds is useful but
  different from discovery. The surface should separate "in your library" from
  "not in your library" rather than mixing ranks silently.
- **Hide semantics diverge.** Trakt supports server-side hide; TMDb has no such
  concept. A local per-user hide list must apply to the merged feed; propagating
  a hide to Trakt (when the item came from Trakt) is optional polish and must
  never be required for the local hide to work.
- **Identity gaps.** A recommended TMDb id that maps to several library items
  (multi-catalog duplicates) or to none must degrade the same way the
  watched-history mapper does: deterministic, reported, never guessed.

## Open Questions

- Where does the surface live: a home-page row, a dedicated page under Browse, or
  both (row capped, page full)?
- Do not-in-library recommendations link into the torrent add flow directly, or
  only into a metadata detail view with an explicit add action?
- Seed selection tuning: how many seeds, recency half-life, whether favorites
  count extra.
- Does hiding a merged item that appeared in both providers also call Trakt's
  hide endpoint, or stay purely local in v1?
- Per-user provider toggles: is "both enabled" the default when Trakt is
  connected, or opt-in?

## Current Recommendation

Approach C. Build the provider abstraction with the built-in TMDb engine first —
it serves every user and forces the seed/cache/rank mechanics into shape — then
add the Trakt provider as a thin adapter over the already-stored connection, and
finish with rank fusion and the boost-on-agreement rule. UI ships once, against
the merged feed.

## Links

- Watched-history provider foundation: `docs/ideas/trakt-watched-state-sync.md`
  (Promoted) and `docs/planning/trakt-watched-state-sync.md`.
- Trakt recommendations API: `GET /recommendations/movies`,
  `GET /recommendations/shows`, `DELETE /recommendations/{type}/{id}`.
- TMDb recommendations API: `GET /movie/{id}/recommendations`,
  `GET /tv/{id}/recommendations`.

## Notes

- Live probes on 2026-07-24: Trakt shows-recommendations returned a ranked
  personalized list (tmdb ids present) on a free account; movies came back empty
  only because the account's post-wipe history held almost no movies. TMDb
  per-title recommendations returned plausible lists; `/similar` did not.
- Trakt free-tier context: one Community App per free account, so the built-in
  provider is the only path for users whose slot is occupied by another client.
