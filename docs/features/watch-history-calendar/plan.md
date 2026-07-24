# Watch-History Calendar

Status: In Progress
Created: 2026-07-24
Updated: 2026-07-24

## Goal

One calendar surface that answers both calendar questions: the existing
**Releases** view keeps answering "what comes out and when", and a new
**Watched** mode answers "what did I finish on this date" — a restrained,
read-only screening diary over the per-play history
(`PlaybackHistoryEntries`), scoped to the signed-in user.

This plan was promoted from the exploratory idea document; the evaluated
alternatives (mixed grid, separate History page, primary heatmap) and their
rejection rationale live in this file's git history.

## Target Behavior

A diff against the current `/calendar` page (Monday-first month grid for
release events, three chips per day, overflow dialog, month state in memory):

- The page gains a visible `Releases | Watched` segmented control. `Releases`
  stays the default and its rendering, toolbar, and actions are unchanged.
- Calendar state moves into the URL — `/calendar?view=watched&month=2026-07` —
  for both modes. This is net-new: today the month lives only in React state.
  Explicit URL selection, no silent persistence in browser storage.
- **Watched desktop grid**: a movie play renders poster, title, and local
  completion time; multiple plays of one movie on one day collapse to `x2`.
  Episodes of one series on one calendar day always collapse to **one card
  carrying the series poster** (`Severance · 3 episodes`) — a 10+ episode
  binge day still occupies one card. Presentation grouping only, never
  history deduplication: raw entry IDs and timestamps ride along and expand
  losslessly. At most three compact groups per cell, then `+N more`.
- Selecting a single-play row opens the title's detail page. Selecting an
  aggregated row, the day, or its overflow opens a chronological **Watched on
  this day** dialog: poster, movie title or episode code, exact local time, a
  subtle source label for imported plays, and one `--brand` time notch per
  play.
- Watched-mode toolbar replaces release actions with `All | Movies | Episodes`
  kind filters and an `Undated N` entry whose count follows the active kind
  filter. Timeless marks (manual/legacy) never receive a fabricated date; they
  live in a **Watched without a date** list outside the grid.
- An empty visible month says `Nothing watched this month` and, when older
  exact history exists, offers `Jump to last watched month`.
- **Mobile agenda** replaces the seven-column grid: date-grouped rows using
  the same series-per-day card (series poster, `3 episodes`, first–last
  completion times). The same grouping helpers drive grid and agenda so their
  counts cannot diverge.
- Day boundaries are the browser's local time zone; the day detail surfaces
  the active time zone when it differs from the instance's configured one.
- Visuals stay inside the existing token system: neutral oklch tokens plus
  `--brand` ("projector gold") reserved for the time notches; Inter for
  chrome, Fraunces only for media titles in day detail, Geist Mono for times
  and counts; one short mode-change transition honoring reduced motion.

Out of scope for this plan (explicitly decided, not deferred by accident):
a `Combined` releases+watched overlay, history editing or deletion,
viewing-duration or streak analytics, and any heatmap.

## Data and API

- New authenticated read endpoint beside the provider-management routes:

  ```http
  GET /api/watch-history/calendar?from=<utc-instant>&toExclusive=<utc-instant>
  ```

  The browser computes the UTC instants for the visible local grid range; the
  backend filters exact `WatchedAt` values, caps the interval (62 days), and
  returns raw plays — the client groups them in its own time zone, which
  keeps daylight-saving boundaries correct.
- Response envelope: `events`, `undated` (per-kind, e.g.
  `{ "movies": 8, "episodes": 4 }` — a single total could not follow the kind
  filter because timeless rows are absent from `events`), and
  `latestWatchedAt` (powers the empty-state jump without loading history).
- Each event: history-entry ID, `watchedAt`, media-item ID, kind, title,
  poster URL — **the series poster for episodes**, which rarely carry their
  own artwork and are grouped at series level anyway — detail-URL inputs,
  series title/ID plus season and episode numbers for episodes, and origin.
- New index `(AppUserId, WatchedAt)` via EF migration: the existing
  `(AppUserId, MediaItemId, WatchedAt)` index serves one item's history, not
  a user's time-range scan.

## Deliverables

- [x] Calendar read endpoint: bounded range, per-kind `undated`,
      `latestWatchedAt`, series-poster episode rows, user scoping — with unit
      tests for the envelope, the caps, and the scoping.
- [x] `(AppUserId, WatchedAt)` index migration.
- [x] Calendar shell split: shared month shell (navigation, grid frame,
      overflow) with mode-specific content and toolbars — no conditional
      branching accumulation; URL state `?view=&month=` for both modes with
      `Releases` as the default; release behavior pinned by new e2e (the
      calendar had none).
- [x] Watched desktop grid: lossless grouping helpers (movie `xN`,
      series-per-day card), three-group cap, day-detail dialog with exact
      times, provenance labels, and time notches.
- [x] Watched toolbar: kind filters, filter-following `Undated N`, the
      **Watched without a date** list, empty month state with
      `Jump to last watched month`.
- [ ] Mobile agenda on the same grouping helpers.
- [ ] Visual pass: tokens only, Fraunces day-detail titles, reduced-motion
      transition, dark-mode and Hosty-iframe verification.
- [ ] `feature.md` for this folder written from shipped reality; this plan
      deleted; index regenerated — in the completing PR.

## Phases

One branch, one PR, per repository rules.

1. **Data and API** — the index migration and the read endpoint with its
   tests. No UI change; independently verifiable with `dotnet test`.
2. **Shell and URL state** — split the calendar into shell + mode content,
   introduce the segmented control and URL state, keep `Releases` untouched
   (guarded by the existing Playwright e2e).
3. **Watched grid and day detail** — grouping helpers (unit-tested pure
   functions in `src/web/src/lib/`), grid cells, dialog, toolbar filters,
   undated list, empty states.
4. **Mobile agenda and polish** — agenda view over the same helpers, visual
   pass, dark/iframe check.

## Open Questions

None. The idea-stage questions were resolved during review: grouping is one
card per series per calendar day with the series poster; v1 is read-only;
`Releases` stays the default mode; no `Combined` mode, duration metrics, or
heatmap in v1; imported and local plays share one grid treatment with
provenance only in day detail.

## Verification

- `dotnet test` — endpoint envelope, range caps, per-kind undated counts,
  user scoping, index migration applies cleanly.
- Web: `vitest` for grouping/range/URL helpers; `next build`; Playwright e2e
  for mode switching, deep links, and unchanged release behavior.
- Manual matrix: a 10+ episode binge day renders one card and a complete day
  detail; a same-day movie rewatch shows `x2` with both timestamps in detail;
  `Undated N` follows the kind filter; a DST-boundary month groups by local
  days; dark mode and the Hosty iframe render correctly; a month with no
  history offers the jump when older history exists.

## Links

- [Watched-history provider plan](../../planning/trakt-watched-state-sync.md)
  — the per-play history this surface reads.
- [Frontend application](../frontend-application.md) — the current calendar
  and navigation reality.
- [Release tracking](../release-tracking.md) — the release-mode
  specification.
- [Domain model](../domain-model.md) — `PlaybackHistoryEntry` and friends.
