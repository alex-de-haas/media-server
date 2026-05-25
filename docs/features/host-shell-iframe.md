# Host Shell Iframe Compatibility

## Description

Media Server's browser UI is technically feasible as a Docker Host shell app,
provided it is implemented as an embedded iframe application rather than as a
standalone same-origin web application. Docker Host authenticates access to the
shell app, proxies embedded module traffic through reserved Host routes, and
does not forward Host session cookies to the module.

The iframe sandbox intentionally does not grant same-origin privileges to module
scripts. Media Server must therefore avoid designs that depend on reading Host
cookies, accessing Host local storage, reaching into the parent DOM, or
performing top-level navigation as part of normal workflows.

## Feasibility Summary

Feasible in the Host shell iframe:

- Dashboard and status views.
- File browser UI for server-side storage roots.
- Browser file uploads through module API endpoints.
- File copy, move, delete, and rename commands.
- Torrent list, add, pause, resume, and delete commands.
- Media library browsing and metadata management.
- Background job progress display.
- Settings pages for module-owned settings.
- In-shell media previews when served through Host-routed module URLs.

Feasible with Host gateway or proxy validation:

- SignalR WebSockets, Server-Sent Events, or long polling.
- Large file uploads.
- Large file downloads.
- HTTP range requests for in-shell media previews.
- Links that open media or downloads outside the iframe.

Not an iframe concern and should use separate Host gateway exposure:

- Jellyfin-compatible access from Infuse or other native clients.
- Direct streaming endpoints intended for external media clients.
- HLS segment URLs consumed by external clients.
- Local network UDP discovery on `7359/udp`.
- Public or login-required service/API subdomains.

## Shell App Requirements

The Docker Host module metadata should include `ui` metadata for the primary
browser experience:

- `ui.entrypoint.portKey` references a public endpoint key.
- `ui.entrypoint.path` is a same-origin absolute path.
- Navigation paths are same-origin absolute paths.
- The browser UI is not modeled as an anonymous public service endpoint.

The frontend must be iframe-safe:

- Use relative URLs or a Host-provided base URL for frontend assets and API
  calls.
- Avoid hard-coded absolute origins such as `http://localhost`.
- Avoid browser code that reads Host cookies, Host local storage, or Host DOM.
- Avoid top-level redirects, frame busting, and popup-based auth flows.
- Keep client-side routing compatible with the embedded entrypoint path.
- Treat browser storage as module UI convenience only, not as authority for
  authentication or authorization.

The backend must be Host-gateway-safe:

- Validate the signed `X-Docker-Host-Identity` token before trusting Host user
  claims.
- Do not trust unsigned `X-Docker-Host-*`, `Forwarded`, `X-Forwarded-*`, or
  trusted-proxy assertion headers from clients.
- Do not require Host cookies for health, readiness, API, WebSocket, or media
  routes.
- Keep module authorization separate from Host authorization.

## Feature Assessment

File and directory management:

- Technically feasible in the iframe for listing storage roots and issuing
  server-side file operations.
- Browser uploads are feasible with file input or drag-and-drop, but the Host
  proxy must support the required request sizes and streaming behavior.
- Selecting arbitrary server host directories is not feasible from iframe
  browser APIs. External host folders should be selected through Docker Host
  module storage settings or other Host-owned administrative flows.
- Downloads can work through proxied module URLs, but large downloads and
  `Content-Disposition` behavior must be validated through the Host shell
  sandbox and gateway.

Torrent management:

- Technically feasible in the iframe for adding magnet links or `.torrent`
  files, viewing status, and sending pause, resume, stop, and delete commands.
- Real-time progress is feasible if SignalR works through the Host embed route
  with WebSocket, Server-Sent Events, or long-polling fallback.
- Torrent save paths must remain backend-selected or constrained to configured
  storage roots; the iframe cannot safely grant arbitrary host filesystem
  access.

Media libraries:

- Technically feasible in the iframe for library browsing, scan triggers,
  metadata refresh, and manual match workflows.
- Posters and backdrops should be served through module image endpoints that
  work through the Host embed route.
- Media scanning itself is backend work and is unaffected by iframe sandboxing
  as long as storage roots are mounted by Docker Host.

Background tasks and progress:

- Technically feasible in the iframe.
- SignalR transport must be validated through the Docker Host Dev Model because
  WebSocket and SSE behavior depends on Host gateway support.
- A long-polling fallback should be available for progress updates if WebSocket
  upgrade is unavailable in a specific Host deployment.

Jellyfin-compatible streaming:

- External Jellyfin-compatible clients should not be treated as iframe users.
  Infuse and similar clients need service/API gateway exposure outside the Host
  shell.
- In-shell media preview is feasible when video, image, subtitle, and range
  request endpoints work through Host-routed URLs.
- Native client direct play, HLS playlists, subtitle URLs, and query-string
  compatibility tokens must be validated through the separate Host gateway
  exposure, not only through iframe testing.
- UDP discovery is not provided by the iframe and requires explicit host
  networking configuration.

Frontend application:

- Technically feasible if the Next.js app is built for embedded routing and
  relative resource loading.
- Server components and server-side rendering must not assume a public
  standalone origin unless that origin is configured through Docker Host.
- Client navigation should stay inside the iframe for normal application pages.
- Actions that intentionally leave the shell, such as opening an external stream
  URL, should use explicit links and be validated against the iframe sandbox.

Security and configuration:

- Module-owned settings pages are feasible in the iframe.
- Docker Host install/update settings, external mounts, gateway exposure policy,
  and module assignment remain Host-owned concerns.
- Media Server should not implement iframe-only bypasses for authentication.
  The backend must use Host-issued identity or Media Server-owned tokens.

Build, packaging, and deployment:

- Technically feasible with separate web and API containers.
- Metadata must include the shell `ui` entrypoint and endpoint hints needed for
  Docker Host routing.
- Container health and readiness endpoints must work without browser cookies.
- Production-like validation must install or link the module through Docker Host,
  not only run the frontend directly in a browser.

## Validation Requirements

Validate the iframe implementation through the Docker Host Dev Model:

- Open the app from the Docker Host Apps shell.
- Confirm frontend assets load from the embedded route.
- Confirm client-side routing works after refresh and direct navigation.
- Confirm REST API calls succeed without Host cookies being visible to module
  code.
- Confirm SignalR connects and receives torrent and job progress updates.
- Confirm uploads and downloads work for expected file sizes.
- Confirm image, artwork, and in-shell media preview URLs work.
- Confirm the backend validates `X-Docker-Host-Identity` signature, issuer,
  audience, and expiration.
- Confirm spoofed client-supplied identity and forwarded headers are rejected.
- Confirm assigned-user access and Host account switching behave correctly.

Production-like validation should also install release metadata through Docker
Host to verify container networking, storage mounts, lifecycle behavior, and
gateway exposure.

## Open Questions

- Question: Should Media Server keep a separate application login inside the
  Host shell?
  Answer: The docs do not require one. Docker Host already authenticates shell
  access, and Media Server can map Host identity to module-owned roles.
  Recommendation: Use Host identity for the shell UI first. Keep Jellyfin tokens
  separate for external clients.

- Question: Should large downloads happen inside the iframe or through a
  separate gateway endpoint?
  Answer: Both are possible, but browser sandbox download behavior and Host
  proxy limits need validation.
  Recommendation: Support normal in-shell downloads for file management and use
  explicit gateway-exposed streaming endpoints for media clients.

- Question: Should WebSocket be required for SignalR?
  Answer: WebSocket is desirable, but gateway deployments may vary.
  Recommendation: Support SignalR fallback transports and validate WebSocket,
  SSE, and long polling through the Docker Host Dev Model.
