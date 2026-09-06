# Apple Series Browsing

Status: Draft
Created: 2026-09-06
Updated: 2026-09-06

## Goal

Make locally available TV episodes browsable and playable in the Apple TV client.
The current client renders series through the movie detail screen, including an
inappropriate series-level Play action, and discards the API's season summaries.

## Target behavior

- Series detail shows its synopsis and credits plus available seasons in numeric
  order, including season zero when present. No series-level Play action.
- Selecting a season opens its available episodes in episode order, with episode
  number/range, title, watched state, and resume position where available.
- Selecting an episode opens its details, sources, Play/Resume actions, and track
  information using the existing playback flow. Playback resolves the episode ID,
  never the series ID.
- Back returns to the selected season and episode. Playback progress refreshes
  the episode's status after returning from playback.
- Empty seasons, missing content, loading, errors with retry, and an older server
  without the episode endpoint have explicit states.

## Deliverables

- [ ] Add an authenticated native episode-list route using LibraryReadService's
  existing projection. Validate the series/season relationship and preserve
  published/removed-item visibility and per-user playback data.
- [ ] Regenerate native OpenAPI and Swift code; map seasons and episodes into
  client models without adding episodes to the top-level Movies/Series grids.
- [ ] Add season and episode browsing, replace series-level Play, and reuse the
  episode detail and playback pipeline with the episode identity.
- [ ] Preserve navigation/focus and refresh episode playback state on return.
- [ ] Add backend contract/visibility tests and client mapping, ordering,
  empty/error/older-server, and episode playback identity tests.
- [ ] Verify remote navigation through long lists, episode source selection,
  and Back in the simulator using local series fixtures.
- [ ] Verify real episode playback, resume persistence, and audio/subtitle choice
  on Apple TV with an updated server.
- [ ] Publish feature.md, remove this plan when its deliverables are complete,
  and regenerate the documentation index.

## Scope and versioning

This expands beyond [Apple client visual design](../apple-client-visual-design/plan.md),
which explicitly excludes episode browsing. Keep all implementation phases in one
feature PR. If included in the still-unreleased 0.72.0 server / 0.10.0 Apple client
release, retain those version bumps; otherwise version both components for the
release that ships this functionality.

Automatic next-episode playback, series-level Continue Watching aggregation,
and library management are outside this feature.

## Verification

Run backend tests, regenerate the Swift API, run MediaKit tests, build the tvOS
client, validate the docs index, and exercise the complete series → season →
episode → details → playback → Back flow. Record device acceptance separately
from simulator checks.
