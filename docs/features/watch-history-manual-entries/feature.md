# Watch-History Manual Entries

Created: 2026-08-09
Updated: 2026-09-22

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
else about a hand-made statement does: the Jellyfin played command records a **timeless**
entry by design, and timeless entries are counted under `Undated N` rather than
placed on a guessed day.

Logging and setting time cover viewings without an observed threshold crossing
or an explicit Finish watching action. Two ordinary situations bypass playback
observation:

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

## Playback status and explicit completion

Movie heroes and episode rows show watched status and the date of the latest
actual dated history entry. An undated Jellyfin mark can set the status, but does
not invent a date. The web interface has no timeless watched/unwatched toggle.
Jellyfin played/unplayed commands keep their existing timeless semantics.

An in-progress movie or episode offers **Finish watching** and **Clear progress**.
Finishing records a dated manual watch at the current server time, marks the item
watched and clears its resume position. A transaction claims the position, so
repeated completion requests add no extra watch. When the position belongs to a
known player session, that session is completed too; later reports from it cannot
record another completion. An already completed session does not get another
history entry. Without a resume position, finishing is a no-op.

Clearing progress only resets the resume position. It does not change watched
state, counts, dates, or history. Both controls also appear on the home
**Continue watching** cards. Without a resume position the detail controls offer
**Log watch**, with the time dialog initially set to now.

Automatic playback completion uses an 85% runtime threshold. The existing session
and below-threshold observation requirements still apply.

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
the folder itself is a different gesture. The UI offers the action on movie pages and episode rows.

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
particular leaves the entry and its aggregates unchanged.

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

## API

```http
POST  /api/library/{id}/watches          { "watchedAt": "<utc-instant>" }
PATCH /api/watch-history/entries/{id}    { "watchedAt": "<utc-instant>" }
POST  /api/library/{id}/resume/finish
DELETE /api/library/{id}/resume
```

All routes are authenticated and scoped to the caller. Resume operations accept
published movies and episodes, return updated user data on success (including a
no-op), 404 for unknown items and 400 for folders. They change only the caller's
state. The DTO's `lastWatchedAt` comes from dated history, independently of the
legacy aggregate field of the same name.

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
season or series at once, moving a whole day's
plays at once, and any bulk backfill.

## Movie detail history

The [movie detail page](../movie-detail-context/feature.md) offers Log watch beside
its status and in the overflow menu. Inline history appears with at least two
records and offers Log watch plus Set time / Change time on individual entries.
Single records remain editable in the calendar. Retained removed movies accept these web actions when the
caller has their own retained signal. Public/native playback paths keep their
published-item requirement.

## Testing Expectations

- Backend coverage checks the 85% boundary, movie and episode completion,
  repeated requests and later session reports, caller isolation, folder rejection,
  clear-progress preservation of all viewing facts, and undated status without
  a fabricated date.
- `e2e/watch-actions.spec.ts` covers completion and dismissal, zero/one/two-viewing
  timeline visibility, home card removal, episode controls on phones, and failure
  recovery without hiding resume actions.

- `WatchHistoryRecorderTests` cover dated manual entries without a session,
  separate rewatches, forward-only aggregate updates, stable watched flags,
  resume behavior, invalid/future inputs, unidentified items, and keeping logged
  plays when the user unwatches an item.
- `WatchHistoryEntryServiceTests` cover dating marks without changing play count,
  user isolation, future-time rejection, and corrections to dated plays. When the
  aggregate points at the corrected play, it follows that play or the next latest
  sibling; otherwise an older correction leaves the latest watch alone.
- Reapplying the same instant leaves the entry and aggregates unchanged.
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
