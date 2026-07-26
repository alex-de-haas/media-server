# Trakt Favorites Sync

Status: Draft
Created: 2026-07-26
Updated: 2026-07-26

## Goal

Make the local per-user favorite flag portable through Trakt: a movie or series
favorited in Media Server appears in the user's Trakt favorites, and favorites
kept on Trakt appear locally. Besides portability this feeds Trakt's
recommendation engine — the same engine whose feed the
[recommendation providers](../recommendation-providers/feature.md) feature
already consumes — so favorites strengthen both the local seed weighting and
the Trakt-side feed.

## Background

What exists today, and what this plan changes:

- **Local favorites.** `UserItemData.IsFavorite` is per user and settable on
  any library item — movie, series, season, or episode — through
  `POST`/`DELETE /library/{id}/favorite`. Favorites already boost local
  recommendation seeds (`RecommendationSeedSelector.FavoriteBoost`). None of
  this changes; sync is layered on top.
- **Trakt plumbing.** The watched-history integration
  ([Watched-History Providers: Trakt](../../planning/trakt-watched-state-sync.md))
  already ships the per-user Trakt OAuth connection, token storage in the Hosty
  Core secrets store, TMDb→Trakt identity resolution, a durable outbox with a
  delivery worker, and an explicit **Sync with Trakt** preview/apply flow. This
  feature reuses the same connection — important because a free Trakt account
  allows only one connected app — and extends the same pipeline instead of
  building a parallel one.
- **Trakt favorites API.** `GET /sync/favorites/movies|shows`,
  `POST /sync/favorites`, `POST /sync/favorites/remove`. Only movies and shows
  can be favorited — no seasons or episodes. Favorites are capped at **100
  items total for all users** (VIP does not raise it); exceeding the cap fails
  the add request with **HTTP 420 Account Limit Exceeded**.
  `/sync/last_activities` exposes `favorited_at` for cheap change detection,
  and add/get responses carry `list.item_count`.

## Target Behavior

### Scope of synced items

Only movies and series sync. Favorites on seasons and episodes remain
local-only — Trakt cannot represent them — and are excluded from every sync
surface. They also never consume the 100-item budget. The exclusion is
communicated once — a line in the Trakt settings section and a mention in the
sync preview when such favorites exist — not with per-item badges.

### Duplicates during the transition

Until [single catalog per title](../single-catalog-per-title/plan.md) ships
and its audit clears pre-existing pairs, one work can still be several
library items. For sync purposes a work is favorited when **any** of its
items is, and an unfavorite pushes a removal only when no item for that
identity remains favorited.

### Outbound (push)

Favoriting or unfavoriting a movie or series enqueues an outbox event; the
delivery worker maps the identity and calls the Trakt add/remove endpoint.
Local behavior never blocks on Trakt, matching the watched-history rule.

A removal reaches Trakt **only** from an explicit unfavorite action recorded
as an event. A row that merely disappears — a tombstone's full purge
(`deleteUserData`), any bulk cleanup — pushes nothing: absence is never
interpreted as an unfavorite. Purging local history is data hygiene, not a
statement about the work. This also dictates where events are born: in the
favorite/unfavorite endpoints themselves, never in a `SaveChanges` hook —
bulk deletes bypass the change tracker, so a hook would turn every purge into
whatever the hook happens to infer.

HTTP 420 is classified as a **terminal** event failure, not a retryable one:
retrying cannot succeed until the user frees space on Trakt. The local favorite
is kept, the item is marked as not synced, and the state is visible in the same
Settings surface that reports watch-history sync — a silent Kodi-style swallow
of the 420 is explicitly what this design avoids.

### Inbound (pull)

The explicit **Sync with Trakt** flow gains a favorites section: the preview
shows which favorites would be added or removed on each side, and apply
performs the reconciliation. To distinguish "added remotely" from "removed
locally" (and vice versa) the connection keeps a per-item favorites sync state
(remote presence and `favorited_at` as of the last reconciliation), mirroring
how watched-history stores resolved remote ids.

An inbound favorite naming a title the library lacks first tries to match a
**tombstone** by identity
([library item tombstones](../library-item-tombstones/feature.md)) — a
deleted-but-loved title lands back on its ghost. What matches nothing is
skipped, and the preview says so with a visible count rather than silently.

There is no automatic background polling and no one-time import at
connection: the first explicit sync *is* the import. This deliberately
mirrors the watched-history decision — explicit sync is the only mass write.

### Limit handling

- The known Trakt-side count (`list.item_count`) is stored on the connection
  and surfaced in Settings ("97/100"), with a warning as the cap nears.
- On 420 the affected items enter a visible "limit exceeded" state.
- Media Server never evicts Trakt favorites automatically to make room;
  freeing space is always an explicit user action.

## Deliverables

- [ ] Favorites sync state on the Trakt connection (per-item remote presence,
      `favorited_at`, remote count), with migration.
- [ ] Outbox events for favorite/unfavorite of movies and series; delivery via
      `POST /sync/favorites` / `POST /sync/favorites/remove`; 420 classified
      terminal with a per-item visible failure state.
- [ ] Favorites reconciliation in the Sync with Trakt preview/apply flow:
      deletion propagation in both directions, tombstone matching for inbound
      favorites, and a visible skipped count for titles not in the library.
- [ ] Settings UI: favorites count against the 100 cap, limit warnings,
      per-item sync failure visibility beside the existing sync status, and
      the one-line note that only movies and series sync.
- [ ] Backend xUnit tests (Imposter for Trakt/API mocks) covering push, pull,
      deletion propagation, identity misses, and the 420 path; frontend tests
      for the new Settings and preview surfaces.
- [ ] Live contract verification against the dedicated Trakt test account.
- [ ] `feature.md` for this folder, `plan.md` deleted, index regenerated.

## Phases

Implemented on one branch, one PR (per repository PR rules):

1. **Push.** Sync state storage, outbox events, delivery, 420 handling.
2. **Pull.** Reconciliation inside the explicit sync preview/apply flow.
3. **Surfaces.** Settings counter, warnings, failure states; docs and live
   verification.

## Out of Scope

- **Landing library-absent inbound favorites as tracked titles.**
  `TrackedTitle` (`MediaItemId` nullable) is the natural home for a favorite
  the library cannot hold, but wiring favorites into release tracking is a
  separate future feature; v1 skips what matches neither an item nor a
  tombstone.
- **Automatic inbound polling** via `/sync/last_activities`. If it ever
  arrives, it arrives as one decision for watched history and favorites
  together — never as a favorites-only exception.
- **Trakt favorite notes and custom ordering.** Trakt supports per-favorite
  notes and reordering; nothing local models either.

## Verification

- `dotnet test` for the backend suites; frontend test run for the new UI.
- Imposter-mocked 420 path: item enters and leaves the limit-exceeded state.
- Live two-way check with the dedicated Trakt test account: favorite and
  unfavorite propagate in both directions; counts match `list.item_count`.
