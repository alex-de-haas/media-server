# Apple Client Visual Design and Collections

Status: In Progress
Created: 2026-09-06
Updated: 2026-09-06

## Goal

Give the Apple TV client the atmosphere of a personal cinema and make browsing,
resuming, and choosing a copy clear from a sofa. Add Collections as a separate
top-level tab. The user approved the visual direction and requested Collections
in chat on 2026-09-06 and approved implementation of this plan the same day.

## Current behavior and target changes

The baseline is [Apple Client](../apple-client/feature.md). `LibraryView` has
Movies, Series, and Settings tabs, with a uniform poster grid. `TitleView` is a
vertical text layout, although `TitleDetail` already exposes a backdrop URL.
Versions and full track lists share the main detail screen. Pairing shows an
address/code; Settings combines playback preferences with technical diagnostics.

The target is native tvOS navigation with cinematic artwork and restrained
chrome. Keep standard SF typography and a clear focus treatment. Follow the system light/dark appearance with an adaptive
neutral canvas, high-contrast primary text, quieter secondary text, and artwork
as the source of color. Avoid ambient animation and automatic video previews.
Check contrast over both bright and dark artwork, including increased-contrast
and reduced-motion settings.

### Main navigation and library

Top-level order: **Movies · Series · Collections · Settings**. Each browsing tab
keeps its own navigation path, scroll position, and focused item on return.
Collections stays visible with a useful empty state even when none qualify.

Movies gains a compact Continue Watching row above All Movies. Use existing
resume positions greater than zero for unwatched movies, in stable library
order; do not imply recency without a timestamp. Hide the row when empty.
Keep the ordinary Series grid: episode navigation and next-episode aggregation
are outside this visual change, so do not imply playable series-level resume.

Movie and series cards show a compact year or resume line below the poster,
without repeated titles or a focused-title header above shelves and grids. Center
the title inside the placeholder only when artwork is unavailable.
Scale poster and metadata together on focus with a short gap, retaining the
accessible title and watched/resume marks. Return from playback updates
both the row and grid, removing completed movies from Continue Watching.

Show a textual resume position rather than inventing a progress fraction:
`LibraryTitle` has no runtime. This plan does not add per-card detail requests
or extend sync just to draw progress bars. On the detail screen, duration is
available and any progress display must use a valid matching duration.

### Title screen

Use the existing authenticated backdrop loader for a wide cinematic header,
with a gradient protecting text at the left and transitioning into the page
below. Without artwork, use the same stable composition on the neutral canvas.
Do not stretch a poster into a backdrop.

The first viewport contains the title, compact year/runtime/rating facts,
primary Play or Resume from [time] action, a secondary From the Beginning action
when applicable, a short synopsis, and the selected version summary. Limit the
synopsis to 3–4 lines with an explicit expansion action for longer descriptions.
Preserve the watched mark, playback preparation state, and actionable refusals.

Show all editions as selectable inline rows with a checkmark for the playback
choice. Include container, video codec, size, dynamic-range badges and Dolby
Vision profiles in each row so sources can be compared without opening a picker.
Place format badges and compatibility notices on the right, with name and file
parameters on the left; label unnamed sources Original.
Keep compatibility notices such as Plays as HDR10 on this device beside their
source. Put full audio/subtitle lists in an expandable section for the selected
version. The version choice must still determine playback; technical presentation
must not change track selection or capability negotiation.

### Collections

Reuse the existing [Collections](../collections/feature.md) domain: TMDb movie
franchises spanning catalogs, owned and visible movies only, at least two
qualifying movies. No manual collections, series collections, or unowned films.

The Collections tab shows a poster grid with collection names and movie counts.
Use collection artwork, falling back to an eligible member poster and then a
stable placeholder. Collection detail uses the same cinematic header language
and a member grid in release order (unknown year last, then title and stable ID
for ties). Opening a member uses the existing title screen and playback flow.

The native OpenAPI currently has no collection list/detail routes. Add
authenticated `GET /native/v1/collections` and
`GET /native/v1/collections/{id}` projections over the existing collection read
logic, with native item IDs suitable for opening `TitleView`. Share eligibility,
ordering, and fallback logic rather than creating a separate collection model.
Ensure removed/unpublished movies cannot appear in counts, members, or artwork
fallbacks. Missing or no-longer-eligible collections return a recoverable 404.

Add authenticated collection artwork routes under
`/native/v1/collections/{id}/images/{imageType}` with server-local URLs and cache
tags/ETags, reusing the existing image cache. The current item image route
resolves `MediaItems` and cannot serve collection IDs. The client must not fetch
provider CDN artwork directly. Regenerate committed native OpenAPI and the
Swift client/hash together, and verify public-binding authorization.

On older servers that lack the new routes, keep other tabs usable and show an
update-required state in Collections. Distinguish this from an empty collection
list and ordinary network failure. A detail 404 returns the viewer to a refreshed
collection list with an explanation rather than declaring the server outdated.

### Settings, pairing, and loading

Group Settings into Server, Playback, and Diagnostics, moving capabilities and
loader details into expandable technical content. Preserve every existing
preference and its persistence, including dynamic-range override and sign-out.

Show a locally generated QR code for the server-provided verification URI beside
the large pairing code. Preserve the readable URI and manual instructions; do
not construct an approval URL or encode credentials. When no URI is supplied,
keep the existing manual Hosty instructions. Expiry, renewal, and cancellation
continue to follow `PairingSession`.

Use stable poster/header placeholders while images arrive. Differentiate loading,
empty, failed, and unsupported-server states with concise explanations and retry
actions where useful. Missing artwork must never block navigation or playback.

## Dependencies and ownership

- [Apple client core](../apple-client-core/plan.md) retains ownership of the local
  SQLite mirror, sync reset/tombstones, and remaining core work. This plan changes
  presentation over the current in-memory store and does not depend on the mirror.
- [Apple client loading](../apple-client-loading/plan.md) retains playback loading
  and performance work. Coordinate edits to shared views; preserve its diagnostics
  switches and do not change the player/loader to achieve visual effects.
- [Native client API](../native-client-api/feature.md) supplies authentication,
  OpenAPI, and image-serving conventions. This feature owns its collection route
  additions; existing live-verification work stays in that feature's plan.
- Collections domain rules and web/Infuse behavior remain shared. Any reusable
  query changes require regression coverage on those surfaces.
- iOS, iPadOS, macOS shells, discovery recommendations, and episode browsing are
  not deliverables of this tvOS design feature.

## Deliverables and phases

All phases ship on one feature branch and one PR; phases are implementation
order, not separate releases.

### Phase 1 — visual foundation and title screen

- [x] Define shared spacing, text, artwork, badge, and focus treatments with
  SwiftUI previews for long titles and missing images.
- [ ] Verify bright/dark backdrop previews and contrast with real artwork.
- [x] Implement the cinematic title header, primary/secondary playback hierarchy,
  synopsis expansion, inline source rows with format badges, and expandable track details.
- [x] Preserve playback refusals, compatibility notices, sidecar identification,
  selected-version semantics, and resume/watched refresh behavior.

### Phase 2 — library and navigation

- [x] Add the four top-level tabs in the agreed order and preserve navigation,
  scroll, and focus on back navigation and tab switches.
- [x] Refine poster cards, consistent status marks, spacing, and loading fallbacks.
- [x] Add a compact HDR/Dolby Vision summary to library card responses and the
  generated Apple API model, then display it beside the year without Dolby Vision
  profiles. Server and client contract changes are included in this PR.
  Summarize available movie sources rather than implying device playback support.
  Omit SDR/unknown labels and avoid fetching individual title details for the grid.
  Cover mixed-source aggregation, missing probe data, and older server responses
  in backend and Apple client tests.
- [ ] Validate HDR/Dolby Vision card caption readability on Apple TV.
- [x] Add Continue Watching with honest resume labels, empty-row hiding, and
  refreshed state after playback across all visible instances of a movie.

### Phase 3 — Collections end to end

- [x] Add native collection list/detail and artwork contracts/routes with shared
  visibility, count, ordering, and artwork fallback rules.
- [x] Regenerate OpenAPI and Swift sources/hash; add MediaKit collection models
  and loading/error handling through the existing authenticated session.
- [x] Implement collection grid/detail, member-to-title navigation, focus return,
  and empty, removed-collection, retry, and older-server states.

### Phase 4 — supporting screens and acceptance

- [x] Group Settings and preserve saved preferences and access to diagnostics.
- [x] Add pairing QR code with URI-absent fallback, preserving the tested
  pairing renewal/cancellation state machine. Visual QR acceptance remains below.
- [x] Complete unit/contract tests and initial simulator navigation/focus checks.
- [ ] Complete real-instance collection/artwork/playback and pairing QR checks,
  including expired/renewed QR rendering; the simulator fixture does not verify these.
- [ ] Complete Apple TV Siri Remote, sofa-distance readability, VoiceOver,
  increased contrast, and Reduce Motion acceptance checks.
- [x] Update Apple client, Collections, and native API reality docs; create this
  feature's `feature.md` with Testing Expectations and regenerate the docs index.
- [ ] Investigate and verify a fix for the intermittently missing Series tab
  symbol, observed on both the simulator and a physical Apple TV.
- [ ] Delete this plan and regenerate the index after all acceptance checks pass.
- [x] Apply independent minor version bumps at implementation shipment: Apple
  `MARKETING_VERSION` for the client, `manifest.json` for the new server API.
  Use then-current versions and leave `schemaVersion` unchanged.
  PR description lists deliverables, links this
  feature folder, and states both version outcomes and verification results.

## Open questions

No unresolved product questions. Initial layout values are validated in previews
and on a television within the approved visual direction.

## Verification steps

- Run `node scripts/docs-index.mjs --fix` and
  `node scripts/docs-index.mjs --check` for documentation changes.
- For implementation, run `dotnet build --configuration Release` and
  `dotnet test --configuration Release --no-build --verbosity normal` from
  `src/api`. Backend coverage uses xUnit and Imposter for mocked dependencies.
  Cover collection eligibility/count parity, removals, chronological order,
  fallback images, authenticated routes, 404s, cache tags, and existing web/Infuse
  behavior affected by shared query changes.
- Run `scripts/generate-apple-client.sh`, check generated contract/hash freshness,
  and run `swift test` from `src/apple/MediaKit`. Cover collection decoding and
  errors, resume filtering/formatting, state refresh, and credential handling.
- Build `MediaServerTV` using the Xcode project and an available tvOS simulator
  destination. Follow `src/apple/README.md` for the environment's build workflow.
- Check Movies → title → selected version → play/resume → back, Collections →
  collection → movie → playback → back, all tab switches, Settings persistence,
  and pairing with/without a URI against a Core-managed development instance.
- Verify Siri Remote focus on Apple TV hardware, readability from sofa distance,
  long titles, missing artwork, slow/failed requests, large and empty libraries,
  VoiceOver labels, increased contrast, and Reduce Motion. Capture representative
  screenshots for review. Simulator success alone is not hardware acceptance;
  record unavailable checks as unfinished deliverables rather than passed checks.

## Verification recorded on 2026-09-06

- `dotnet build --configuration Release`: passed; existing unrelated warnings.
- `dotnet test --configuration Release --verbosity normal`: 2,054 passed.
- Added route-metadata test after the full run: targeted `NativeCollectionTests`
  run passed all 3 tests, including authorization/public routing.
- `scripts/generate-apple-client.sh`: passed, contract/hash updated. Existing
  generator schema warnings remain; no skipped schemas.
- `swift test`: 161 tests passed across 27 suites.
- Unsigned tvOS Simulator build: passed. Local preview exercised movie detail,
  back navigation, top-level Collections, and collection detail with missing art.
- Prepared version changes: server 0.71.0 → 0.72.0; Apple client 0.9.6 → 0.10.0.
- Real-instance and hardware acceptance remain unchecked above. No server
  deployment or physical-device installation has been performed in this change.

Card/theme refinement on 2026-09-06: native decoration is restricted to the
poster; captions have independent spacing and padding. Forced dark appearance
is removed. Simulator preview checks passed for dark and light appearance
(the tvOS runtime does not support `simctl ui appearance`, so the light check
uses a debug-only SwiftUI appearance override). MediaKit: 161 tests passed.

Sign-out contrast refinement: replaced the destructive red styling with a
neutral native bordered button and exit symbol. The action still unpairs the
device. Simulator build and 161 MediaKit tests passed.

Continue Watching focus regression: reproduced Down failing while the lazy
All Movies grid was below the viewport. Added explicit reveal-then-focus
navigation and focus sections. Simulator verified Down into All Movies and Up
back to Continue Watching, with poster-only decoration retained.
