# Watch-History Calendar

Created: 2026-07-24
Updated: 2026-07-24

## Description

`/calendar` answers two questions behind one `Releases | Watched` switch.
**Releases** is unchanged: tracked release dates, reminders, and the Add-title
and Tracked/Reminders actions. **Watched** is a read-only screening diary over
the per-play history in `PlaybackHistoryEntries` — what the signed-in user
finished, and when.

Both modes share a shell that owns the heading, the mode switch, month
navigation, and the Monday-first grid frame. Each mode supplies its own toolbar
and day contents and mounts only when selected, so the inactive mode never
issues a query and neither accumulates branches inside the other.

## Mode and month live in the URL

`/calendar?view=watched&month=2026-07`. `Releases` is the default, so an
existing `/calendar` link keeps its meaning, and defaults are omitted from
generated links — the common case stays a bare `/calendar`.

The params are read in the **server component** and passed down as props.
`useSearchParams` is deliberately not used: on this statically rendered route it
yields nothing until hydration, and a click landing in that window navigated
from the current month instead of the linked one.

## Watched mode

### Grouping

The grid shows cards, not raw plays:

- **A movie** is one card. Several plays of it on one day render `xN`.
- **A series** is one card per calendar day, carrying the **series poster** —
  ten episodes in an evening still occupy one card, because a day cell has no
  room for more and the series is the unit a viewer remembers. A rewatched
  episode counts once toward the episode tally.

Grouping is presentation only. Every underlying play — its id and exact
timestamp — rides along, so the day detail expands losslessly and no rewatch is
hidden. At most three cards fit a cell before the rest collapse into `+N more`.

Days are the **browser's** local days. The API returns raw UTC instants for
exactly this reason: a play at 00:30 lands on the day it was watched, and
daylight-saving boundaries stay correct.

### Day detail

Selecting a card, a day, or its overflow opens **Watched on this day**: every
play in chronological order with poster, title (episode code plus episode title
for series), exact local time, and a `--brand` time notch. Provenance appears
only here — an imported play is labelled `Imported`; the grid treats local and
imported plays identically, because provenance matters for diagnosis, not for
the memory.

### Filters and undated marks

`All | Movies | Episodes` narrows the grid. Timeless marks — a manual toggle, or
pre-migration history — never receive a fabricated date; they are counted per
kind and listed in **Watched without a date**, reachable from the `Undated N`
control, whose count follows the active filter. The counts must come from the
server per kind: those rows are absent from `events` by design, so a single
total could not be re-filtered in the browser.

An empty month says `Nothing watched this month`, and offers
`Jump to last watched month` when older dated history exists — driven by
`latestWatchedAt`, so the shortcut costs no extra history load.

### Narrow screens

Below `md` the seven-column grid is replaced by a date-grouped agenda: the same
series-per-day cards, each with its first–last completion span, under a date
heading carrying the day's real play count. Grid and agenda run on the same
grouping helpers, so their counts cannot diverge. The swap is CSS, not a
viewport hook, so server and client render identical markup.

### Layout

The calendar breaks out of the shell's 1024px reading column to
`min(100vw - 3rem, 90rem)`. Seven fixed columns turn width straight into legible
cards: a day cell goes from ~139px to ~176px. The shell already clips
`overflow-x` for full-bleed children, so no horizontal scrollbar appears, and
the cap stops an ultrawide monitor from smearing a month across the screen. On
phones it is a no-op. Its content column is consequently wider than the nav's —
a deliberate trade, not an oversight.

## API

```http
GET /api/watch-history/calendar?from=<utc-instant>&toExclusive=<utc-instant>
GET /api/watch-history/calendar/undated
```

Both are authenticated and scoped to the caller; every query filters on the
caller's `AppUserId`.

The calendar range is capped at **62 days** — a month grid spans six weeks at
most — and is served by a `(AppUserId, WatchedAt)` index; the pre-existing
`(AppUserId, MediaItemId, WatchedAt)` index leads with the item and cannot serve
a user's time-range scan.

The response envelope carries `events` (raw, ungrouped plays), `undated`
(`{ movies, episodes }`), and `latestWatchedAt`. Each event carries the
history-entry id, `watchedAt`, media-item id, kind, title, poster URL, series id
and title, season/episode numbers, and origin. For an episode the poster is the
**series** poster, and the numbering is canonical (`Identity*`) with the display
numbering as fallback — a re-mapped release displays one way and is identified
another.

`/undated` returns the timeless marks themselves, newest first and bounded, so
the list can name titles rather than only counting them. It is fetched when the
dialog opens, not with the calendar.

## Not included

Deliberately out of scope: a combined releases-plus-watched overlay, editing or
deleting history from the calendar, viewing-duration or streak analytics, and an
activity heatmap.

## Testing Expectations

- `WatchHistoryCalendarServiceTests` covers the read: raw ungrouped plays,
  range filtering with an exclusive end, series title/poster/canonical numbering
  on episodes, per-kind undated counts, `latestWatchedAt` reaching beyond the
  requested range, the undated list naming its items, and — twice over — that
  another user's history is never returned.
- `watch-history-calendar.test.ts` covers the grouping the UI depends on: a
  ten-episode binge collapsing to one card while retaining every play, distinct
  series and movies staying apart, a rewatched episode counting once, an orphan
  episode falling back to its own id, local-day bucketing, the grid range, kind
  filters, and the filter-following undated count.
- `calendar.test.ts` covers URL state: the default mode, fallbacks for malformed
  values, and hrefs that omit defaults.
- `e2e/calendar.spec.ts` covers the surface: releases unchanged and default,
  mode switching preserving a non-current month, watched deep links, a binge
  rendering as one card that expands to every episode, the undated count
  tracking the filter, the empty-month jump, and the phone agenda replacing the
  grid.
