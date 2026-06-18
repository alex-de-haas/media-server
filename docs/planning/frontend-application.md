# Frontend Application

Status: Draft
Created: 2026-06-15
Updated: 2026-06-18

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
`implementation-plan.md`.)

- **Home** (`/`) — overview rails built from playback state: Continue Watching
  (resume), Next Up, and Recently Added, plus an admin-only ops strip (active
  downloads, items needing review, catalog warnings).
- **Movies / Series** (`/movies`, `/series`) — poster grids (infinite scroll) with
  detail pages (`/movies/[id]`, `/series/[id]`): backdrop hero, overview, media
  info (resolution/codec/audio), watched/favorite toggles, season/episode listing
  with resume, source-file remap, and delete. **Playback is not in-browser** — Play
  deep-links to an Infuse/Jellyfin client.
- **Downloads** (`/downloads`) — torrent list with live progress, ratio, and
  seeding status; add (with catalog + `keepSeeding`, showing each catalog's free
  space and refusing oversized `.torrent` downloads), pause, resume, stop seeding,
  delete.
- **Activity** (`/activity`) — the live automation pipeline per ingest item,
  including the review queue for low-confidence matches, source-file assignments,
  and manual match/remap override.
- **Catalogs** (`/catalogs`, admin) — configured catalogs (with free space and
  offline state), scan triggers, browse `library/`.
- **Settings** (`/settings`) — admin app-owned settings (TMDb key, supported
  languages, server name, torrent limits) and, **per signed-in user**, Infuse
  access credentials (username + PIN).
- **Watchlist** — (future, M5) monitored titles and release calendar.

## Session and Data

- On launch the app receives a `?code`, exchanges it for an app identity token,
  stores an app-origin HttpOnly cookie when browser policy allows it, supports
  the standard Hosty Runtime App bearer-header fallback for embedded iframe
  sessions, and removes the code from the URL (see
  [Hosty runtime app](hosty-runtime-app.md)).
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
- The app **inherits the Hosty Shell theme** (light/dark) via the Shell theme
  bridge (initial `hosty_theme` URL params + `hosty:shell-theme` postMessage); it
  ships both token sets and does not present its own theme toggle.

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
- Library grids use infinite-scroll (scroll-based) pagination so large catalogs
  stay responsive.

The UI does not expose a general file manager in v1. It exposes media-oriented
actions: add torrent, confirm/remap source files, stop seeding, remove downloads,
and delete library items. The UI is English-only in v1; localization is deferred
until Hosty provides app-level language support.

## Testing Expectations

Frontend tests should cover user-visible behavior where practical. Required
coverage:

- API/BFF integration boundaries for catalogs, downloads, and pipeline views.
- Role gating for admin-only configuration.
- SignalR event handling for downloads and pipeline jobs.
- Routing for primary pages, including refresh and direct navigation.
- Error, empty, and loading states.
- Embedded routing and asset loading through the Hosty Shell iframe.
