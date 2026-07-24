# Watch-History Calendar

Status: Draft
Created: 2026-07-24
Updated: 2026-07-24

## Motivation

Media Server now has per-play history with exact timestamps for completed local
playback and provider-imported plays. The history is useful for more than watched
state: it can answer what the user watched on a particular evening, show a series
binge in context, and make rewatches visible without turning the library into an
analytics dashboard.

The application already has a primary **Calendar** page for future movie and series
release dates. Watch history should reuse its month navigation and date vocabulary,
but release intent (plan and remind) must remain visually distinct from viewing
history (remember and revisit).

## Current Foundation

- `PlaybackHistoryEntry` stores one row per known play, scoped to an `AppUser` and
  `MediaItem`.
- `WatchedAt` is an exact `DateTimeOffset` for observed or imported plays and is
  `null` for manual and legacy "watched, time unknown" marks.
- History retains its origin (`LocalPlayback`, `ProviderSync`, `Manual`, or
  `Legacy`) and keeps separate entries for genuine rewatches.
- Deleting a library item currently cascades to its history, so the calendar can
  link every surviving event to a current movie or episode.
- There is no user-facing history-read endpoint. Existing `/api/watch-history`
  routes manage provider connections and synchronization only.
- `/calendar` is a Monday-first month grid for release events. It already supports
  month navigation, poster thumbnails, three visible chips per day, and a full-day
  overflow dialog.

## Possible Approaches

### Approach A: Mix Releases and Plays in One Grid

Render future releases and past viewing events together, distinguished by color
and icon.

Pros:

- Gives one chronological view of the user's media life.
- Requires no new top-level navigation or mode switch.

Cons:

- A release chip is actionable (set a reminder), while a play is historical
  (open the title); identical placement hides that difference.
- Busy days become unreadable, especially when several episodes and release dates
  coincide.
- Color alone is not a sufficient distinction and creates accessibility pressure.
- Existing release controls would appear relevant while the user is inspecting
  past activity.

### Approach B: Add a Separate History Page

Add a primary **History** destination with its own calendar and agenda layouts.

Pros:

- Clear semantics and room for history-specific filters and statistics.
- No risk of changing the current release-calendar behavior.

Cons:

- Adds another primary tab to an already broad navigation bar.
- Duplicates month navigation, responsive layout, date helpers, loading states,
  and overflow behavior.
- Makes two calendar-shaped surfaces feel unrelated even though users naturally
  expect to switch between future and past dates.

### Approach C: Two Modes in the Existing Calendar

Keep one **Calendar** destination and add a visible `Releases | Watched` segmented
control. Each mode owns its event rendering and toolbar; only month navigation and
the underlying calendar shell are shared.

Pros:

- Preserves the existing navigation while keeping the two meanings separate.
- Reuses proven calendar behavior without forcing both event types into one cell.
- Provides stable deep links such as `/calendar?view=watched&month=2026-07`.
  This is new work — the current page keeps its month in memory only — but the
  release view gains the same shareable URLs from it.
- Leaves room for a later explicit `Combined` mode if users actually need it.

Cons:

- The calendar component must be split into a reusable shell and mode-specific
  content rather than accumulating conditional branches.
- Toolbars change with the selected mode, so the mode control must remain obvious.

### Approach D: Year Activity Heatmap

Show one square per day, with intensity based on the number of completed titles or
episodes.

Pros:

- Excellent for spotting streaks, gaps, and viewing-density patterns.
- Compact enough to show a whole year.

Cons:

- Does not answer the primary question: *what did I watch?*
- Encourages questionable metrics because history records completion, not actual
  minutes watched.
- Works better as optional statistics above or below a real history view.

## Recommended Experience

Use Approach C. The page's single job in **Watched** mode is to answer: "What did I
finish on this date?"

### Desktop

```text
Calendar    [ Releases | Watched ]       <   July 2026   >  Today
            [ All ] [ Movies ] [ Episodes ]    Undated 12

 Mon          Tue          Wed          Thu          Fri ...
+------------+------------+------------+------------+------------+
| 6          | 7          | 8          | 9          | 10         |
| [poster]   |            | [poster]   |            | [poster]   |
| Dune  21:14|            | Severance  |            | Arrival    |
|            |            | 3 episodes |            | 22:03  x2  |
+------------+------------+------------+------------+------------+
```

- A movie row uses poster, title, and local completion time.
- Episodes from the same series on the same calendar day always collapse to
  **one card carrying the series poster**, such as `Severance · 3 episodes`. A
  binge day of 10+ episodes still occupies exactly one card — the day cell has
  no room for more, and the series is the unit the user remembers. This is
  presentation grouping, not history deduplication; every individual episode
  and its exact time remains in the day detail.
- Multiple plays of the same movie on one day show `x2`. Every timestamp remains
  available in the day detail.
- At most three compact groups appear in a cell. `+N more` opens the complete day.
- Selecting a single-play row opens the movie or episode detail. Selecting an
  aggregated row (`x2` or several episodes), the day, or its overflow opens a
  chronological **Watched on this day** dialog with poster, movie title or episode
  code, exact local time, and a subtle source label when the play was imported.
- Release-only actions (`Add title`, tracked titles, and reminders) disappear in
  Watched mode. They are replaced by kind filters and an `Undated N` entry when
  timeless watched marks exist.
- An empty visible month says `Nothing watched this month`. When older exact
  history exists, it also offers `Jump to last watched month` rather than making
  the user hunt backward.

### Mobile

Do not compress seven columns into unreadable chips. Keep the month switcher and
render an agenda grouped by date:

```text
Wed, Jul 8                                      3 plays
  [series poster] Severance · 3 episodes   19:42–21:31

Fri, Jul 10                                     2 plays
  [poster] Arrival                                  x2
```

The agenda uses the same series-per-day card as the grid — one row per series
with the series poster and the first–last completion times; individual episodes
expand in the day detail. The same API data and grouping helpers should drive
the desktop grid and mobile agenda so their counts cannot diverge.

### Visual Direction

Treat the view as a restrained **screening diary**, not a generic activity
dashboard.

- Palette: the existing neutral tokens (`--background`, `--foreground`,
  `--muted`, `--border`) plus `--brand` — the app's single non-neutral
  "projector gold" hue, reserved here for the time notches. No new hex values:
  the page must follow the oklch token system so both themes keep working.
- Typography: Inter for controls and titles, Fraunces only when a media title is
  given room in the day detail, and Geist Mono for exact times and counts.
- Signature element: each exact play in the day detail gets one small
  projector-gold time notch beside its timestamp, making a day's list read like a
  screening log. Keep the month grid otherwise neutral.
- Motion: one short opacity/position transition when changing modes; respect
  reduced-motion preferences and avoid per-chip animation.

The rejected primary heatmap is intentional: it could belong in a later yearly
summary, but it is too generic and hides the media-specific information this view
exists to reveal.

## Data and API Shape

A bounded, authenticated read endpoint can sit alongside the provider-management
routes:

```http
GET /api/watch-history/calendar?from=<utc-instant>&toExclusive=<utc-instant>
```

The browser should compute the UTC instants for the visible local calendar range.
The backend filters exact `WatchedAt` values by those instants and returns raw
plays; the client formats and groups them in the browser's current time zone. This
avoids treating timestamps like the `DateOnly` values used by release tracking and
handles daylight-saving boundaries correctly.

Each response row needs:

- history-entry ID and `watchedAt`;
- media-item ID, kind, title, poster URL, and detail URL inputs;
- series title/ID plus season and episode numbers for episodes — and for them
  the poster input is the **series** poster: episodes rarely carry artwork of
  their own, and the grouped card is series-level anyway;
- origin, for optional provenance in the detailed view.

The response should be an envelope containing `events`, `undatedCount`, and
`latestWatchedAt`; the last value supports the honest empty-state shortcut without
loading all history. The backend should cap the requested interval (for example,
62 days) and add an index on `(AppUserId, WatchedAt)`. The existing
`(AppUserId, MediaItemId, WatchedAt)` index is optimized for one item's history,
not a user's time-range scan.

Timeless rows must not be assigned a fabricated date such as their creation or
sync date. Return their count through the calendar response or a small companion
endpoint and expose them in **Watched without a date** outside the grid.

## Risks

- The current release calendar is documented as planned while its UI and API are
  already present. The watched-history provider plan is also still `Draft`, while
  per-play storage and provider integration code exist. Before promoting this idea
  to planning, the repository's planning/feature documentation needs to be
  reconciled with implementation reality.
- Grouping in the browser can accidentally hide real rewatches. Aggregation must
  retain raw entry IDs and timestamps and expand losslessly in day detail.
- Browser time-zone changes can move an event to an adjacent day. This is correct
  for a local-time calendar, but the active time zone should be visible in the day
  detail when it differs from the app's configured time zone.
- A month with heavy episode viewing can still be dense. The three-group cell cap
  and mobile agenda are required, not optional polish.
- History deletion cascades with library-item deletion. A future permanent media
  diary would require decoupling history identity from the live library item; that
  is outside this display-only idea.

## Open Questions

- **Should releases and watched events ever be overlaid?** Current answer: not by
  default. **Recommendation:** ship only the separate `Releases` and `Watched`
  modes, then validate demand before adding `Combined`.
- **Which mode opens by default?** Current answer: the existing release calendar.
  **Recommendation:** keep `Releases` as the default for backward compatibility;
  encode an explicit selection in the URL instead of silently persisting it in
  browser storage.
- **What qualifies for a dated event?** Current answer: only an entry with an
  exact `WatchedAt`. **Recommendation:** never place manual or legacy timeless
  marks on a guessed date.
- **How should series episodes be grouped?** Decided: one card per series per
  calendar day, carrying the series poster, in both the grid and the mobile
  agenda; individual episode rows and times appear only in day detail.
  **Recommendation:** group strictly by series and calendar day — do not infer
  binge sessions or collapse episode ranges from temporal proximity.
- **Should users edit or delete history from the calendar?** Current answer: this
  request is about display, and remote ownership rules make deletion non-trivial.
  **Recommendation:** make the first version read-only; treat history correction
  as a separate idea.
- **Should imported and local plays look different?** Current answer: provenance
  matters for diagnosis but not for the memory itself. **Recommendation:** use the
  same grid treatment and show `Local` or the provider name only in day detail.
- **Should the page include watch-time totals or streaks?** Current answer: the
  system records completion timestamps, not actual viewing duration.
  **Recommendation:** show play/title counts only; defer duration and streak
  metrics until their semantics and value are established.

## Current Recommendation

Extend the existing `/calendar` surface with `Releases | Watched` modes. Preserve
the current release view and its actions unchanged. In Watched mode, show exact
history as a screening diary: compact, losslessly grouped entries in the desktop
month grid; a chronological day detail; and a date-grouped mobile agenda. Keep
timeless marks outside the calendar, use the browser's local day boundaries, and
return raw per-play events from a bounded user-scoped API.

Do not make a heatmap, combined release/history grid, editing, deletion, or
viewing-duration analytics part of the first slice.

## Links

- [Watched-history providers idea](trakt-watched-state-sync.md)
- [Watched-history provider planning](../planning/trakt-watched-state-sync.md)
- [Frontend application](../features/frontend-application.md)
- [Release tracking](../features/release-tracking.md)
- [Domain model](../features/domain-model.md)

## Notes

This is an exploratory product and interaction design. It is not approved for
implementation and must be promoted to a complete planning document before code
work starts.
