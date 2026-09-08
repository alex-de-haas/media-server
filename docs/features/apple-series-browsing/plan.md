# Apple Series Browsing — Device Acceptance

Status: In Progress
Created: 2026-09-06
Updated: 2026-09-08

## Goal

Complete acceptance of the implemented [series browsing](feature.md) on a real
Apple TV with an updated server. The user approved development in chat on
2026-09-08. Implementation is on one branch from main, `feat/apple-series-browsing`.

## Remaining deliverables

- [ ] Verify real episode playback, resume persistence, and audio/subtitle choice
  on Apple TV with the updated server and a real series. Refresh catalog metadata
  and confirm episode-specific stills and synopses for existing episodes.
- [ ] Record device acceptance, remove this plan after the final deliverable, and
  regenerate the documentation index in the same change.

## Implemented scope and verification

The implementation and required regression coverage are described in `feature.md`.
The native route, season filtering, episode metadata/stills, generated Swift API,
client mapping, in-place season selection, episode details/playback identity,
request-race protection, refresh and focus restoration are implemented.

- Full backend test run: 2100 passed. Additional targeted checks cover the final
  native-route authentication and relationship validation changes.
- MediaKit: 195 tests passed, including season ordering, artwork fallback,
  cancellation races, API authentication and episode playback identity.
- tvOS simulator build succeeded. Local fixtures exercise season focus changes,
  an 18-episode horizontal rail, episode details, selection of the 1080p source,
  and Back to the previous card.
- Simulator fixtures use placeholder artwork and do not establish successful
  playback, provider image downloads or progress persistence on physical hardware.

## Scope and versioning

Server: 0.75.0 → 0.76.0. Apple client: 0.11.1 → 0.12.0. Keep implementation and
acceptance in one feature PR. No commits are created without the user's request.

Automatic next-episode playback, series-level Continue Watching aggregation,
and library management remain outside this feature.
