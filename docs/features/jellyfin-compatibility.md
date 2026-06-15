# Jellyfin Compatibility

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
- Live TV, DVR, music, photos, books, plugins, collections, playlists.
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
**Media Server-owned credentials**, bound to Host users.

### Media Access Credential

While signed into the Media Server UI (already authenticated via Hosty), a user
creates an Infuse access credential:

```jsonc
{
  "hostyUserId": "{host user sub}",
  "username": "alex@example.com",   // shown as the Hosty email for familiarity
  "pinHash": "{hashed}",            // 4–8 digit PIN, user-set or generated
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

Discovery/health: `GET /System/Ping`, `GET /System/Info/Public`, `GET /System/Info`.

Auth/sessions: `POST /Users/AuthenticateByName`, `GET /Users/Me`,
`POST /Sessions/Logout`, `POST /Sessions/Capabilities`,
`POST /Sessions/Capabilities/Full`.

Browsing: `GET /UserViews`, `GET /Items`, `GET /Items/{itemId}`,
`GET /Items/Latest`, `GET /Items/Counts`, `GET /Search/Hints`.

Artwork: `GET|HEAD /Items/{itemId}/Images/{imageType}`,
`GET /Items/{itemId}/Images/{imageType}/{imageIndex}`.

Playback negotiation and streaming: `GET|POST /Items/{itemId}/PlaybackInfo`,
`GET|HEAD /Videos/{itemId}/stream`, `GET|HEAD /Videos/{itemId}/stream.{container}`,
`GET /Videos/{itemId}/master.m3u8`, `GET /Videos/{itemId}/main.m3u8`,
`GET /Videos/{itemId}/hls/{playlistId}/...`,
`GET /Videos/{itemId}/{mediaSourceId}/Subtitles/{index}/Stream.{format}`.

Playback state: `POST /Sessions/Playing`, `POST /Sessions/Playing/Progress`,
`POST /Sessions/Playing/Stopped`, `GET|POST /UserItems/{itemId}/UserData`,
`POST|DELETE /UserPlayedItems/{itemId}`, `POST|DELETE /UserFavoriteItems/{itemId}`,
`POST|DELETE /UserItems/{itemId}/Rating`.

## Media Model Mapping

- Catalog (`movie`) → `CollectionFolder` with `CollectionType = movies`.
- Catalog (`series`) → `CollectionFolder` with `CollectionType = tvshows`.
- Movie → `Movie`; Series → `Series`; Season → `Season`; Episode → `Episode`;
  unmatched file → `Video`.
- Public item IDs are stable across rescans, independent of physical path,
  provider id, and database row id.

`BaseItemDto` should include at least `Id`, `Name`, `Type`, `ServerId`, parent
links, `ProductionYear`/`PremiereDate`/`RunTimeTicks`, `Overview`/`Genres`/
`OfficialRating`/`CommunityRating`, image tags, `UserData`
(`PlaybackPositionTicks`, `Played`, `IsFavorite`, `PlayedPercentage`), and
`MediaSources` when requested with `fields=MediaSources`.

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

- External `.srt` / `.vtt` files, and embedded text subtitles converted to WebVTT
  on demand.
- Subtitle stream metadata in `MediaSources[].MediaStreams`.

## Playback Progress and User Data

- Store progress per Host user and item.
- Mark played past a configurable threshold (e.g. 90%) or on explicit mark.
- Reset progress when marked watched; preserve progress when stopped earlier.
- Apply season/series aggregate watched state from episode state.

## Security and Abuse Controls

- Every endpoint except public system info and ping requires authentication.
- Stream URLs never bypass catalog authorization; access is by item id, so path
  traversal is impossible.
- Query-string tokens are redacted in logs/metrics.
- Rate-limit authentication, image, search, and streaming session creation.
- No administrator operations are exposed through this layer.

## Implementation Milestones

1. Connect, browse, Direct Play: system endpoints, credential auth + token store,
   `/UserViews` `/Items` images search, `PlaybackInfo` with direct sources, range
   streaming. Tests for auth, mapping, authorization, range handling.
2. Playback state sync: `Sessions/Playing*`, user data, resume, favorites,
   ratings. Tests for thresholds and progress persistence.
3. Subtitles and multi-version playback: external/embedded text subtitles,
   multiple media sources. Tests for stream selection and subtitle authorization.

## Testing Expectations

Backend tests should use xUnit and Imposter (mock catalog repositories, root
resolvers, token/credential stores, ffprobe runner, authorization). Required
coverage: MediaBrowser header parsing and token validation; credential auth
success/failure/lockout/logout/revocation; DTO mapping for catalogs, movies,
series, seasons, episodes, images, media sources, streams, and user data; item
access authorization across users and catalogs; range request handling including
invalid ranges and `HEAD`; playback thresholds, resume, and watched state.
