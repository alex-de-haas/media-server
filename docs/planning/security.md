# Security

Status: Draft
Created: 2026-06-15
Updated: 2026-06-15

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
- Never trust client-supplied forwarding or proxy headers (for example
  `Forwarded` / `X-Forwarded-*`) or any unsigned, client-set identity header. The
  only trusted identity is the Host identity token validated against Core above.
- Authorization is role-based, not catalog-scoped. There are only two in-app
  roles: `admin` and `user`.

## In-App Users and Roles

Media Server keeps an internal user row for each Hosty user that opens the app.
Application data is linked to this internal user id, while the row stores the
current Hosty user id and email for re-linking.

- Hosty users with the `host.admin` role become Media Server `admin` users.
- Other assigned Hosty users become Media Server `user` users.
- `user` can browse all catalogs and use playback-related UI.
- `admin` can additionally manage configuration: catalogs, provider settings,
  supported languages, Jellyfin access credentials, and runtime settings.
- Catalog-level ACLs and additional custom roles are intentionally out of scope.
- If a Hosty user id changes but the email uniquely matches an existing internal
  user, the app may re-link that Hosty identity to the existing internal user.

## Jellyfin Credentials (app-owned)

Native clients authenticate with a Media Server-owned credential bound to an
internal Media Server user, which is itself linked to a Hosty user: `username`
(the Hosty email) + a 6–8 digit PIN. The PIN is verified only at login and
yields an opaque, hashed, revocable access token (see
[Jellyfin compatibility](jellyfin-compatibility.md)).

Because a short numeric PIN sits on a public endpoint, brute-force protection is
mandatory:

- Rate-limit `/Users/AuthenticateByName` per IP and per username.
- **Temporary lockout** after 10 consecutive failed attempts (with a growing
  window).
- **Permanent lockout** after 100 consecutive failed attempts; cleared only by
  regenerating the credential.
- A successful login resets the failed-attempt counter. Counting consecutive (not
  cumulative) failures prevents an attacker from slowly drip-feeding failures to
  permanently lock out a legitimate user (a denial-of-service).
- Enforce a minimum of 6 digits and allow up to 8 digits.
- Hash PINs with a slow, memory-hard algorithm (**argon2id**); never store or log
  a plaintext PIN.
- Access tokens are opaque random values with at least 128 bits of entropy, stored
  only as a hash at rest.
- Tokens, PINs, and query-string tokens are redacted from logs and metrics.
- Do not call Core on every media/image/metadata request. Core assignment is
  checked when a Jellyfin credential is created, when a Jellyfin token is issued,
  and during token refresh or session validation. Tokens for users no longer
  assigned to the app are rejected or revoked at those validation points.

## File Safety

- All file access is sandboxed to configured catalog roots; traversal and symlink
  escapes are rejected (see [File and directory management](file-directory-management.md)).
- Jellyfin clients address media by item id, never by path, so stream URLs cannot
  bypass catalog authorization.

## Secrets

- `TMDB_API_KEY` is a required secret app setting with no default.
- Secrets are stored via Hosty settings, not baked into images, and redacted from
  logs.

## Open Questions

- Should Hosty provide reusable host-level lockout or temporary PIN-user
  primitives for apps?
  Recommendation: keep Media Server's Jellyfin brute-force protection local for
  v1, then document a separate Hosty platform change if the pattern repeats.

## Testing Expectations

Backend tests should use xUnit and Imposter. Required coverage:

- UI identity validation against Core; rejection of spoofed/forwarded headers.
- Jellyfin credential auth success/failure, lockout (temporary at 10, permanent
  at 100), logout, and token revocation.
- Role authorization for configuration operations and user access to catalogs,
  media, and streaming.
- Path sandboxing and traversal rejection.
- Secret redaction and configuration binding/validation.
