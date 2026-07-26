# Trakt Favorites Sync

Created: 2026-07-26
Updated: 2026-07-26

The local favorite flag is portable through Trakt: a movie or series favorited
here reaches the user's Trakt favorites, and favorites kept there arrive here.
Besides portability this feeds Trakt's recommendation engine — the same one
whose feed [recommendation providers](../recommendation-providers/feature.md)
already consumes — so favorites strengthen both the local seed weighting and
the Trakt-side feed.

It reuses the existing Trakt connection: no second OAuth, which matters
because a free Trakt account allows only one connected application.

## What syncs

Movies and series only. Trakt holds favorites for works, so a favorited season
or episode stays local rather than being approximated by its series — the
Settings section says so in one line instead of badging every card. Those
favorites also never consume the remote cap.

While a work can still be two library items (a duplicate pair predating
[single catalog per title](../single-catalog-per-title/feature.md)), it counts
as favorited when **any** copy is, and an unfavorite only travels once the last
copy is cleared.

## Outbound

An explicit favorite or unfavorite enqueues an outbox event, delivered by the
same worker as watched history. Two rules bound it:

- **Only an explicit action queues anything.** The event is staged in the
  favorite endpoints, never in a `SaveChanges` hook — bulk deletes bypass the
  change tracker anyway — so a row that merely disappears (a tombstone's full
  purge, any cleanup) says nothing about the work. Deleting local data is
  hygiene, not a statement.
- **Only a real transition.** Re-clicking a favorite queues nothing, and an
  undelivered opposite event supersedes its predecessor rather than sending
  both in whichever order the worker picks them up.

Trakt caps favorites at **100 for every account** — VIP does not raise it —
and answers a full list with **HTTP 420**. That is not a pacing problem like
429, so it maps to its own `AccountLimitReached` failure and ends the event
terminally instead of retrying into the same wall. The local favorite is kept
and the title is named in Settings; silently swallowing the 420 (as some
clients do) is precisely what this avoids.

## Inbound

Favorites reconcile through their own preview/apply pair
(`POST /api/watch-history/connections/{key}/favorites/preview` and `/apply`),
beside the explicit watched-history sync. There is no background polling and
no one-time import at connection: the first explicit sync *is* the import.

The comparison is **three-way** — local now, remote now, and what the last
reconciliation recorded in `WatchHistoryFavoriteState`. Without that memory,
"favorited here, absent there" cannot be told from "unfavorited there, still
flagged here", and reconciliation would have to guess which side to follow.
With no memory at all, a one-sided favorite is treated as an addition on that
side: the conservative reading, since the alternative would clear a flag the
user set before favorites sync existed. State rows are kept only while there
is something to remember, so a large library stores nothing for the titles
nobody favorited.

An inbound favorite for a title the library lacks first tries to match a
**tombstone** ([library item tombstones](../library-item-tombstones/feature.md)) —
a deleted-but-loved title lands back on its ghost and returns with it. What
matches nothing is reported as skipped with a visible count rather than
dropped silently.

## Surfaces

The Trakt section in Settings shows how full the remote list is (`97/100`),
warns as it nears the cap and marks it when full, notes that only movies and
series sync, and names the titles whose push ended terminally.

## Provider contract

`IWatchHistoryFavoritesProvider` is optional and work-level: an adapter that
knows only plays stays a complete `IWatchHistoryProvider`, and the core
resolves favorites by provider key rather than downcasting or consulting a
capability flag that could disagree with the type. Delivery resolves the
favorites adapter before the history one, since a favorites event never needs
the latter.

## Testing Expectations

- `FavoritesPushTests` — an explicit transition queues work; re-favoriting
  queues nothing; opposite events supersede; episodes stay local; nothing is
  queued without a favorites-capable connection; a duplicated work keeps its
  remote favorite until the last copy is cleared.
- `FavoritesDeliveryTests` — a delivered add records how full the list is;
  HTTP 420 ends the event terminally and visibly; a transient failure still
  retries; an unrecognised title ends the event.
- `FavoritesSyncTests` — each of the four reconciliation directions, inbound
  favorites landing on a tombstone, remote favorites absent from the library
  reported as skipped, and the cap reported in the plan.

Live contract verification against a real Trakt account has not been run yet;
it is tracked as the one open deliverable in [plan.md](plan.md).
