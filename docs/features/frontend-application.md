# Frontend Application

## Description

The `web` service is a Next.js application providing the browser experience for
catalogs, downloads, the automation pipeline, playback, and settings. It runs in
the authenticated Hosty Shell as a sandboxed iframe app and acts as a
backend-for-frontend (BFF): it holds the Hosty app-origin session and proxies
REST and SignalR to the `api` service, so the browser stays same-origin and
iframe-safe.

## Pages

- **Dashboard** — status and recent activity.
- **Catalogs** — configured catalogs, scan triggers, browse `library/`.
- **Downloads** — torrent list with live progress, ratio, and seeding status;
  add (with catalog + `keepSeeding`), pause, resume, stop seeding, delete.
- **Activity** — the live automation pipeline per ingest item, including the
  review queue for low-confidence matches and manual match override.
- **Movies / Series** — media grids with posters and detail pages.
- **Settings** — app-owned settings, Infuse access credentials (email + PIN),
  supported languages.
- **Watchlist** — (future) monitored titles and release calendar.

## Session and Data

- On launch the app receives a `?code`, exchanges it for an app identity token,
  stores an app-origin HttpOnly cookie, and removes the code from the URL (see
  [Hosty runtime app](hosty-runtime-app.md)).
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
- Pipeline activity timeline and review queue.
- Background task notifications.

## Testing Expectations

Frontend tests should cover user-visible behavior where practical. Required
coverage:

- API/BFF integration boundaries for catalogs, downloads, and pipeline views.
- SignalR event handling for downloads and pipeline jobs.
- Routing for primary pages, including refresh and direct navigation.
- Error, empty, and loading states.
- Embedded routing and asset loading through the Hosty Shell iframe.
