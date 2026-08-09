# Watch-History Manual Entries

Status: In Progress
Created: 2026-08-09
Updated: 2026-08-09

## Goal

Let a user state **when** they watched something, by hand, so a play the server
never observed lands on the right day of the Watched calendar.

Two gaps drive it, and both are visible today:

1. **Nothing watched away from this server can be dated.** The only manual
   statement the app accepts is the watched toggle, and by design it records a
   **timeless** entry — `WatchedAt = null`, `Origin = Manual`
   ([watch-history-providers](../watch-history-providers/feature.md)). Timeless
   entries are excluded from the calendar grid by construction; they are counted
   under `Undated N` and listed in **Watched without a date**
   ([watch-history-calendar](../watch-history-calendar/feature.md)).
2. **A real play sometimes arrives undated.** Only a threshold crossing observed
   through playback reports produces an exact instant. A client that instead
   marks the item played — `POST /Users/{id}/PlayedItems/{itemId}` with no
   `DatePlayed`, which `JellyfinPlaybackEndpoints` faithfully translates to
   `playedAt: null` — travels the same path as the toggle and yields a timeless
   entry. The viewing happened, the time is simply missing, and today there is no
   way to supply it.

   **Observed cause:** restarting or updating the server mid-playback. The
   progress reports that would have carried the crossing never reach it, and the
   client reconciles afterwards with a plain played mark. The window is exactly
   as wide as a deployment, so no amount of recording logic closes it — the play
   is genuinely unobserved, and only the viewer knows when it happened.

The counterpart already exists: a play that should not be there can be deleted
([watch-history-deletion](../watch-history-deletion/feature.md)). A play that
happened and was never recorded has no such affordance. This plan is that
affordance.

## Target behavior

Written as a diff against the three watch-history documents; this feature gets
its own `feature.md` on completion, and theirs are amended where the change
shows through.

### The existing toggle is untouched

`Mark watched` / `Mark unwatched` keep their exact current semantics: an
idempotent statement about current state, one timeless entry at most, no second
viewing for a second click. Everything below is a **new, separate action**. That
boundary is deliberate — the toggle is the cheap one-click gesture, and making it
prompt for a time would tax the common case to serve the rare one.

### Logging a watch

A new action on the movie page — `Log watch…`, in the header's `⋮` overflow menu
rather than in the button row — opens a dialog whose single field is a local date
and time, **pre-filled with now**. Confirming records one play at that instant.

The overflow menu is where it belongs precisely because it is the rare gesture:
the button row is for what a viewer does on most visits, and a fourth button
there would compete with `Mark watched` for the same glance. That menu is
`AdminControls` today and returns null for everyone else, so it becomes
`ItemActions`: `Log watch…` for any signed-in user, then a separator, then the
existing admin items behind the role check they already have. The menu itself
disappears only when a user has nothing in it at all — a non-admin on a series,
as today.

- Every confirmation is **one more play**, including on an already-watched movie:
  that is what a rewatch is, and unlike the toggle this action is an explicit
  statement about a specific viewing rather than about current state.
- The entry is `Origin = Manual` with a real `WatchedAt`. `Manual` therefore
  stops meaning "timeless"; its meaning becomes "the user said so", which is what
  it always described. Nothing keys on the origin alone — the two queries that
  read it (`StageManualWatchedAsync`, `StageUnwatchedAsync`) already pair it with
  `WatchedAt == null`, so a dated manual entry is invisible to both. In
  particular **an unwatch still drops only timeless marks**, and a logged play
  survives it exactly as an observed play does.
- A future instant is refused. The rest of the range is not policed: dating a
  viewing to 1998 is a legitimate thing to record.
- The time is entered and displayed in the browser's local zone and stored as a
  UTC instant, which is what makes it land on the day the user means — the
  calendar buckets by local day for the same reason.

### Giving an undated mark its time

In **Watched without a date**, each mark gains `Set time…`, opening the same
dialog. Confirming stamps `WatchedAt` on that existing entry: the mark leaves the
undated list and appears in the grid on that day. No entry is created and none is
destroyed, so the item's play count does not move — the viewing was always there,
only its time was missing. This is the direct fix for gap 2 above.

It is deliberately narrow: it dates an **undated** mark. Re-dating a play that
already carries a time stays out of scope, as
[watch-history-calendar](../watch-history-calendar/feature.md) says today.

### What the aggregates become

`UserItemData` follows the logged play, with one rule the existing `MarkWatched`
does not need: **timestamps never move backwards**. Logging a viewing from last
year must not rewrite `LastWatchedAt` to last year.

- `PlayCount` increments on a log, and does not move when an existing mark is
  merely dated.
- `LastWatchedAt` and `LastPlayedDate` advance to the logged instant only when it
  is later than what they hold.
- `Played` is raised; `WatchedStateChangedAt` moves only on the actual
  transition, because it is the idempotency discriminator for manual marks.
- The resume position is cleared, as it is for any completion.

### What the provider is told

A logged play stages `AddExactWatch`, the same operation an observed completion
uses, keyed on the **entry id** — a row-derived key would collide, since a second
log changes no state on the row and the second event would be swallowed as a
duplicate. Dating an existing mark queues nothing: `EnsureTimelessWatched` was
already delivered for it, and re-posting it as an exact play would double the
viewing on the remote account.

As with every exact play, ownership of the remote entry is not resolved, so
deleting a logged play later does not remove it remotely — the recorded
limitation in [watch-history-deletion](../watch-history-deletion/feature.md),
unchanged here and moot while the provider is wound down.

### Scope

Movies and episodes are both valid targets of the API — the guard is "not a
folder", which is one condition rather than two. The UI surfaces `Log watch` on
the **movie** page only; a season-wide fan-out at one instant is a different
gesture and is not part of this.

## Deliverables

- [ ] `WatchHistoryRecorder.StageManualWatchAsync` — a dated `Manual` entry plus
      an `AddExactWatch` outbox event keyed on the entry id.
- [ ] `WatchHistoryEntryService.SetWatchedAtAsync` — stamps an instant on the
      caller's own **undated** entry; not-found for an unknown, foreign, or
      already-dated entry; queues nothing.
- [ ] `UserDataService.LogWatchAsync` — the aggregates above, including the
      never-backwards rule, in one transaction with the entry and the outbox
      event.
- [ ] `POST /api/library/{id:guid}/watches` — `{ watchedAt }`, returning the
      updated `UserItemData`; `400` for a missing, unparseable or future instant
      and for a folder; `404` for an unknown item.
- [ ] `PATCH /api/watch-history/entries/{entryId}` — `{ watchedAt }`, `204`;
      `404` scoped to the caller, `400` on a future instant or an entry that is
      already dated.
- [ ] `PlaybackHistoryOrigin.Manual`'s doc comment corrected: it is no longer
      necessarily timeless.
- [ ] Web: `mediaServer.logWatch` / `mediaServer.setEntryWatchedAt`, a shared
      `watch-time-dialog` (local ⇄ UTC, defaulting to now, refusing the future
      client-side too), `AdminControls` → `ItemActions` carrying `Log watch…`
      above the admin block, and `Set time…` in **Watched without a date** — each
      invalidating the item, the calendar and the undated queries.
- [ ] Tests per the Verification section.
- [ ] `docs/features/watch-history-manual-entries/feature.md`; amendments to
      `watch-history-calendar`, `watch-history-providers` and
      `watch-history-deletion`; `plan.md` deleted; index regenerated.
- [ ] `manifest.json` `0.55.1 → 0.56.0` (new functionality).

## Phases

One branch, one PR (per `AGENTS.md`).

1. **API** — recorder, service, aggregates, both endpoints, tests.
2. **Web** — client calls, dialog, both entry points, unit tests.
3. **Verification and docs** — e2e, `feature.md`, amendments, index, version.

## Open questions

- **Resolved on approval:** `Set time…` for undated marks ships with the rest
  rather than being cut. It is the half that addresses the observed cause above,
  where the entry already exists and only its time is missing.

## Verification

- `WatchHistoryRecorderTests` — a dated manual entry is recorded with its
  instant; the outbox event is `AddExactWatch` carrying `occurredAt`; two logs on
  one item queue two events rather than colliding; no connection queues nothing;
  an unidentifiable item still records the entry.
- `WatchHistoryEntryServiceTests` — an undated mark takes its instant; an
  unknown, another user's, or an already-dated entry is refused and changes
  nothing; the play count does not move; nothing is queued.
- `UserDataService` tests — a log increments the count and raises `Played` with
  `WatchedStateChangedAt` moving only on the transition; a backdated log leaves
  `LastWatchedAt`/`LastPlayedDate` alone while a later one advances them; the
  resume position is cleared; a folder and a future instant are refused; another
  user's state is untouched.
- `WatchHistoryEndpointMappingTests` — status mapping for both routes.
- Web unit — local ⇄ UTC round-trip across a DST boundary, the default being
  now, and the future guard.
- `e2e/calendar.spec.ts` / `e2e/detail.spec.ts` — logging a watch from the movie
  page makes it appear on that day of the Watched calendar, and dating an undated
  mark moves it from the undated list into the grid.
- `dotnet build`, `dotnet test`, `pnpm lint`, `pnpm test`, `pnpm exec playwright
  test`, `node scripts/docs-index.mjs --check`.
