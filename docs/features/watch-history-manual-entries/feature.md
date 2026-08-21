# Watch-History Manual Entries

Created: 2026-08-09
Updated: 2026-08-21

## Description

A signed-in user can state **when** they watched something. Three actions, one
question:

- **Log watch** records a viewing the server never observed, at an instant the
  user names.
- **Set time** gives an undated mark the time it should always have had.
- **Change time** moves a play the server did record to the time it really
  happened.

All three put a play on a day of the Watched calendar
([watch-history-calendar](../watch-history-calendar/feature.md)), which nothing
else about a hand-made statement does: the watched toggle records a **timeless**
entry by design, and timeless entries are counted under `Undated N` rather than
placed on a guessed day.

The first two exist because a dated play is only ever produced by playback
reports observed crossing the watched threshold. Two ordinary situations bypass
that:

1. **The viewing happened elsewhere** — another device, a cinema, a disc — and
   the server saw none of it.
2. **The reports never arrived.** A client that marks an item played rather than
   reporting progress (`POST /Users/{id}/PlayedItems/{itemId}` with no
   `DatePlayed`) travels the toggle's path and lands undated. Restarting or
   updating the server mid-playback produces exactly this: the window is as wide
   as a deployment, the play is genuinely unobserved, and only the viewer knows
   when it was.

The third exists because an observed instant is the moment the report landed,
which is not always the moment the viewer means: a film finished on a player left
running overnight, or a viewing hand-logged onto the wrong evening. The play is
real; only its time is wrong.

This is the counterpart to
[watch-history-deletion](../watch-history-deletion/feature.md): that removes a
play that should not be there, this records one that is missing — or moves one
that is in the wrong place.

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

**Change time…** is the same control on each play in the calendar's day detail,
beside that play's delete control. It is named down to the timestamp for the same
reason: a day can hold two plays of one movie.

All three open the same dialog: one field and a `Now` button. Logging a watch and
dating a mark open on the current local time, re-stamped each time so "now" means
now; changing a time opens on the time on record instead, because the common
correction is an hour out rather than a different evening, and starting from now
would make the user retype a date that was already right. A future instant is
refused in the dialog before the round trip, and again on the server, which
trusts no client clock.

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

## Setting or changing a play's time

The existing entry is stamped: nothing is created and nothing is destroyed, so
the play count does not move — the viewing was always recorded, and moving it in
time does not make it a second one. A dated mark leaves **Watched without a
date** and appears in the grid on that day; a corrected play leaves the day it
was on and appears on the new one.

Re-confirming the instant a play already carries changes nothing at all, and in
particular queues the provider no work: nobody made a correction.

### What `LastWatchedAt` becomes

Forwards only, with one exception. The row can hold a later viewing the entry
table never received — pre-migration history, or a remap that merged aggregates
without merging entries — so backfilling an old play must not claim the item has
gone unwatched since.

The exception is the row pointing at the very play being moved. Then it follows
it, backwards included, or the item would keep advertising an instant nothing was
watched at. It is recomputed from the plays that remain rather than simply taking
the new instant, because pulling this one back can hand the title to another
play.

`PlayCount`, `Played` and `LastPlayedDate` are untouched, as they are by a
deletion: when a viewing happened is not a claim about whether it happened, nor
about the item's ordering.

## What the provider is told

A logged play stages `AddExactWatch` — the same operation an observed completion
uses — keyed on the **entry id**. A row-derived key would collide, because a
second log changes no state on the row and the second event would be swallowed as
a duplicate.

Setting or changing a time queues **two** events: `RemoveOwnedEntries` for the
remote entry as it stands, and `AddExactWatch` for the play it has become. The
claim the provider holds — "watched, time unknown", or "watched at the old T" — is
no longer the claim being made, and adding without removing would leave the
account with the same viewing twice, after which the next explicit sync would
import the stale one straight back into the list the user just corrected. The two
are independent (a removal is addressed by remote id, an add by identity and
instant), so their delivery order does not matter. The local entry's link is
cleared with them: after the removal there is no remote entry left for it to name.

Both are keyed on the entry **and the instant it moved to**, so a second
correction is not hashed to the first and swallowed as a duplicate.

### A correction is exported only when it can be stated cleanly

Retiring the stale claim is a precondition, not a bonus. Only timeless marks ever
have their remote id resolved — an exact add records no id it could later address
— so a play that already had a time usually has a remote copy this app can
neither remove nor recall. Adding the corrected time regardless would put a
second viewing of one film on the user's profile, and the next explicit sync
would import the stale one back as another local play: a worse outcome than the
correction never reaching the provider.

So the export is staged only when one of these holds:

- the entry's remote copy is **owned and resolved** — the timeless-mark case, which
  is retired by the `RemoveOwnedEntries` above; or
- the only claim outstanding is an `AddExactWatch` this app queued and has
  **never attempted**, which the provider cannot have seen. That one is deleted
  and replaced, so correcting a play twice before delivery states just the latest
  time. An attempt that already ran is off limits: it may have reached the
  provider before the process died, which is why delivery re-reads history on a
  retry rather than re-posting.

Otherwise the correction stays local and is logged as such. The local history is
still right; the provider keeps the old time — the same standing limitation a
deleted exact play already carries, recorded in
[watch-history-deletion](../watch-history-deletion/feature.md).

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

`PATCH /entries/{id}` answers `204` — for a mark that had no time and for a play
being moved alike — `404` for an entry this user does not have, which is also the
answer for someone else's, so the route cannot be used to probe for one, and
`400` for a missing `watchedAt` or an instant in the future.

Both allow an instant up to **five minutes** ahead of the server's clock. The
value is composed from the browser's clock, and refusing a "now" that runs a
minute fast would fail the most common action there is.

## Not included

Deliberately out of scope: editing anything about a play other than its time —
which item it belongs to, or where it came from — logging a watch for a whole
season or series at once, logging one from the episode list, moving a whole day's
plays at once, and any bulk backfill.

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
  an unknown or another user's entry refused and unchanged; a future instant
  refused; an owned mark retired remotely and re-stated as an exact play with its
  local link cleared; an `Unresolved` one only re-stated; and nothing queued at all
  without a connection.
- The same tests cover correcting a dated play: the entry moving with the play
  count unmoved; the row following the play it was pointing at, backwards
  included, and handing the title to a sibling when one is now the latest; an
  older play's correction leaving the latest watch alone; the instant a play
  already carries queueing nothing; an owned play retired and re-stated at its new
  time; and a future instant refused.
- And what the provider is told about one: a play whose remote copy cannot be
  retired queueing nothing while the local move still happens; an untried queued
  add replaced by the corrected one; an already-attempted add left alone; and two
  corrections before delivery stating only the latest time while the timeless
  mark's removal still stands.
- `WatchHistoryEndpointMappingTests` covers both routes' status mapping,
  including that an unknown and a foreign entry are indistinguishable.
- `watch-time.test.ts` covers the conversion: a local ⇄ UTC round trip on both
  sides of a daylight-saving boundary, an empty or unparseable field refused
  rather than turned into a time, and the future allowance.
- `e2e/detail.spec.ts` covers the movie surface: logging a watch from the
  overflow menu sends the instant it was given, and a non-admin sees `Log watch…`
  in that menu and none of the admin items.
- `e2e/calendar.spec.ts` covers the calendar surfaces: a mark given its time leaves
  the undated list, a future time is refused before any request is sent, and a play
  corrected from the day detail opens on the time on record, sends the new instant,
  and moves to the day it names.
