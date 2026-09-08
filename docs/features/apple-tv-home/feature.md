# Apple TV Home

Created: 2026-09-08
Updated: 2026-09-08

## Navigation and rails

Home is the first tvOS tab, followed by Movies, Series, Collections, and Settings.
Movies and Series are complete browsing grids. Home owns three independent rows:

- Continue Watching reads in-progress movies and episodes in server recency order.
- Next Up reads the next unwatched episode of each started series.
- Recommended for You reads the existing native recommendation feed and shows only
  held titles with a local media item ID, preserving rank and showing up to 20.
  Unavailable discovery titles are omitted. A mixed feed can yield fewer than 20
  held cards because filtering follows the server's bounded 60-item response.

Each row publishes immediately when its request finishes, with loading, empty,
error/retry, and older-server (404) states. Refreshing keeps existing cards visible.
Home refreshes on appearance and foreground activation. Concurrent refreshes of
the same row are ignored. Cancellation resets its state so a later visit can retry.

Cards use authenticated local artwork, a readable missing-artwork title, accessible
names, native directional focus and the existing detail navigation. Episode cards
retain their episode identity and label but open the owning series detail screen;
this feature does not add direct episode playback or redesign series detail.
Recommendations open the local movie/series detail and show the available reason.
Home cards retain visible names alongside resume/episode/recommendation captions.

## Native API

`GET /native/v1/home/resume` and `GET /native/v1/home/nextup` require an authenticated,
provisioned app user and return `LibraryRailItemDto` arrays. Optional `limit` defaults
to 20 and clamps to 1–60. These are wrappers over the same LibraryReadService methods
used by Web; user identities are resolved from the principal, never query parameters.
The OpenAPI document and generated Swift client include both operations.

The existing `/native/v1/recommendations` contract and generation remain unchanged.
The server release is 0.73.0; the independently versioned tvOS client is 0.11.0.

## Related features

- [Apple client visual design](../apple-client-visual-design/feature.md)
- [Recommendation providers](../recommendation-providers/feature.md)
- [Apple client](../apple-client/feature.md)

## Testing Expectations

- Native route authentication, bounded results, and caller isolation; existing library
  service coverage for resume navigation and next unwatched episode selection.
- MediaKit coverage for independent retry, episode navigation, filtering unavailable
  recommendations, empty responses, older servers, and revalidation on return.
- Build the tvOS simulator target and verify Home → each row → detail → Home, plus
  the separate Movies grid, using the local `--cinema-preview` fixture.
- Regenerate the Swift contract after native API changes and check the docs index.
