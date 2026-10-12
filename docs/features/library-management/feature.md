---
created: 2026-10-11
updated: 2026-10-12
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
expansion; title actions load details on demand. The title action trigger always
uses a three-dot icon, and its menu includes Open in library for movies, series
and episodes (opening the episode's series). A filter selects movies, and
matching versions receive a label while alternatives stay visible. Version rows
do not change the title count or pagination.

Expanded versions form a compact nested table with aligned file/track, type,
format/details, size/rate, status and action columns. A package icon identifies a
file container; Blu-ray sources keep their disc icon. Each container has its own
keyboard-accessible disclosure for embedded video, audio, subtitle and other
tracks. Track rows show the recorded metadata, bitrate, a Default badge and forced flags;
an absent track bitrate stays unknown rather than using the container's rate.
Embedded rows align their icons and names with the container. Separate audio and subtitle
files use the same columns and type icons and remain visible when the container
is collapsed. Origin labels distinguish Embedded from Separate file; external
audio carries a Merge for playback hint. Availability belongs to the container.
Embedded tracks come from one container; separate audio and subtitle files from
any version of the same title can join that selection. Selecting a separate file
first does not lock the container choice. A compact action row
below the header offers Extract, Remux, Convert and Clear selection, with reasons
for unavailable operations. Selecting another container requires clearing the
embedded selection; collapsing its tracks preserves the selection. Removed sources
and stream IDs are pruned, and changed selected facts close an open confirmation.

Each eligible embedded audio/text-subtitle row also offers direct Extract. Batch
extraction uses exactly the checked tracks. Remux and Convert require one video
plus audio, optionally subtitles. They open fixed-selection confirmations with
editable track names, including video. Convert also exposes codec, resolution,
quality, encoder and selected-audio settings; Remux optionally rewrites eligible
Dolby Vision 7 to 8.1. Containers expand through the chevron; track selection uses
the row checkboxes. There is no second track picker or sidecar Merge button. See
[Remux and Convert](../convert-dialog/feature.md).

Administrators with no move lock can select tracks. Engine availability gates
extraction; the explicit-video capability additionally gates Remux and Convert.
Failed submissions preserve the selection; successful submissions clear it.

The full content-folder path and Join parts share a slim toolbar. The path joins
the catalog root and relative folder; the catalog name stays in the parent row.
Indexing progress, Blu-ray
import and existing version actions remain available. Long filenames wrap within
their column, and the table scrolls locally when the viewport is narrow.

Availability uses a labeled icon with restrained color in both title and version
rows: green for Available, red for Unavailable, violet for Preparing and neutral
for Not assessed. Amber marks source issues and Dolby Vision caveats without
changing the availability assessment. Color never replaces the status text.

Movie links open `/manage/movies?focus={id}&expanded={id}`. The list locates the
movie's page in ascending title order and expands its versions alongside the other
movies on that page. It replaces the temporary focus with the concrete offset, so
paging, reload and browser history continue to work normally. Legacy
`/manage/movies/{id}` addresses redirect to this list and preserve source,
retained-title and return context; there is no separate movie management screen.
Source links add `source={id}` and target the source after lazy loading.
A missing or filtered-out focus leaves the list usable and offers retained-title
management. Correcting a movie match opens the surviving movie and carries a
return link to the originating list.

Series rows include stored season/episode totals and a distinct affected-episode
count. Issue sorting uses that distinct episode count, even when one episode has
several affected versions. Those counts describe the library, not provider completeness. Opening a
series gives `/manage/series/{id}`, season controls and a paginated episode table.
Series drill-down keeps source filters and starts with ascending episode chronology,
resetting both the series list's sort key and direction.
The affected count opens the current issue subset; an unfiltered count opens
Needs attention. `episode={id}` focuses an episode. Multi-episode ranges retain
labels such as `S01E01-E02`. Version details load only for expanded episodes.
Seasons retained for extras remain visible and deletable; series extras keep
their existing operation restrictions.

Search, filters, sort direction, page size/offset, hidden columns and expanded
IDs live in the management URL and survive reload and browser Back/Forward.
Query-only changes use Next.js's integrated History API, so successive actions
compose against the current URL. A focus query respects its supplied filters;
links from browsing and Activity open the ordinary unfiltered title list.
Open in library links return to the browsing surface. Management lists
and drill-down pages use the full available width with 24 px side padding in both
standalone and embedded launches. Tables scroll locally on narrow screens rather
than widening the page.

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
- `focusId`: optional movie ID for locating its containing page. Requires Movie,
  ascending title order and no `itemId`. A bounded SQL rank query uses the same
  title/ID ordering as pagination. Missing or excluded targets leave the requested
  offset unchanged; this parameter never filters the list to one item.

Technical conditions and issue predicates match the same source and its selected
picture stream. Cover art does not displace the picture. Search, filtering,
counting and stable sorting (with ID ties) execute in SQLite before pagination.
Episode chronology sorts both season and episode numbers in the requested
direction; equal numbers retain the same ID tie-breaker across pages.
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

- BDMV sources show a single Requires conversion status and a Create MKV
  icon in the source actions; Blu-ray-derived MKV files are ordinary files.
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
management entry or a removed series' Manage link. Both use `removed=1` and the
caller's own removed-title list to open the retained-title dialog. Movies use the
shared movie list route; series retain their series route. These links do not
broaden access to another user's retained history.

Related conversion/move components remain the existing jobs; Activity is the
shared operations page. Inline Conversions cards omit Manage because their
versions are already visible beneath them. Cancelling a queued or running job
requires confirmation naming the job, both here and in Activity. A failed cancel
keeps the job and confirmation available for retry. Completed and cancelled jobs
automatically disappear from the inline section without deleting their history;
the section disappears when no visible jobs remain. Failed jobs keep their error
and dismissal control visible. Activity retains the full job history.
Activity cards retain Manage and resolve the current owner before opening the
shared movie list or series context. Source mutations and coarse realtime events invalidate
management summaries, diagnostics and viewer availability. List/availability
polling provides a 30-second reconciliation fallback. Expanded conversion
completion refreshes source details without requiring collapse/reopen. A created
MKV can remain unknown/preparing until background indexing observes it.

## Testing Expectations

- xUnit SQLite tests cover mixed disc/file versions, DV alternatives, missing
  probes/profiles, offline/unanchored storage, preparation states, native rule
  parity, cover-art selection, removed exclusion, same-source filters, distinct
  episode counts, ascending/descending episode chronology, stable paging,
  literal search, focused movie pagination with equal-title ties and missing or
  excluded targets, and bounded SQL command counts.
- Endpoint tests assert administrator versus authenticated-viewer policy metadata,
  query-default and focus binding, and rejection of invalid page windows.
- Indexing snapshot tests cover expiry, restart and preservation of known failure.
- Vitest covers management URL boundaries and episode targeting.
- Playwright covers lazy expansion with all versions, filter match labels,
  reload/Back, multivalue technical filters, sorting/paging, direct links, viewer exclusion,
  personal-action separation, mobile/embedded layout and series drill-down.
  Container disclosures work independently with Enter/Space, preserve external-file
  selections and expose embedded track types, metadata and default/forced flags.
  Legacy movie links resolve the containing page without hiding neighboring movies;
  source targeting and Back navigation survive. Activity retains management links
  while inline conversion cards omit them. Removed movies and series retain their
  administrative actions in management. Conversion cancellation requires explicit
  confirmation; declining or escaping sends no request, and a failed request keeps
  the job visible. Polling removes completed/cancelled inline jobs without deleting
  history, refreshes the versions and leaves failed jobs visible. Corrected episodes
  open their current series whether remapping preserves or replaces the episode ID.
  Existing conversion, extraction, Blu-ray, joining, indexing, last-version,
  stale conflict, season deletion and retained-history scenarios run at their
  corresponding library or management entry points.
