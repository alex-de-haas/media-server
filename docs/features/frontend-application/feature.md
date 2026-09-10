# Frontend Application

Created: 2026-06-15
Updated: 2026-09-10

## Description

The `web` service is a Next.js application providing the browser experience for
catalogs, downloads, the automation pipeline, playback, and admin settings. It runs in
the authenticated Hosty Shell as a sandboxed iframe app and acts as a
backend-for-frontend (BFF): it holds the Hosty app-origin session and proxies
REST and SignalR to the `api` service, so the browser stays same-origin and
iframe-safe.

## Pages

Navigation is a **top tab bar**: primary tabs available to all users, with
admin-only surfaces behind a right-aligned admin menu. Detail pages are push
routes, not tabs. (Decisions recorded 2026-06-18; see the M3.5 milestone in
`implementation-plan.md`.) The tab bar renders only in `standalone` launches;
under a shell it is hidden as duplicated chrome — the shell renders the manifest
`ui.navigation` pages instead (see the hosty-runtime-app feature's Launch Mode
section).

- **Home** (`/`) — overview rails built from playback state: Continue Watching
  (resume), Next Up, and Recently Added, plus an admin-only ops strip (active
  downloads, items needing review, catalog warnings).
- **Movies / Series** (`/movies`, `/series`) — poster grids with an optional
  catalog selector when more than one applicable catalog exists. Every poster
  card — here, in the Home rails, on Collections, and on a person's credits —
  names its title under the art, over one muted caption line the surface fills
  in: `Movie · 2019` in these grids and the Recently Added rail, the member
  count on Collections, the year and the role in a person's credits, nothing at
  all where the caller has nothing to add. A card without artwork says "No
  poster" in place of the image. The selection
  is stored in `?catalog=<id>`, applied by the backend, and preserved through
  detail navigation and refresh. Movies offers `Movie` catalogs; Series offers
  `Series` and `Anime` catalogs. Offline catalogs remain selectable and are
  labelled accordingly. Detail pages (`/movies/[id]`, `/series/[id]`) provide a
  backdrop hero, overview, watched/favorite toggles, and detail tabs. Movie
  detail tabs show Cast, Media (resolution/codec/audio), and Tags. Series detail
  tabs show Cast, Episodes grouped by season, and Tags; an episode whose file
  holds a consecutive range is labelled `S01E01-E02` (matching the on-disk name)
  so the season does not look like it skipped an episode — the title stays the
  first episode's, as that is all the provider has. Every episode row leads with
  the episode's still (16:9, lazy-loaded; a "No preview" placeholder when none is
  cached, never the show's backdrop), then its title, one fact line with the air
  date (the provider's calendar day, formatted in UTC so it never shifts a day
  west of Greenwich) and runtime, and its synopsis clamped to three lines — the
  clamp lifts when the row is opened. A watched episode carries the same check
  badge in the still's corner that a poster card does; the control that flips it
  is the first of the row's action icons on the right, tinted while the episode
  is watched. Below those sits a one-line summary of what
  is on disk (`HEVC 2160p · Dolby Vision 7 · 38.2 GB · 2 versions`, or "No file"),
  and the row expands onto the same media surface a movie's Media tab is —
  versions, tracks, sidecars, and for an admin every control that changes them;
  the episode's detail is fetched when the row opens. An admin also
  sees a Conversions block above the seasons listing every episode's jobs, and
  **Refresh media data** in the series menu, which fans out over its episodes.
  See [episode-media](../episode-media/feature.md). Seasons come from the detail's
  season rollup, so a season the API kept after its last episode went — one holding
  only extras — still gets a heading (reading "No episodes in this season") instead of
  vanishing from the tab. On the Episodes tab an admin can also delete a single episode
  row or a whole season from its heading; both confirm first, with a "Delete files from
  disk" checkbox that defaults to off, and both return to the library grid when the
  delete leaves the series with nothing in it.
  Detail admin controls support metadata refresh, remap where applicable,
  deletion, and **Choose poster…** — the cached posters for the title with the text
  language of each spelled out, overriding the automatic [artwork
  language](../artwork-language/feature.md) choice for a title that came out
  ambiguous. **Playback is not in-browser** — Play deep-links to an Infuse/Jellyfin
  client.
- **Downloads** (`/downloads`) — torrent list with live progress, ratio, and
  seeding status; add (with catalog + `keepSeeding`, showing each catalog's free
  space and refusing oversized `.torrent` downloads), pause, resume, stop seeding,
  delete.
- **Activity** (`/activity`) — the live automation pipeline per ingest item,
  including the review queue for low-confidence matches, source-file assignments,
  and manual match/remap override. The page shows three kinds of work — downloads
  (with the pipeline stepper), cross-catalog moves, and conversions — grouped
  under labelled headers and rendered as **one shared card**: a bordered shell
  holding a title with inline markers, a muted meta line, a right-aligned row of
  tooltip-labelled icon actions, and, below them, a progress bar with a
  monospace tabular stat line (percent, transferred/total bytes for a download,
  rates, ETA). Work that is waiting shows a
  queued line instead of a bar. The same card renders a conversion wherever it
  appears, including the Conversions block on movie detail and above a series'
  seasons.
- **Settings** (`/settings`) — General retains release-group preferences, per-user
  Infuse credentials, and watch-history controls. The admin-only **Catalogs** tab
  (`/settings?tab=catalogs`) holds catalog configuration, storage usage, scanning,
  metadata refresh, browsing, and removal. Tab selection survives refresh and browser
  history. Non-admin requests for the Catalogs tab display General.
- Movies and Series show contextual alerts for unavailable storage, with an admin
  link to catalog settings. The Home offline-catalog indicator opens that same tab.
- The standalone `/catalogs` page and its app/Hosty navigation entry are removed;
  the old URL returns 404 without redirecting.
- **Watchlist** — (future, M5) monitored titles and release calendar.

## Session and Data

- On launch the app receives a `?code`, exchanges it for an app identity token,
  stores an app-origin HttpOnly cookie when browser policy allows it, supports
  the standard Hosty Runtime App bearer-header fallback for embedded iframe
  sessions, and removes the code from the URL (see
  [Hosty runtime app](../hosty-runtime-app/feature.md)).
- The BFF resolves the Hosty identity to an internal Media Server user. Hosty
  admins receive the `admin` role; assigned non-admin Hosty users receive the
  `user` role.
- Server data loads through the BFF REST proxy to `api`.
- Real-time updates use the SignalR client (proxied through `web`).
- Client cache and mutations via TanStack React Query.

## Architecture Boundaries

- The UI consumes **only** the internal `/api` surface (camelCase, Hosty identity)
  through the BFF proxy. It must **never** couple to the Jellyfin surface.
- The Jellyfin surface is a **content-provider adapter** for external native
  players (e.g. Infuse) that sits *beside* the UI, not beneath it, and may be
  swapped for another protocol later. Both surfaces project from a shared,
  surface-neutral domain/read layer — they are siblings, not a dependency chain.
- The app **inherits the Hosty Shell theme** (light/dark) through the SDK's theme
  slice — `HostThemeBridge` from `@hosty-sdk/app/react` plus the
  `themeBootstrapScript` in the root layout's head, which read the `hosty_theme`
  launch params first and the `hosty:shell-theme` postMessage for later changes;
  it ships both token sets and does not present its own theme toggle.
- **`shadcn` is a build-time dependency, never a runtime one.** It lives in
  `devDependencies` and is used at build time twice: as the scaffolding CLI
  driven by `components.json`, and as the Tailwind layer that
  `src/app/globals.css` pulls in with `@import "shadcn/tailwind.css"` (the
  package ships it at `dist/tailwind.css`). Turbopack resolves that import
  strictly — the build fails with `Can't resolve` when the package is missing —
  so an install that skips dev dependencies cannot build the app. The Next
  standalone bundle traces only what the server actually imports, so the package
  does not reach the runtime image.

## Iframe Safety

- Use relative URLs or `HOSTY_CORE_PUBLIC_ORIGIN`; no hard-coded origins.
- Keep client routing compatible with `ui.entrypoint.path` and survive refresh
  and direct navigation.
- Do not read Host cookies, Host local storage, or the parent DOM.
- Avoid top-level redirects, frame busting, and popup auth flows.
- Treat browser storage as UI convenience only, never as auth authority.

## UI Features

- Catalog/library browser with grid and detail views.
- Torrent/download list with live progress and seeding controls.
- Intake match confirmation, pipeline activity timeline, review queue, and
  post-publish remap for source files.
- Background task notifications.
- Admin-only configuration surfaces for catalogs, providers, supported languages,
  and Jellyfin access credentials.
- Library grids request only their media kind and optional catalog from the
  backend. They currently load the complete matching top-level result; pagination
  and infinite scrolling are not implemented.

The UI does not expose a general file manager in v1. It exposes media-oriented
actions: add torrent, confirm/remap source files, stop seeding, remove downloads,
and delete library items — a whole movie or series, one season, or one episode. The UI
is English-only in v1; localization is deferred until Hosty provides app-level language
support.

## Library poster captions

Movies and Series grids hide the permanent title beneath poster artwork and show
the year followed by available video formats, such as `1997 · HDR10 · Dolby Vision`.
The title remains the link's accessible name and appears in the artwork placeholder
when no poster is available. Missing years and formats produce no extra separators.
Other poster-card surfaces keep their title captions. A series' badges are the union
of its episodes' formats — a show whose later seasons arrived in Dolby Vision says so
on its card — and a series whose episodes were never probed carries none.

## Indexing progress

Movie media cards and expanded episode media cards display [indexing progress](../indexing-progress/feature.md), including independent external audio preparation. The existing SSE bridge updates percentages without polling; reconnect refreshes detail snapshots.

## Testing Expectations

Frontend tests should cover user-visible behavior where practical. Required
coverage:

- API/BFF integration boundaries for catalogs, downloads, and pipeline views.
- Role gating for admin-only configuration.
- SignalR event handling for downloads and pipeline jobs.
- Routing for primary pages, including refresh and direct navigation.
- Error, empty, and loading states.
- Embedded routing and asset loading through the Hosty Shell iframe.
- Catalog filter routing, applicable catalog types, offline labels, and
  preservation through detail navigation.
- Activity card behavior per work type: the actions each state offers (their
  accessible names), and that a queued item shows the queued line rather than a
  progress bar.
- The download card's transferred/total readout: derivation from the bytes still
  remaining, the downloaded-total and percentage fallbacks, clamping into the
  torrent's size, and no readout while the size is unknown.
- Episode and season deletion: the actions exist only for an admin, the request
  carries `deleteFiles` only when the checkbox is ticked, a delete that prunes the
  series navigates back to the library grid, and a season with no episodes left is
  still listed and still deletable.
- Episode media: the summary line on a row and "No file" on one without a source,
  expanding a row fetching the episode's detail once and listing its versions, a
  viewer seeing the media and none of the controls, an admin opening the Convert
  and Extract dialogs from an episode's version with copy that fits either kind, the
  rename preview carrying the episode's own stem, and an episode's job listed above
  the seasons.
- Series grid captions carrying the aggregated format badges, and none for a series
  with no probed episode.

## Links

- [Catalog library browsing idea](../../ideas/catalog-library-browsing.md)
- [Watch-history calendar](../watch-history-calendar/feature.md)
- [Catalogs](../catalogs/feature.md)
