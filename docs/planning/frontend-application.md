# Frontend Application

Status: Draft
Created: 2026-06-15
Updated: 2026-06-15

## Description

The `web` service is a Next.js application providing the browser experience for
catalogs, downloads, the automation pipeline, playback, and admin settings. It runs in
the authenticated Hosty Shell as a sandboxed iframe app and acts as a
backend-for-frontend (BFF): it holds the Hosty app-origin session and proxies
REST and SignalR to the `api` service, so the browser stays same-origin and
iframe-safe.

## Pages

- **Dashboard** — status and recent activity.
- **Catalogs** — configured catalogs (with free space and offline state), scan
  triggers, browse `library/`.
- **Downloads** — torrent list with live progress, ratio, and seeding status;
  add (with catalog + `keepSeeding`, showing each catalog's free space and
  refusing oversized `.torrent` downloads), review suggested metadata/file
  mappings, pause, resume, stop seeding, delete.
- **Activity** — the live automation pipeline per ingest item, including the
  review queue for low-confidence matches, source-file assignments, and manual
  match/remap override.
- **Movies / Series** — media grids with posters and detail pages.
- **Settings** — app-owned settings, Infuse access credentials (email + PIN),
  supported languages.
- **Watchlist** — (future) monitored titles and release calendar.

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
- Optional client cache via SWR or React Query.

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
