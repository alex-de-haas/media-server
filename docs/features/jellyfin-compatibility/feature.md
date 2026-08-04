# Jellyfin Compatibility

Created: 2026-06-15
Updated: 2026-08-02

## Description

Media Server exposes a Jellyfin-compatible HTTP API subset so clients such as
Infuse can browse catalogs, fetch metadata and artwork, Direct Play media, and
synchronize playback progress. This is a compatibility layer, not a full Jellyfin
server. It is served on the public `jellyfin` endpoint of the `api` service (see
[Hosty runtime app](../hosty-runtime-app/feature.md)).

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

This is no longer the only surface native clients can use: `/native/v1` exists
beside it for Media Server's own clients, carrying what a Jellyfin DTO cannot. See
[native-client-api](../native-client-api/feature.md). Nothing here is deprecated by
it — Infuse and any other third-party client keep this surface unchanged.

## Non-Goals

- Full Jellyfin administration API.
- Live TV, DVR, music, photos, books, plugins, playlists. (Movie *collections* —
  TMDb franchises as `BoxSet`s — are supported; see
  [collections.md](../collections.md).)
- DLNA.
- On-the-fly transcoding: this surface serves original files only. Offline
  conversion is a separate feature and never happens in a playback request.

## External Access Model

Hosty does not currently provide an external ingress/gateway for native clients.
The `jellyfin` endpoint is published as a public app endpoint; the operator sets
`HOSTY_PUBLIC_ORIGIN_JELLYFIN` and fronts it with their own reverse proxy to the
Core-assigned local port. Hosty adds no auth to that endpoint — and none is
needed, because Jellyfin endpoints are protected by Media Server-owned tokens.

- The operator enters the server URL in Infuse **by hand**.
- UDP auto-discovery on `7359/udp` is not implemented: it does not map cleanly
  onto Core port assignment.

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
in [Security](../security.md): rate limiting, temporary lockout after 10 failed
attempts, permanent lockout after 100 (cleared by regenerating the credential).

```json
{
  "User": { "Id": "{userId}", "Name": "alex", "ServerId": "{serverId}" },
  "AccessToken": "{opaque-token}",
  "ServerId": "{serverId}",
  "SessionInfo": { "Id": "{sessionId}", "UserId": "{userId}", "Client": "Infuse", "DeviceId": "{deviceId}" }
}
```

## Endpoints

Everything below is served on the public `jellyfin` endpoint, without a route
prefix. (The credential management API a signed-in user drives from the web UI is
*not* part of this surface: it lives on the internal API under `/api/jellyfin`.)

Several routes exist in two forms — a `/Users/{userId}/…` path form and a newer
form taking `userId` as a query parameter. Both are served, because Jellyfin
clients disagree about which to use and Infuse picks the newer one.

`GET /Items` supports the `PersonIds` filter, which narrows the result to the
titles those people are credited on. An id that resolves to nobody narrows it to
nothing rather than being ignored.

Anonymous discovery/auth:

- `POST /Users/AuthenticateByName`
- `GET /System/Info/Public`
- `GET|POST /System/Ping`
- `GET /Users/Public`
- `GET /Branding/Configuration`

Authenticated system/user/session:

- `GET /System/Info`
- `GET /Users`
- `GET /Users/Me`
- `GET /Users/{userId}`
- `GET /Sessions`
- `POST /Sessions/Logout` (revokes the token)
- `POST /Sessions/Capabilities`, `POST /Sessions/Capabilities/Full` (accepted and
  discarded — this server drives no client)

Library and browsing:

- `GET /Library/MediaFolders`
- `GET /Library/VirtualFolders`
- `GET /UserViews` — the one Infuse calls; `GET /Users/{userId}/Views` is the
  legacy alias
- `GET /UserViews/GroupingOptions`, `GET /Users/{userId}/GroupingOptions`
- `GET /Items`
- `GET /Items/{itemId}`
- `GET /Users/{userId}/Items`
- `GET /Users/{userId}/Items/{itemId}`
- `GET /Items/Latest`, `GET /Users/{userId}/Items/Latest`
- `GET /UserItems/Resume`, `GET /Users/{userId}/Items/Resume`
- `GET /Shows/{seriesId}/Seasons`
- `GET /Shows/{seriesId}/Episodes`
- `GET /Shows/NextUp`
- `GET /Items/{itemId}/LocalTrailers`
- `GET /Items/{itemId}/SpecialFeatures`,
  `GET /Users/{userId}/Items/{itemId}/SpecialFeatures`
- `GET /Persons` — the people credited on something in the library, honoring
  `SearchTerm`, `StartIndex` and `Limit`. It answers with an empty result rather
  than 404 even when it matches nothing: Infuse's search fans out here first and
  treats a 404 as a hard failure, so a title that *is* in the library would
  never surface.
- `GET /MediaSegments/{itemId}`
- `GET|POST /DisplayPreferences/{displayPreferencesId}`

Artwork:

- `GET|HEAD /Items/{itemId}/Images/{imageType}`
- `GET|HEAD /Items/{itemId}/Images/{imageType}/{imageIndex}`

The same routes serve a person's profile photo, addressed by the person id.
Person and collection artwork are remote provider URLs with no `ImageAsset` row
behind them, so they cache under deterministic file names that the periodic
cache sweep recomputes; a person has one image, and a request for anything but
`Primary` is answered with nothing rather than with the portrait in the wrong
slot.

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

### Not implemented

- `GET /Items/Counts`, `GET /Search/Hints`, `GET|POST /UserItems/{itemId}/UserData`,
  `POST|DELETE /UserItems/{itemId}/Rating`.
- HLS (`/Videos/{itemId}/master.m3u8`, `main.m3u8`, `/hls/{playlistId}/…`) —
  excluded by the no-conversion design above, not merely unbuilt.
- `GET /Videos/{itemId}/{mediaSourceId}/Subtitles/{index}/Stream.{format}` —
  see [external subtitle delivery](../external-subtitle-delivery/plan.md).
- Jellyfin's own recommendation endpoints (`/Movies/Recommendations`,
  `/Items/{itemId}/Similar` and its per-type variants, `/Items/Suggestions`).
  Infuse never requests them: six weeks of this app's own request log contain
  zero calls to any of them.

## Media Model Mapping

- Catalog (`movie`) → `CollectionFolder` with `CollectionType = movies`.
- Catalog (`series`) → `CollectionFolder` with `CollectionType = tvshows`.
- The synthetic Collections view → `CollectionFolder` with
  `CollectionType = boxsets`; each qualifying `MovieCollection` → `BoxSet` whose
  children are its owned movies. See [collections.md](../collections.md).
- The synthetic Recommended view → `CollectionFolder` with a **null**
  `CollectionType` (mixed content), holding the part of the recommendation feed
  this instance actually has. It is personal, so unlike the catalog views it is
  listed only for a user whose shelf is non-empty, and it is the one view whose
  `Items/Latest` returns a ranked selection rather than recently added titles.
  See [recommendation providers](../recommendation-providers/feature.md).
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

- A `Person` (cast or crew member) → a `Person` item with `LocationType`
  `Virtual`. Its client-facing id is derived from the provider identity
  (`tmdb` + the provider's person id), not from the database row, so it survives
  a rescan like item ids do. A person is not part of any catalog and appears
  only through `/Persons`, a direct id lookup, or an item's `People`.

`BaseItemDto` carries `Id`, `Name`, `Type`, `ServerId`, parent
links, `ProductionYear`/`PremiereDate`/`RunTimeTicks`, `Overview`/`Genres`/
`OfficialRating`/`CommunityRating`, image tags, `UserData`
(`PlaybackPositionTicks`, `Played`, `IsFavorite`, `PlayedPercentage`), and
`MediaSources` when requested with `fields=MediaSources`.

## People

An item's credits reach the client as `BaseItemDto.People`, each entry carrying
the person id, name, the Jellyfin person kind and the free-text role under it.

- The field is emitted on the **item detail** responses only (`GET /Items/{id}`
  and `GET /Users/{userId}/Items/{itemId}`). A list response would need a credit
  query per row, and no observed client asks for `Fields=People` there.
- Cast comes first in the provider's billing order with the portrayed character
  as `Role`, then crew with the director first.
- Only directing, writing and producing credits are emitted; the animators,
  lighting artists and stunt performers that dominate a TMDb crew list are
  dropped. `Role` keeps the provider's own job string, so a client shows
  "Screenplay" rather than the kind it was mapped to.
- Cast is capped at 30 entries and crew at 10. A person is listed once per kind,
  so someone who both acted in and directed a title appears under each.
- An item whose credits were never fetched carries no `People` field at all.

The credits themselves are produced by the metadata pipeline (`PersonSyncService`
and `PersonBackfillService` parse them out of the cached provider payload); this
surface only projects what is already stored, and a title with no stored credits
shows none.

## Item IDs and Server Version

- Client-facing item ids (`BaseItemDto.Id`) are emitted as 32-character lowercase
  hex (Jellyfin's `Guid` shape) derived deterministically from the canonical
  identity key, so they satisfy strict clients while staying stable across rescans.
- `System/Info` reports a recent **stable Jellyfin server version** that Infuse is
  known to support (Infuse 8.3 ↔ Jellyfin 10.11). Treat the reported version as a
  tested constant, bumped deliberately after verifying against Infuse — some
  clients gate features on it.

## Media Probing

The pipeline probes each playable file and persists container, size, duration,
bitrate, video codec/profile/resolution/frame rate/bit depth/HDR, audio streams
(codec, language, channels, default/forced), subtitle streams (codec, language,
text/picture, external path, default/forced), and chapters where available. This
builds the `MediaSourceInfo` / `MediaStream` objects this surface returns.

This app no longer runs `ffprobe` itself — probing goes through the providers
described in [media probe providers](../media-probe-providers/feature.md), and
what a probe can answer therefore depends on which one served it. A file probed
by the container-header reader alone reports less than one the external engine
saw, so a `MediaStream` list may be thinner than a full `ffprobe` would produce.

## Playback Negotiation and Direct Streaming

`PlaybackInfo` returns a `PlaybackInfoResponse` with `MediaSources` and a
`PlaySessionId`:

- A local file the user may stream is offered as a Direct Stream source.
- The `EnableDirectPlay` / `EnableDirectStream` request flags are parsed into the
  request DTO and then **ignored**: the same sources come back either way. A
  client that turns both off would, on a real Jellyfin server, be offered a
  transcode — which this surface does not do, so there is nothing else to return.
- Media stream indexes are included for audio/subtitle selection.
- Raw host paths are never returned; media is addressed by item id and HTTP URLs.
- When a title has multiple versions, all of them appear in `MediaSources` and an
  explicit `MediaSourceId` on the stream request is **honored**. The first or
  highest-resolution source is not served unconditionally — Infuse would otherwise
  play the wrong version.
- An item that is unavailable, still in the pipeline, or outside policy yields a
  compatible error.

The direct streaming endpoint serves the original file with `GET`/`HEAD`, `Range`
and `If-Range`, `206 Partial Content`, `Accept-Ranges`/`Content-Range`/
`Content-Length`/`ETag`/`Last-Modified`, client-disconnect cancellation, and no
whole-file buffering. Supported direct containers: `.mp4`, `.m4v`, `.mov`,
`.mkv`, `.webm`, `.avi`, `.ts`, `.m2ts`. The endpoint validates that the item
resolves to a file inside a catalog root and that the user may access the catalog.

HLS, remux, and transcoding are out of scope for this surface's no-conversion
design.

## Subtitles

- Embedded subtitles reach the viewer by **Direct Play**: the client (Infuse)
  reads them from the container. This surface neither extracts nor converts them
  — the `api` image ships without ffmpeg.
- External sidecar `.srt` / `.vtt` files alongside the media are surfaced as
  external subtitle streams, but no delivery URL is emitted yet, so a client is
  told the stream exists and given no way to fetch it. Closing that gap is
  [external subtitle delivery](../external-subtitle-delivery/plan.md).
- Subtitle stream metadata is reported in `MediaSources[].MediaStreams` from
  whatever the probe providers could determine.

## Playback Progress and User Data

- Progress is stored per internal Media Server user and item.
- An item is marked played past a fixed 90% threshold
  (`UserDataService.WatchedThreshold`) or on an explicit mark; below 5% no resume
  point is kept.
- Marking watched resets progress; stopping earlier preserves it.
- Season and series watched state is aggregated from episode state.

## Security and Abuse Controls

- Every endpoint except public system info and ping requires authentication.
- Authenticated requests validate the opaque Media Server token locally. Core is
  consulted during login/token issuance and session validation, not on every
  stream or image request.
- Stream URLs never bypass catalog authorization; access is by item id, so path
  traversal is impossible.
- Query-string tokens are redacted in logs/metrics.
- **Only `POST /Users/AuthenticateByName` is rate-limited** (`jellyfin-auth`: a
  fixed window of 10 requests per 30 seconds, partitioned by source IP; the
  per-username dimension is covered by credential lockout instead). Image, search
  and `PlaybackInfo` requests carry no rate-limit policy — an authenticated token
  is the only thing standing between a caller and those endpoints.
- No administrator operations are exposed through this layer.

## Testing Expectations

Backend tests use xUnit and Imposter (mock catalog repositories, root resolvers,
token/credential stores, probe providers, authorization). Required
coverage: MediaBrowser header parsing and token validation; credential auth
success/failure/lockout/logout/revocation; DTO mapping for catalogs, movies,
series, seasons, episodes, images, media sources, streams, and user data; item
access authorization across users and catalogs; range request handling including
invalid ranges and `HEAD`; playback thresholds, resume, and watched state; the
people projection — person id derivation, cast/crew ordering, the dropped crew
jobs, the per-kind caps, people staying off list responses, person id lookup and
photo serving, `/Persons` search and paging, the `PersonIds` filter, and the
cache sweep keeping live person photos while reclaiming superseded ones.
