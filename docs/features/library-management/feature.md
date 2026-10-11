---
created: 2026-10-11
updated: 2026-10-11
summary: Administrator movie and series tables with expandable versions, source diagnostics and filters, separated from personal library browsing.
components: [src/api, src/web]
---

# Library Management

## Browse and manage

Movies and Series keep their poster grids, descriptions, cast, personal ratings,
favorites and watch history. Movie details show a compact media summary; library
seasons keep their accordion, episode stills, synopses and personal watch controls.
File paths, version editors, conversion jobs and administrative mutations live in
Management. An administrator's movie/series toolbar and title or episode actions
link to the corresponding management context. These contextual links remain visible
under standalone, embedded and native Hosty launches; `ui.navigation` stays public.

`/manage` redirects to `/manage/movies`. Movies and Series tabs open
`/manage/movies` and `/manage/series`. Management renders only for the validated
admin session. Its read endpoints independently require `AppRoles.AdminPolicy`.
The BFF forwards the existing identity and the existing mutation endpoints retain
their authorization and operation admission rules.

## Lists and navigation

The movie table has one row per published movie. Columns include title/year,
catalog, version count, stored source size, availability, source issues and active
operations. Sortable columns have explicit controls; Columns changes visibility.
A labeled, keyboard-accessible disclosure opens every version inline, in the
existing default-source order. Details and source diagnostics load only after
expansion; title actions load details on demand. A filter selects movies, and
matching versions receive a label while alternatives stay visible. Version rows
do not change the title count or pagination.

A movie deep link, `/manage/movies/{id}`, uses the same table with the movie
expanded. Source links add `source={id}` and target the source after lazy loading.
A focused item that is removed or merged offers a useful empty/error return path;
a retained movie remains manageable only when its existing detail authorization
permits access. Correcting a movie match opens the surviving movie and carries a
return link to the originating list.

Series rows include stored season/episode totals and a distinct affected-episode
count. Issue sorting uses that distinct episode count, even when one episode has
several affected versions. Those counts describe the library, not provider completeness. Opening a
series gives `/manage/series/{id}`, season controls and a paginated episode table.
The affected count opens the current issue subset; an unfiltered count opens
Needs attention. `episode={id}` focuses an episode. Multi-episode ranges retain
labels such as `S01E01-E02`. Version details load only for expanded episodes.
Seasons retained for extras remain visible and deletable; series extras keep
their existing operation restrictions.

Search, filters, sort direction, page size/offset, hidden columns and expanded
IDs live in the management URL and survive reload and browser Back/Forward.
Query-only changes use Next.js's integrated History API, so successive actions
compose against the current URL. Deep links do not silently clear incompatible
filters. Open in library links return to the browsing surface. Tables scroll
locally on narrow screens rather than widening the page.

## Filters and query contract

ReUI Data Grid (TanStack Table v9) supplies the table, expanded rows, column menu
and pagination. ReUI Filters supplies a flat AND filter bar; values inside a
source field are OR alternatives. Both come from the configured Base UI registry
and use existing shadcn controls and theme tokens. Local registry adaptations
supply missing menu primitives, retain its imperative measurement/focus behavior
under React lint checks, and remove unused declarations.

`GET /api/management/items` accepts:

- `kind`: Movie (default), Series or Episode; optional `itemId`, `seriesId`,
  `season`, `catalogId` and literal title `search` (maximum 200 characters).
- `view`: all, attention, unavailable, bluray, dolby-vision or active.
- Comma-separated `sourceKind`, `resolution`, `codec`, `hdr`, `dvProfile` and
  `state`; `minVersions` / `maxVersions` apply to total owned versions.
- `sort`: title, year, size, versions, issues or episode; `descending`;
  `limit` defaults to 25 and is bounded to 1–100; nonnegative `offset`.

Technical conditions and issue predicates match the same source and its selected
picture stream. Cover art does not displace the picture. Search, filtering,
counting and stable sorting (with ID ties) execute in SQLite before pagination.
Quick counts use the current technical/search filters before the selected view;
they overlap and are not additive. Size sums each stored source once, with unknown
sizes labeled explicitly. Lists do not load tracks or probe the filesystem.

`GET /api/management/items/{id}/sources` returns diagnostics for all published
sources plus a match flag under the current filters. The location endpoint
`GET /api/management/items/{id}/location` resolves Activity links on demand, so
jobs that outlive deletion or merging do not expose stale mutation controls.

## Assessment and presentation

Availability is independent from source issues. The named reference is the
native Apple/tvOS capability profile with HDR10 and Dolby Vision enabled:
MP4/M4V/MOV direct containers, HEVC/H.264 video, AAC/AC-3/E-AC-3 audio, and
SDR/HDR10/Dolby Vision presentation. This is a stored-facts assessment, not TV
detection, full-file validation or an Infuse compatibility promise. The shared
`PlaybackEligibility` decision expression serves native negotiation and SQL
projections; management does not create playback sessions or stream URLs.

- BDMV sources require Create MKV; Blu-ray-derived MKV files are ordinary files.
- DV profile 7 carries an HDR10 fallback warning, rather than an automatic refusal.
- Offline or unanchored catalog storage overrides source eligibility.
- Missing media/DV-profile facts, including incomplete audio codecs, remain unknown. Unknown alternatives prevent a
  definitive No available version result.
- Index preparation uses bounded background observations (10,000 entries,
  ten-minute freshness). Waiting/indexing/saving is preparing; a recorded failure
  is unavailable; recent ready observations permit the remaining codec checks.
  Missing/expired observations remain unknown. This advisory snapshot does no
  filesystem I/O on a list request and a restart clears it.

A movie or episode with an eligible alternative is available while retained
problematic copies still appear in issue views. A series is available when at
least one stored episode source is eligible; its affected-episode count exposes
limitations elsewhere. Episodes without sources also need attention.

Authenticated browsing uses the separate, path-free
`GET /api/library/availability?ids=...` projection, bounded to 100 IDs per batch.
Movie/series poster grids show Unavailable or Preparing only when no eligible
copy exists. Title and episode details explain viewing-relevant DV fallback;
an eligible alternative without that limitation suppresses the fallback notice.
Complete source reasons and filter matches stay in Management. Unknown is not
shown as a green compatibility claim.

## Existing operations

Management reuses `MediaSources` and its dialogs: default version, rename,
conversion, Create MKV, track editing, extraction, merging, part joining,
sidecar removal and version deletion. Title menus own metadata/media refresh,
poster selection, movie matching, moves and deletion. Episode matching/deletion
and season deletion keep their existing scopes. Series moves lock episode/source
and season mutations. Last-version refusal still opens whole-item confirmation;
file and personal-data erasure remain off by default, including stale conflicts.
Episode match correction resolves the surviving episode's current series before
navigating, including when its ID stays the same while the series changes.

Removed titles stay outside published tables. Personal history stays in the
library. Administrators reach permanent deletion from the retained movie's
management entry or a removed series' Manage link; the series route uses
`removed=1` and the caller's own removed-title list. These links do not broaden
access to another user's retained history.

Related conversion/move components remain the existing jobs; Activity is the
shared operations page. Source mutations and coarse realtime events invalidate
management summaries, diagnostics and viewer availability. List/availability
polling provides a 30-second reconciliation fallback. Expanded conversion
completion refreshes source details without requiring collapse/reopen. A created
MKV can remain unknown/preparing until background indexing observes it.

## Testing Expectations

- xUnit SQLite tests cover mixed disc/file versions, DV alternatives, missing
  probes/profiles, offline/unanchored storage, preparation states, native rule
  parity, cover-art selection, removed exclusion, same-source filters, distinct
  episode counts, stable paging, literal search and bounded SQL command counts.
- Endpoint tests assert administrator versus authenticated-viewer policy metadata,
  query-default binding and rejection of invalid page windows.
- Indexing snapshot tests cover expiry, restart and preservation of known failure.
- Vitest covers management URL boundaries and episode targeting.
- Playwright covers lazy expansion with all versions, filter match labels,
  reload/Back, multivalue technical filters, sorting/paging, direct links, viewer exclusion,
  personal-action separation, mobile/embedded layout and series drill-down.
  Retained-series actions stay in management; corrected episodes open their current
  series whether remapping preserves or replaces the episode ID.
  Existing conversion, extraction, Blu-ray, joining, indexing, last-version,
  stale conflict, season deletion and retained-history scenarios run at their
  corresponding library or management entry points.
