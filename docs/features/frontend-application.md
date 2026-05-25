# Frontend Application

## Description

The frontend is a Next.js application that provides the primary browser
experience for files, torrents, media libraries, playback, and settings. It is
designed to run as the web container in the Docker Host module and to appear in
the authenticated Docker Host Apps shell.

## Pages and Sections

The application should include:

- Dashboard.
- File Browser.
- Torrents.
- Media Libraries.
- Movies.
- TV Series.
- Settings.

## State Management

Frontend state should use:

- Server data loaded through REST endpoints.
- Real-time updates through SignalR.
- Optional client cache through React Query or SWR.

## UI Features

Expected UI features:

- File explorer with tree and list views.
- Torrent list with live progress bars.
- Media grids with posters.
- Media detail pages.
- Background task notifications.

## Host Shell Iframe Compatibility

The frontend is technically feasible inside the Docker Host shell iframe if it
is implemented as an embedded application:

- Use relative URLs or a Host-provided base URL for assets and API calls.
- Keep client-side routing compatible with the module `ui.entrypoint.path`.
- Avoid reading Host cookies, Host local storage, or the Host parent DOM.
- Avoid top-level redirects and popup-based authentication flows.
- Validate REST, SignalR, uploads, downloads, and media previews through the
  Docker Host Dev Model.

Detailed compatibility requirements are documented in
[Host shell iframe compatibility](host-shell-iframe.md).

## Testing Expectations

Frontend tests should cover user-visible behavior where practical.

Required coverage:

- API integration boundaries for file, torrent, and media library views.
- SignalR event handling for torrent and background job updates.
- Routing for primary pages.
- Error, empty, and loading states.
- Embedded routing and asset loading through the Docker Host shell iframe.
