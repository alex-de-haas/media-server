# Catalog Library Browsing

Status: Promoted
Created: 2026-07-12
Updated: 2026-07-12

## Motivation

Movies and series can currently be browsed only as type-wide lists. Operators may
configure multiple catalogs of the same type, such as `Movies 4K`, `Kids`,
`Series RU`, or `Anime`, but the web UI does not let a user narrow a library grid
to one of those catalogs.

The browsing experience should expose catalogs without mixing media discovery
with the existing admin-only catalog configuration workflow.

## Behavior at Time of Proposal

- `/movies` and `/series` render the same top-level library response and filter
  it by media kind in the browser.
- The internal `GET /api/library` endpoint already accepts both `catalogId` and
  `kind` query parameters.
- `GET /api/catalogs` is available to every authenticated user.
- `/catalogs` is an admin-only operational page for configuring paths, viewing
  storage usage and offline state, scanning, refreshing metadata, and removing
  catalogs.
- Movie and series detail pages currently return to the unfiltered `/movies` or
  `/series` route.

## Possible Approaches

### Approach A: Dedicated Catalog Browsing Page

Add a user-facing catalog gallery. Opening a catalog shows only the movies or
series stored in it.

Pros:

- Catalogs become prominent first-class library destinations.
- A catalog can later have its own artwork, description, and mixed-content
  presentation.
- This resembles the separate-library model exposed to Jellyfin clients.

Cons:

- The existing `/catalogs` route and navigation label already mean admin
  configuration, so a new browse surface needs a different name and route.
- It adds another primary navigation destination to an already full top tab bar.
- It duplicates the poster-grid browsing flow and forces an extra click for a
  common task.
- A catalog has one configured type today, so a dedicated mixed movies/series
  page does not provide much additional value.

### Approach B: Catalog Filter on Movies and Series

Add a catalog selector below the Movies and Series headings. Keep `All` as the
default and represent the selection in the URL, for example
`/movies?catalog=<id>`.

Pros:

- Keeps the user's primary question first: browse movies or browse series.
- Reuses the existing grids and detail routes.
- Requires no new backend contract because catalog and kind filters already
  exist.
- Supports direct links, refresh, browser history, and returning to the same
  filtered result.
- Avoids changing the meaning or permissions of the admin Catalogs page.

Cons:

- Catalogs are less visually prominent than on a dedicated gallery page.
- A long catalog list needs a compact responsive control rather than an
  ever-growing row of tabs.
- Detail links and back navigation must explicitly preserve the selected
  catalog.

### Approach C: Filter First, Catalog Shortcuts Second

Use Approach B as the primary browsing model, then add a `Browse media` action
to each row on the admin Catalogs page. The shortcut opens the matching filtered
Movies or Series page.

Pros:

- Provides fast catalog-first navigation for admins without duplicating the
  browsing UI.
- Preserves the separation between catalog management and media browsing.
- Leaves room for a dedicated user-facing catalog gallery later if catalogs gain
  artwork or mixed-content behavior.

Cons:

- The shortcut is only discoverable to administrators.
- It adds a small amount of navigation logic to the catalog management screen.

## Risks

- Reusing the current React Query key for differently filtered requests could
  show stale results. Query keys must include media kind and catalog id.
- Returning from a detail page to an unfiltered grid would make the filter feel
  unreliable. The catalog context must be carried into detail links and the back
  link.
- Series browsing includes both `Series` and `Anime` catalog types. The selector
  must offer both, while the Movies page must offer only `Movie` catalogs.
- Offline catalogs retain their published items by design. The selector should
  not silently hide them; an offline indicator can explain why playback or file
  actions may be unavailable.
- At proposal time, the feature documentation described library grids as
  infinitely paginated while the UI loaded the full library response. The
  implementation reconciled the documentation with the existing behavior;
  pagination remained outside this idea's scope.

## Open Questions

- **Question:** Which control should be used when several catalogs are available?
  **Current answer:** A compact select is robust for arbitrary catalog counts;
  short catalog chips are faster when there are only a few.
  **Recommendation:** Use an `All catalogs` select beside the page heading on
  desktop and below it on narrow screens. Hide the control when only one
  applicable catalog exists.

- **Question:** Should catalog selection survive navigation and refresh?
  **Current answer:** Losing the selection after opening a title would make the
  feature frustrating.
  **Recommendation:** Store the selection in the URL query string and preserve it
  in card links and detail-page back links. Do not use local storage as the source
  of truth.

- **Question:** Should users see unavailable catalogs?
  **Current answer:** Their items remain in the database while a volume is
  offline, and hiding them would make content appear to vanish.
  **Recommendation:** Keep offline catalogs selectable and mark them `Offline` in
  the selector without exposing filesystem paths or storage administration.

- **Question:** Should the admin Catalogs page also link into browsing?
  **Current answer:** This is useful but not required for the core filter.
  **Recommendation:** Include a `Browse media` row action in the same feature if
  it remains a small UI-only addition.

## Current Recommendation

Proceed with Approach C: make catalog filtering part of the existing Movies and
Series pages, and add a lightweight browse shortcut to the admin Catalogs page.

The filter should:

- default to `All catalogs`;
- show only `Movie` catalogs on `/movies` and `Series` plus `Anime` catalogs on
  `/series`;
- use the existing backend `catalogId` and `kind` filters instead of loading the
  entire library and filtering it in the browser;
- keep the selected catalog in the URL and preserve it through detail-page
  navigation;
- remain absent when there is only one applicable catalog.

A dedicated catalog gallery should be reconsidered only if catalogs later gain
user-facing artwork, descriptions, mixed media, or other identity beyond being a
storage destination.

## Links

- Implementation planning was completed on 2026-07-12 and removed under the
  planning completion rule.
- [Catalogs feature](../features/catalogs.md)
- [Frontend application](../features/frontend-application.md)

## Notes

This document records the exploratory UI discussion. Approach C was approved and
implemented on 2026-07-12; current behavior is documented in the linked feature
documents.
