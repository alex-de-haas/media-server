# Watch-History Manual Entries

Created: 2026-08-09
Updated: 2026-08-09

## Description

A signed-in user can state **when** they watched something. Two actions, one
question:

- **Log watch** records a viewing the server never observed, at an instant the
  user names.
- **Set time** gives an undated mark the time it should always have had.

Both put a play on a day of the Watched calendar
([watch-history-calendar](../watch-history-calendar/feature.md)), which nothing
else about a hand-made statement does: the watched toggle records a **timeless**
entry by design, and timeless entries are counted under `Undated N` rather than
placed on a guessed day.

They exist because a dated play is only ever produced by playback reports
observed crossing the watched threshold. Two ordinary situations bypass that:

1. **The viewing happened elsewhere** — another device, a cinema, a disc — and
   the server saw none of it.
2. **The reports never arrived.** A client that marks an item played rather than
   reporting progress (`POST /Users/{id}/PlayedItems/{itemId}` with no
   `DatePlayed`) travels the toggle's path and lands undated. Restarting or
   updating the server mid-playback produces exactly this: the window is as wide
   as a deployment, the play is genuinely unobserved, and only the viewer knows
   when it was.

This is the counterpart to
[watch-history-deletion](../watch-history-deletion/feature.md): that removes a
play that should not be there, this records one that is missing.

## The toggle is untouched

`Mark watched` / `Mark unwatched` keep their semantics exactly: an idempotent
statement about current state, at most one timeless entry, and no second viewing
for a second click. The toggle is the one-click gesture on the button row;
logging a past viewing is rare, and making the common case ask for a time would
tax it to serve the exception.

## Where it appears

**Log watch…** is in the `⋮` overflow menu in the movie page header, above the
admin block and separated from it. That menu was admin-only; logging a play
against your own history is not an admin act, so it now renders for any signed-in
user and the admin items stay behind their role check. A viewer with nothing in
the menu — a non-admin on a series — still sees no menu at all.

**Set time…** is a control on each mark in **Watched without a date**, beside its
delete control. Its accessible name carries the title, because two controls with
the same name leave a screen reader unable to say which row it is on.

Both open the same dialog: one field, pre-filled with the current local time,
re-stamped each time it opens so "now" means now. A `Now` button restores it
after browsing to another date. A future instant is refused in the dialog before
the round trip, and again on the server, which trusts no client clock.

The time is entered as local wall-clock and sent as a UTC instant. That is what
makes it land on the intended day: the calendar buckets by the browser's local
day, so a play at 00:30 belongs to the day it was watched.

## Logging a watch

Every confirmation is **one more play**, including on an already-watched movie —
that is what a rewatch is. This is the deliberate difference from the toggle: an
explicit statement about a specific viewing, not about current state.

The entry is `Origin = Manual` with a real `WatchedAt` and no session id. `Manual`
therefore no longer means "timeless"; it means "the user said so". Nothing keys on
the origin alone — the two queries that read it pair it with `WatchedAt == null`,
so a logged play is invisible to the toggle's bookkeeping. In particular **an
unwatch still drops only timeless marks**, and a logged play survives it exactly
as an observed play does.

Any playable leaf — a movie, an episode, an extra — is a valid target of the API,
the same set that accepts playback reports. A season or series is refused, because
marking a folder is a fan-out over its episodes and logging one viewing against
the folder itself is a different gesture. The UI offers the action on the movie
page.

### What the aggregates become

`UserItemData` follows the play, with one rule the observed path does not need:
**timestamps never move backwards.**

- `PlayCount` increments on every log.
- `LastWatchedAt` and `LastPlayedDate` advance to the logged instant only when it
  is later than what they already hold — a viewing logged for 2019 was not the
  most recent one.
- `Played` is raised; `WatchedStateChangedAt` moves only on the actual
  transition, because it is the idempotency discriminator for manual marks and a
  needless bump would let a later mark-watched queue an event for a click that
  changed nothing.
- The resume position is cleared only when the logged instant is the item's
  latest activity. Backfilling an old viewing must not throw away a position the
  user is in the middle of right now.

## Setting an undated mark's time

The existing entry is stamped: nothing is created and nothing is destroyed, so
the play count does not move — the viewing was always recorded, only its time was
missing. The mark leaves **Watched without a date** and appears in the grid on
that day. The row's `LastWatchedAt` learns the instant, forwards only.

It is deliberately narrow. An entry that already carries a time is refused:
"the recorded time is wrong" is a different claim from "this play was never
timed", and re-dating a play remains out of scope.

## What the provider is told

A logged play stages `AddExactWatch` — the same operation an observed completion
uses — keyed on the **entry id**. A row-derived key would collide, because a
second log changes no state on the row and the second event would be swallowed as
a duplicate.

Dating an existing mark queues **two** events: `RemoveOwnedEntries` for the remote
timeless mark, and `AddExactWatch` for the play it has become. "Watched, time
unknown" and "watched at T" are different claims, and the provider holds the
first — adding without removing would leave the account with the same viewing
twice, and the next explicit sync would import that timeless mark straight back
into the undated list the user just emptied. The two are independent (a removal is
addressed by remote id, an add by identity and instant), so their delivery order
does not matter. The local entry's link is cleared with them: after the removal
there is no remote entry left for it to name.

Only a mark this app **owns** is retired. An `Unresolved` link is left alone, as
everywhere else — the add committed but its id was never pinned down, and removing
on a guess destroys history this app did not create. The remote timeless mark then
survives and a later sync can re-import it, which is the standing `Unresolved`
limitation rather than a new one.

As with every exact play, ownership of the remote entry is not resolved, so
deleting a logged play later does not remove it remotely — the limitation
recorded in [watch-history-deletion](../watch-history-deletion/feature.md), and
moot while the provider is wound down
([watch-history-providers](../watch-history-providers/feature.md)).

## API

```http
POST  /api/library/{id}/watches          { "watchedAt": "<utc-instant>" }
PATCH /api/watch-history/entries/{id}    { "watchedAt": "<utc-instant>" }
```

Both are authenticated and scoped to the caller.

`POST /watches` answers `200` with the updated `UserItemData`, `404` for an
unknown item, and `400` for a folder, a missing `watchedAt`, or a future instant.
A folder is a `400` rather than a `404` because the item does exist, and saying
otherwise would send the caller looking for the wrong bug.

`PATCH /entries/{id}` answers `204`, `404` for an entry this user does not have —
which is also the answer for someone else's, so the route cannot be used to probe
for one — and `400` for an entry that is already dated or an instant in the
future.

Both allow an instant up to **five minutes** ahead of the server's clock. The
value is composed from the browser's clock, and refusing a "now" that runs a
minute fast would fail the most common action there is.

## Not included

Deliberately out of scope: re-dating a play that already has a time, logging a
watch for a whole season or series at once, logging one from the episode list,
and any bulk backfill.

## Testing Expectations

- `WatchHistoryRecorderTests` covers logging: one dated `Manual` entry with no
  session; two logs recording two plays and queueing two `AddExactWatch` events
  rather than colliding on one idempotency key; the never-backwards rule in both
  directions; the watched flag changing once however many plays are logged; a
  backdated log keeping the resume point while a current one clears it; a folder,
  an unknown item and a future instant refused; a clock-skewed "now" accepted; an
  unidentifiable item still recording its play without queueing undeliverable
  work; and an unwatch leaving a logged play alone.
- `WatchHistoryEntryServiceTests` covers dating: an undated mark taking its
  instant with the play count unmoved; the row learning the time, forwards only;
  an already-dated, unknown, or another user's entry refused and unchanged; a
  future instant refused; an owned mark retired remotely and re-stated as an exact
  play with its local link cleared; an `Unresolved` one only re-stated; and
  nothing queued at all without a connection.
- `WatchHistoryEndpointMappingTests` covers both routes' status mapping,
  including that an unknown and a foreign entry are indistinguishable.
- `watch-time.test.ts` covers the conversion: a local ⇄ UTC round trip on both
  sides of a daylight-saving boundary, an empty or unparseable field refused
  rather than turned into a time, and the future allowance.
- `e2e/detail.spec.ts` covers the movie surface: logging a watch from the
  overflow menu sends the instant it was given, and a non-admin sees `Log watch…`
  in that menu and none of the admin items.
- `e2e/calendar.spec.ts` covers the undated surface: a mark given its time leaves
  the list, and a future time is refused before any request is sent.
