# Manual and Smart Groups

Created: 2026-09-15
Updated: 2026-09-15

## Behavior

Groups organize library titles independently of their physical catalogs and
[TMDb franchise collections](../collections/feature.md). A title belongs to any
number of groups without duplicating its files, versions, or playback history.
Every group has one fixed catalog type: **Movie**, **Series**, or **Anime**.
Membership spans catalogs of that type and never mixes movies with series or
Series catalogs with Anime catalogs.

Both web and Apple TV place **Groups** between **Series** and **Collections**.
The first page shows groups as folders with their names, catalog types, and
current title counts. Opening a folder shows the existing poster grid and links
to the ordinary movie or series detail and playback flow. Empty groups remain
visible; the two-owned-movie threshold for franchises does not apply.

## Configuration

Administrators create, edit, and delete groups under **Settings → Groups** in
the web app, following the same permissions as catalog configuration. Groups
are shared across the server and browsable by every authenticated app user.
Apple TV browses groups; configuration lives in web Settings.

Each definition has a name, fixed catalog type, and fixed mode:

- **Manual:** the administrator selects titles with a searchable, paged picker.
  Selections persist across search and pagination. Saving the same selection
  twice does not create duplicate links.
- **Smart:** the administrator saves a flat list of conditions, joined with
  **All conditions (AND)** or **Any condition (OR)**. The editor previews the
  matching count and first twelve titles before saving. Changing the definition
  makes the previous preview visibly stale. Smart groups have no manual overrides.

Names contain 1–120 characters. Definitions use rule schema version 1, with up
to 20 conditions. Smart groups require at least one condition; manual groups
accept up to 10,000 title IDs and contain no conditions. Mode and catalog type
cannot change after creation.

## Filters

| Field | Supported conditions |
| --- | --- |
| Release year | Before, after, equals, inclusive range |
| Resolution | 480p, 720p, 1080p, 2160p / 4K |
| HDR format | HDR10, HDR10+, Dolby Vision, HLG, HDR with unspecified type, Any HDR |
| Tag | Exact existing metadata keyword value |
| Genre | Exact existing metadata genre value |

Year and tag/genre conditions apply to the movie or series itself. Release year
uses `MediaItem.Year` with a metadata release-date fallback, preferring the
configured language, then its primary language subtag. A missing year does not
match. Before 2000 excludes releases in 2000. A range includes both endpoints.

Tags use `MetadataTagKind.Keyword`; genres use `MetadataTagKind.Genre`. The
searchable options endpoint returns up to 100 matching values per kind from
visible titles of the chosen catalog type. Conditions match whole values across
cached metadata languages with SQLite NOCASE comparison, not synopsis substrings.
Repeated tag or genre conditions follow the group's AND/OR choice. Unknown or
absent values do not satisfy a condition.

### Sources and HDR

Source conditions match the probed video streams. Resolution uses the same
ordered width/height buckets as `VideoResolution.Label`, including cropped
widescreen films. Filenames and the selected default version do not decide
membership.

Each recognized HDR type has its own exact-token filter. A combined value such
as `HDR10 · Dolby Vision` matches each constituent filter. HDR10+ alone does not
match HDR10. **HDR (unspecified type)** matches the generic `HDR` token, whereas
**Any HDR** matches all five recognized types. SDR and unrecognized format
strings match none of the HDR filters.

For AND rules, all source conditions must match **one video stream of one
source**. A 4K SDR version and a separate 1080p Dolby Vision version do not
satisfy `4K AND Dolby Vision`. OR rules accept either condition. One or several
matching sources yield one title and one count.

For Series and Anime, source conditions inspect published, non-removed episodes.
One matching episode source includes its parent series once. Episode rows and
seasons never appear as group members. Series and Anime remain separated by
catalog type even though both have `MediaKind.Series` top-level records.

## Data and lifecycle

`MediaGroup` stores the name, mode, catalog type, and versioned rules JSON.
`MediaGroupMember` stores manual links with a composite primary key on group and
title IDs. These tables are separate from `MovieCollection` and
`MediaItem.CollectionId`, so TMDb refreshes and franchise recommendations retain
their existing semantics.

Smart membership is evaluated in SQL on every read from current metadata and
sources. Counts, saved groups, and previews share the same evaluator; filtering
and deduplication precede pagination. Detail pages default to 60 titles and are
bounded to 100 per request, ordered by a language-ranked metadata title and then
item ID. The web client refreshes visible group data periodically and invalidates
it when library events arrive. Apple TV refreshes folders on screen appearance
and group pages when opened or paged.

- Catalog moves preserve manual links. Movie and series merges union incoming
  memberships onto the surviving title inside the move transaction.
- A Series/Anime cross-type move hides the title from groups of the previous
  type. Existing links remain editable and become visible if the title returns.
- Removed titles are absent from all visible members and counts. Manual links
  survive a tombstone and restore visibility when the identity revives; a hard
  purge cascades the links away.
- A published title with no sources can match title-level rules, but cannot
  match resolution or HDR. Removing the last matching source or refreshing its
  probe data changes source-based membership on the next read.
- Deleting a group removes its definition and links only. It never deletes
  library titles, media sources, files, or user playback data.

## API and clients

All `/api/groups` routes require authentication. List and detail are readable by
all app users; definitions, pickers, preview, and writes require the admin policy.

| Route | Purpose |
| --- | --- |
| `GET /api/groups` | Folder summaries, including empty groups |
| `GET /api/groups/{id}?limit=&offset=` | Paged members with the caller's user data |
| `GET /api/groups/{id}/definition` | Editable definition and manual IDs |
| `GET /api/groups/options?catalogType=&search=` | Supported formats and searchable tags/genres |
| `GET /api/groups/candidates?catalogType=&title=&limit=&offset=` | Manual title picker |
| `POST /api/groups/preview?limit=&offset=` | Evaluate an unsaved definition |
| `POST /api/groups` | Create a group |
| `PUT /api/groups/{id}` | Replace a definition and manual membership |
| `DELETE /api/groups/{id}` | Delete a group only |

Invalid definitions return HTTP 400; unknown groups return 404. Native clients
use authenticated `GET /native/v1/groups` and `GET /native/v1/groups/{id}` with
the same limit/offset parameters. Member artwork uses authenticated native item
image routes. OpenAPI and the generated Swift client carry these contracts.

Apple TV distinguishes loading, empty, failed, unsupported-server, and missing
group states. Failed reads offer retry. The group grid uses existing title
components, preserving movie and series navigation and playback behavior.
Jellyfin/Infuse continues to expose the existing franchise collections.

## Testing Expectations

- `GroupServiceTests` executes the migration and evaluator against SQLite:
  every HDR type, generic/any HDR distinction, exact and combined tokens,
  cropped resolutions, multiple versions, same-source AND versus OR, year
  boundaries and metadata fallback, whole tags/genres, catalog-type isolation,
  episode-to-series aggregation, page/count consistency, input validation,
  per-user state, removal/revival/purge, and editing links after type moves.
- `GroupEndpointTests` checks authentication, admin-only settings/writes, and
  read-only public native routes.
- `LibraryMoveServiceTests` checks manual membership preservation on ordinary
  moves and deduplicated membership unions for movie and series merges.
- `groups.spec.ts` covers settings creation/edit/delete, folder navigation,
  one title card per result, every HDR option, tags/genres, typed series groups,
  preview, read-only viewers, and failure/retry. Existing settings, shell, and
  catalog browsing tests protect adjacent navigation and controls.
- Swift `GroupTests` covers folder/page decoding, bearer authentication,
  empty/unsupported/error/missing states, pagination, refresh, and concurrent
  list-load deduplication. Build the tvOS simulator app to check SwiftUI integration.
- Run backend and web tests, web lint/build, Swift tests, tvOS build,
  `bash scripts/validate-manifest.sh`, and `node scripts/docs-index.mjs --check`.
  Regenerate the Swift client after changing native routes.
