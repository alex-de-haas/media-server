# Watch-History Deletion

Created: 2026-08-05
Updated: 2026-08-05

## Description

A signed-in user can delete one recorded play from their own history. This is the
only way a dated play is ever removed on the user's say-so, and the counterpart to
the recording paths in
[watch-history-providers](../watch-history-providers/feature.md).

It exists because the record is otherwise permanent. A play someone else on the
same profile recorded, a false completion from a client that reported a finish it
never reached, or an import that landed on the wrong title would stay in the diary
forever and keep inflating the item's play count.

Deleting is deliberately per-entry. It corrects a mistake in the record; it is not
a way to erase a title's history in bulk, and it is not the watched toggle.

## Where it appears

In the Watched calendar
([watch-history-calendar](../watch-history-calendar/feature.md)), on each play in
**Watched on this day** and on each mark in **Watched without a date**. Both go
through a confirmation naming the exact play — title, episode code, and time — so
a mis-tap on a dense list cannot silently remove a viewing.

Each control carries its own accessible name down to the timestamp: a day can hold
two plays of one movie, and two controls with the same name leave a screen reader
unable to say which one it is on.

The confirmation stays open if the request fails. Closing it would leave the row
still on screen with only a toast to explain why, which reads as the delete having
done nothing at all. When the day's last play or the last undated mark goes, its
dialog says so rather than sitting empty — the control that opened it is by then
gone.

## API

```http
DELETE /api/watch-history/entries/{entryId}
```

Authenticated and scoped to the caller. `204` on success, `404` when this user has
no such entry — which is also the answer for another user's entry, so the route
cannot be used to probe for one. Unlike disconnecting a provider it is not
idempotent: the id names a specific row, and answering `204` for one the caller
does not own would confirm that it exists.

The entry removal, the reprojected aggregates, and any outbound intent commit in a
single transaction.

## What the aggregates become

`UserItemData`'s counters are reprojected from what survives. The play count is not
a strict projection of the entry table — a mark, an unwatch and a re-mark
legitimately leave one entry and a count of two, and a remap merges history onto a
row without recomputing it — so the rule is stated as two invariants instead:

- a deletion never **increases** the count, however far the two have drifted;
- deleting the last entry leaves a clean slate rather than a count with nothing
  behind it.

Between those, one deleted play is one fewer play, floored at what is actually
left. `LastWatchedAt` falls back to the newest surviving play, or null when none
remains — an item can legitimately end up watched with no last-watched time, which
is the same shape as pre-migration history.

The watched flag is **cleared only when no entries remain**, and never raised. An
item deliberately marked unwatched keeps exact plays on purpose; tidying one of
them away is not a claim that it is watched again. When the flag does flip, its
`WatchedStateChangedAt` moves — that timestamp is the idempotency discriminator for
manual marks, so without the bump a later mark-watched would hash to the key of the
one before this deletion and be swallowed as a duplicate.

The resume position, `LastPlayedDate`, and the favorite flag are untouched: a
resume point is still genuinely useful, and neither ordering nor favorites is a
claim about this play. Because `MarkWatched` zeroes the position when an item
crosses the threshold, clearing the flag does not resurrect the title in Continue
Watching. Next Up does follow, since it keys on the watched flag.

Deleting the last entry can also make a title genuinely disposable: history
presence is one of the signals that makes a library delete leave a tombstone rather
than purge.

## What the provider is told

Only entries this app created **and** whose remote id it resolved are removed
remotely — `ProviderEntryOwned`, never a matching identity and timestamp. An entry
whose link settled `Unresolved` is never removed: guessing there destroys history
this app did not create. The removal travels as a `RemoveOwnedEntries` outbox event
carrying that one id, delivered by the same owned-only path an unwatch uses.

When there is nothing owned to remove, **no event is queued at all**. An empty one
would complete as a no-op, but until the worker reached it the user's explicit sync
would refuse to start, counting it as undelivered work.

That is the common case today, and worth stating plainly: ownership is only ever
recorded for timeless marks — the exact-play push does not resolve the id it
created — so a deleted dated play remains at the provider, and an explicit sync can
re-import it. The integration is wound down (see
[watch-history-providers](../watch-history-providers/feature.md)), so this is a
recorded limitation rather than scheduled work.

## Not included

Deliberately out of scope: deleting a whole day or an item's entire history at
once, editing an entry's timestamp, and a history list on the item page. Deleting
is a correction to one row; wholesale removal of an item's history is what the
library delete and the sync already cover.

## Testing Expectations

- `WatchHistoryEntryServiceTests` covers the service: an unknown id and another
  user's entry are both not-found and delete nothing; the aggregates follow the
  remaining plays; the watched flag is cleared only when nothing is left and is
  never raised on an unwatched item; a count ahead of its entries loses one play,
  the last entry leaves a clean slate however far it had drifted, and a deletion
  never increases the count; a non-latest play leaves `LastWatchedAt` alone while a
  surviving timeless mark leaves it null; an owned entry queues its removal with
  the remote id; imported, `Unresolved` and unlinked entries queue nothing at all;
  no connection means no event; and two deletions on one item queue both removals.
- `WatchHistoryDeliveryServiceTests` covers the dispatch: `RemoveOwnedEntries`
  reaches the provider with the snapshot ids and leaves everything else in place,
  and is terminal — never broadened — for a provider that cannot remove one entry.
- `e2e/calendar.spec.ts` covers the surface: a play deleted from the day detail
  once confirmed, with the grid card following; a cancelled confirmation deleting
  nothing; and an undated mark deleted from its own list, leaving the dialog to
  explain itself.
