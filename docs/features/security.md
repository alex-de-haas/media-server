# Security

## Description

Media Server protects catalog files, torrent operations, settings, and streaming
endpoints. Configuration is provided through Hosty app settings and the runtime
environment. There are two auth domains: Hosty Core-owned identity for the UI, and
Media Server-owned credentials for Jellyfin clients.

## UI Identity (Core-owned)

- The UI uses the Hosty app-code flow and an app-origin session; the app never
  sees Host passwords or Host session cookies (see
  [Hosty runtime app](hosty-runtime-app.md)).
- `api` trusts a forwarded Host identity only after validating it against Core
  (`/api/auth/apps/revalidate`).
- Never trust unsigned `X-Docker-Host-*`, `Forwarded`, `X-Forwarded-*`, or
  trusted-proxy headers as identity.
- Authorization is per operation (catalogs, files, torrents, playback, settings).

## Jellyfin Credentials (app-owned)

Native clients authenticate with a Media Server-owned credential bound to a Host
user: `username` (the Hosty email) + a 4–8 digit PIN. The PIN is verified only at
login and yields an opaque, hashed, revocable access token (see
[Jellyfin compatibility](jellyfin-compatibility.md)).

Because a short numeric PIN sits on a public endpoint, brute-force protection is
mandatory:

- Rate-limit `/Users/AuthenticateByName` per IP and per username.
- **Temporary lockout** after 10 consecutive failed attempts (with a growing
  window).
- **Permanent lockout** after 100 cumulative failed attempts; cleared only by
  regenerating the credential.
- Recommend a minimum of 6 digits and allow longer PINs.
- Tokens, PINs, and query-string tokens are redacted from logs and metrics.

## File Safety

- All file access is sandboxed to configured catalog roots; traversal and symlink
  escapes are rejected (see [File and directory management](file-directory-management.md)).
- Jellyfin clients address media by item id, never by path, so stream URLs cannot
  bypass catalog authorization.

## Secrets

- `TMDB_API_KEY` is a required secret app setting with no default.
- Secrets are stored via Hosty settings, not baked into images, and redacted from
  logs.

## Open Question: Multi-User UI Authorization

The final mapping of Host users to in-app roles is open. The intended direction
is to bind to Hosty users (via the scoped app directory). v1 can run with a single
admin in the UI and one or more Infuse access credentials; multi-user
authorization is an additive change.

## Testing Expectations

Backend tests should use xUnit and Imposter. Required coverage:

- UI identity validation against Core; rejection of spoofed/forwarded headers.
- Jellyfin credential auth success/failure, lockout (temporary at 10, permanent
  at 100), logout, and token revocation.
- Per-operation authorization for files, torrents, media, and streaming.
- Path sandboxing and traversal rejection.
- Secret redaction and configuration binding/validation.
