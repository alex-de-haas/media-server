# Jellyfin Compatibility

Status: Implemented
Created: 2026-06-15
Updated: 2026-06-21

## Description

Media Server exposes a Jellyfin-compatible HTTP API subset so clients such as
Infuse can browse catalogs, fetch metadata and artwork, Direct Play media, and
synchronize playback progress. This is a compatibility layer, not a full Jellyfin
server. It is served on the public `jellyfin` endpoint of the `api` service (see
[Hosty runtime app](hosty-runtime-app.md)).

Protocol references:

- Jellyfin OpenAPI: https://api.jellyfin.org/openapi/
- Codec/streaming behavior: https://jellyfin.org/docs/general/clients/codec-support/
- Infuse integration: https://support.firecore.com/hc/en-us/articles/360006462093

## Goals

- Let Infuse connect with a direct server login.
- Expose catalogs, movies, series, seasons, episodes, images, and search as
  Jellyfin-shaped DTOs.
- Prefer Direct Play and Direct Stream for Apple clients.
- Support HTTP range requests for seeking and high-bitrate playback.
- Synchronize watched state and playback position.
- Keep all file access constrained to configured catalog roots.

## Non-Goals (initial)

- Full Jellyfin administration API.
- Live TV, DVR, music, photos, books, plugins, playlists. (Movie *collections* —
  TMDb franchises as `BoxSet`s — are now supported; see
  [collections.md](collections.md).)
- DLNA.
- On-the-fly transcoding (Media Server does no conversion).

## External Access Model

Hosty does not currently provide an external ingress/gateway for native clients.
The `jellyfin` endpoint is published as a public app endpoint; the operator sets
`HOSTY_PUBLIC_ORIGIN_JELLYFIN` and fronts it with their own reverse proxy to the
Core-assigned local port. Hosty adds no auth to that endpoint — and none is
needed, because Jellyfin endpoints are protected by Media Server-owned tokens.

- v1 relies on **manual server URL entry** in Infuse.
- UDP auto-discovery on `7359/udp` does not map cleanly onto Core port
  assignment; it is optional and deferred.

## Authentication Model

Infuse cannot perform the Hosty app-code flow, so the Jellyfin surface uses
**Media Server-owned credentials**, bound to internal Media Server users that are
linked to Hosty users.

### Media Access Credential

While signed into the Media Server UI (already authenticated via Hosty), a user
creates an Infuse access credential:

```jsonc
{
  "appUserId": "{internal media server user id}",
  "hostyUserId": "{current host user sub}",
  "username": "alex@example.com",   // shown as the Hosty email for familiarity
  "pinHash": "{hashed}",            // 6–8 digit PIN, user-set or generated
  "createdAt": "...",
  "lastUsedAt": "...",
  "revoked": false
}
```

- `POST /Users/AuthenticateByName` validates `username` + PIN against the
  credential store and returns a Jellyfin-shaped `AuthenticationResult` with an
  opaque `AccessToken`.
- The PIN is used **only at login**; subsequent requests use the opaque token, so
  the PIN exposure window is a single request.
- Tokens are opaque, hashed at rest, scoped to a user and device, revocable via
  `/Sessions/Logout`, and redacted from logs.
- The server does not call Hosty Core on every Jellyfin request. Core assignment
  is checked when the credential is created, when the token is issued, and during
  token refresh or session validation. Tokens for users no longer assigned to the
  app are rejected or revoked at those validation points.

The server accepts:

- `Authorization: MediaBrowser Client="...", Device="...", DeviceId="...", Version="...", Token="..."`
- `X-Emby-Authorization` / `X-Emby-Token: <token>`
- `api_key=<token>` only for media and image URLs commonly opened without custom
  headers. Query-string tokens are restricted to compatibility endpoints, must be
  redacted in logs, and must not be accepted by internal `/api` routes.

PIN brute-force protection (short numeric secret on a public endpoint) is defined
in [Security](security.md): rate limiting, temporary lockout after 10 failed
attempts, permanent lockout after 100 (cleared by regenerating the credential).

```json
{
  "User": { "Id": "{userId}", "Name": "alex", "ServerId": "{serverId}" },
  "AccessToken": "{opaque-token}",
  "ServerId": "{serverId}",
  "SessionInfo": { "Id": "{sessionId}", "UserId": "{userId}", "Client": "Infuse", "DeviceId": "{deviceId}" }
}
```

## Required Endpoints

The first Jellyfin baseline should be the endpoint set already proven by the
previous Haas.Media implementation, adapted from the old `/jellyfin` route group
to the new public Jellyfin endpoint.

Anonymous discovery/auth:

- `POST /Users/AuthenticateByName`
- `GET /System/Info/Public`
- `GET /System/Ping`
- `GET /Users/Public`
- `GET /Branding/Configuration`

Authenticated system/user/session:

- `GET /System/Info`
- `GET /Users`
- `GET /Users/Me`
- `GET /Users/{userId}`
- `GET /Sessions`
- `POST /Sessions/Logout` (new token revocation requirement)

Library and browsing:

- `GET /Library/MediaFolders`
- `GET /Library/VirtualFolders`
- `GET /Users/{userId}/Views`
- `GET /Items`
- `GET /Items/{itemId}`
- `GET /Users/{userId}/Items`
- `GET /Users/{userId}/Items/{itemId}`
- `GET /Users/{userId}/Items/Latest`
- `GET /Users/{userId}/Items/Resume`
- `GET /Shows/{seriesId}/Seasons`
- `GET /Shows/{seriesId}/Episodes`
- `GET /Shows/NextUp`
- `GET /Users/{userId}/GroupingOptions`
- `GET|POST /DisplayPreferences/{displayPreferencesId}`

Artwork:

- `GET|HEAD /Items/{itemId}/Images/{imageType}`

Playback negotiation and streaming:

- `GET|POST /Items/{itemId}/PlaybackInfo`
- `GET|HEAD /Videos/{itemId}/stream`
- `GET|HEAD /Videos/{itemId}/stream.{container}`

Playback state:

- `POST /Sessions/Playing`
- `POST /Sessions/Playing/Progress`
- `POST /Sessions/Playing/Stopped`
- `POST|DELETE /Users/{userId}/PlayedItems/{itemId}`
- `POST|DELETE /UserPlayedItems/{itemId}` (10.9+ form; acting user from the
  optional `userId` query parameter — Infuse uses this one)
- `POST|DELETE /Users/{userId}/FavoriteItems/{itemId}`
- `POST|DELETE /UserFavoriteItems/{itemId}` (10.9+ form)

Deferred compatibility endpoints:

- `POST /Sessions/Capabilities`
- `POST /Sessions/Capabilities/Full`
- `GET /Items/Counts`
- `GET /Search/Hints`
- `GET|POST /UserItems/{itemId}/UserData`
- `POST|DELETE /UserItems/{itemId}/Rating`
- `GET /Videos/{itemId}/master.m3u8`
- `GET /Videos/{itemId}/main.m3u8`
- `GET /Videos/{itemId}/hls/{playlistId}/...`
- `GET /Videos/{itemId}/{mediaSourceId}/Subtitles/{index}/Stream.{format}`

## Media Model Mapping

- Catalog (`movie`) → `CollectionFolder` with `CollectionType = movies`.
- Catalog (`series`) → `CollectionFolder` with `CollectionType = tvshows`.
- The synthetic Collections view → `CollectionFolder` with
  `CollectionType = boxsets`; each qualifying `MovieCollection` → `BoxSet` whose
  children are its owned movies. See [collections.md](collections.md).
- Movie → `Movie`; Series → `Series`; Season → `Season`; Episode → `Episode`.
  Unmatched files are represented internally as `Video` but are **not exposed to
  Jellyfin clients** until they have a canonical identity.
- A single file holding two consecutive episodes maps to one `Episode` with
  `IndexNumber` and `IndexNumberEnd` set; playback opens the one file and watched
  state applies to the whole range.
- Public item IDs are stable across rescans and based on the catalog plus the
  canonical provider identity, not on physical path or database row id. The
  internal item id (and the `UserData` keyed to it) is preserved when an item is
  first identified, so the only time a client-visible id changes is an operator
  remap to a different title; clients re-sync user data from the server on refresh.

`BaseItemDto` should include at least `Id`, `Name`, `Type`, `ServerId`, parent
links, `ProductionYear`/`PremiereDate`/`RunTimeTicks`, `Overview`/`Genres`/
`OfficialRating`/`CommunityRating`, image tags, `UserData`
(`PlaybackPositionTicks`, `Played`, `IsFavorite`, `PlayedPercentage`), and
`MediaSources` when requested with `fields=MediaSources`.

## Item IDs and Server Version

- Client-facing item ids (`BaseItemDto.Id`) are emitted as 32-character lowercase
  hex (Jellyfin's `Guid` shape) derived deterministically from the canonical
  identity key, so they satisfy strict clients while staying stable across rescans.
- `System/Info` reports a recent **stable Jellyfin server version** that Infuse is
  known to support (Infuse 8.3 ↔ Jellyfin 10.11). Treat the reported version as a
  tested constant, bumped deliberately after verifying against Infuse — some
  clients gate features on it.

## Media Probing

The pipeline runs `ffprobe` for each playable file and persists container, size,
duration, bitrate, video codec/profile/resolution/frame rate/bit depth/HDR, audio
streams (codec, language, channels, default/forced), subtitle streams (codec,
language, text/picture, external path, default/forced), and chapters where
available. This builds accurate `MediaSourceInfo` / `MediaStream` objects.

## Playback Negotiation and Direct Streaming

`PlaybackInfo` returns a `PlaybackInfoResponse` with `MediaSources` and a
`PlaySessionId`. Default behavior:

- For a local file the user may stream, return a Direct Stream source.
- Respect `EnableDirectPlay` / `EnableDirectStream` request flags.
- Include media stream indexes for audio/subtitle selection.
- Never return raw host paths; address media by item id and HTTP URLs.
- When a title has multiple versions, return all of them in `MediaSources` and
  **honor an explicit `MediaSourceId`** on the stream request. Do not always serve
  the first/highest-resolution source — Infuse can otherwise play the wrong version.
- Return a compatible error when an item is unavailable, still in the pipeline, or
  outside policy.

The direct streaming endpoint serves the original file with `GET`/`HEAD`, `Range`
and `If-Range`, `206 Partial Content`, `Accept-Ranges`/`Content-Range`/
`Content-Length`/`ETag`/`Last-Modified`, client-disconnect cancellation, and no
whole-file buffering. Supported direct containers: `.mp4`, `.m4v`, `.mov`,
`.mkv`, `.webm`, `.avi`, `.ts`, `.m2ts`. The endpoint validates that the item
resolves to a file inside a catalog root and that the user may access the catalog.

HLS, remux, and transcoding are out of scope for Media Server's no-conversion
design and are not planned for the initial milestones.

## Subtitles

- v1 relies on **Direct Play**: embedded subtitles are played by the client
  (Infuse) directly from the container. Media Server does not extract or convert
  embedded subtitles (no FFmpeg in v1 — see [root](../root.md)).
- External sidecar `.srt` / `.vtt` files alongside the media are surfaced as
  external subtitle streams.
- Subtitle stream metadata is reported in `MediaSources[].MediaStreams` from
  `ffprobe`.

## Playback Progress and User Data

- Store progress per internal Media Server user and item.
- Mark played past a configurable threshold (e.g. 90%) or on explicit mark.
- Reset progress when marked watched; preserve progress when stopped earlier.
- Apply season/series aggregate watched state from episode state.

## Security and Abuse Controls

- Every endpoint except public system info and ping requires authentication.
- Authenticated requests validate the opaque Media Server token locally. Core is
  consulted during login/token issuance and session validation, not on every
  stream or image request.
- Stream URLs never bypass catalog authorization; access is by item id, so path
  traversal is impossible.
- Query-string tokens are redacted in logs/metrics.
- Rate-limit authentication, image, search, and streaming session creation.
- No administrator operations are exposed through this layer.

## Implementation Milestones

1. Connect, browse, Direct Play: anonymous discovery, credential auth + token
   store, users/views/items, images, `PlaybackInfo`, and range streaming. Tests
   for auth, mapping, authorization, and range handling.
2. Playback state sync: `Sessions/Playing*`, played/unplayed, favorites, resume,
   latest, and next-up. Tests for thresholds and progress persistence.
3. Additional compatibility: capabilities, item counts, search hints, ratings,
   subtitles, and multi-version playback. Tests for stream selection and subtitle
   authorization.

## Testing Expectations

Backend tests should use xUnit and Imposter (mock catalog repositories, root
resolvers, token/credential stores, ffprobe runner, authorization). Required
coverage: MediaBrowser header parsing and token validation; credential auth
success/failure/lockout/logout/revocation; DTO mapping for catalogs, movies,
series, seasons, episodes, images, media sources, streams, and user data; item
access authorization across users and catalogs; range request handling including
invalid ranges and `HEAD`; playback thresholds, resume, and watched state.
