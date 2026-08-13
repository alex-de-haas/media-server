# Build and Deployment

Status: Implemented
Created: 2026-06-15
Updated: 2026-08-13

## Description

Media Server is developed and delivered as a Hosty runtime app. `dev`
(`localCommand`) is the primary local development loop; `docker` is the v1
delivery target (`defaultRuntime: docker`), unblocked now that Hosty Core
provides external host-path mounts for catalog roots and Cloudflare-tunnel
ingress.

## Repository Layout

```text
manifest.json              # schemaVersion app.0.1 (repo root)
src/
  api/                     # .NET solution (api service)
  web/                     # Next.js app (web service)
docs/                      # this documentation
```

## Local Development (`dev` profile)

```bash
# Run from the repository root; manifest.json lives at the repo root.
hosty core start
hosty apps install . --runtime dev
hosty apps start com.haas.media-server
hosty apps open com.haas.media-server --user <you@example.com>
hosty apps logs com.haas.media-server
```

- `api` and `web` run as local command services; Core assigns loopback ports and
  injects `HOSTY_PORT_{KEY}`, `PORT`, and (because `web` `dependsOn` `api`)
  `HOSTY_SERVICE_API_URL` for the `web` → `api` BFF hop.
- `ffprobe` must be available on the host; its path is provided via the
  `FFPROBE_PATH` app setting at install time.
- Validate identity, Shell embedding, SignalR, and public endpoints through this
  Core-managed lifecycle — not by forging tokens.

## Production Images (`docker` profile, v1 delivery target)

- `api` image: ASP.NET Core app exposing internal `/api` + the realtime (SSE)
  stream and the public `jellyfin` surface, with `ffprobe` available in the image.
  Downloading is delegated to the external `torrent-engine` app (a required cross-app
  dependency), so this image binds **no** raw torrent listener.
- `web` image: Next.js production server (or static export if later converted),
  built on the `node:<major>-bookworm-slim` base. The Node major tracks the active
  LTS line and matches `node-version` in `ci.yml`, so the image runs what CI builds
  and tests against; both move together in one change.
- `docker` is the default install profile; `dev` is used for local development.
  Catalog roots are bound through Hosty external host-path mounts (see
  [Storage and data](storage-and-data/feature.md)). Image build/publish lands in M4 (see
  [Implementation plan](implementation-plan.md)).

### Base images

Both images pin their base by digest, the same discipline `publish.yml` applies to
GitHub Actions: an unpinned tag lets two builds of one commit ship different
userland. The re-pin command is recorded in each Dockerfile.

### Container users

The two services differ, and the difference is a property of what they touch:

- **`web` runs unprivileged** as the base image's `node` user. It serves the Next
  standalone bundle and mounts nothing — `catalogRoots` is declared on `api`, and
  the app data target for `web` exists only in the `dev` runtime.
- **`api` runs as root.** It is not merely a reader of `catalogRoots`: the
  organizer creates canonical directories, ingest recursively deletes
  `.incoming/<downloadId>`, and the mux and Jellyfin image services move and
  delete files in place. Those roots are operator-owned host paths — any number of
  them, each with its own owner, and on an existing installation created
  root-owned by this image. The container cannot take ownership of them (they are
  the user's media library, not app state), and there is no single uid to adopt
  when several roots disagree.

Dropping privileges in `api` therefore needs a uid/gid or supplementary-group
contract from Hosty Core, which does not exist today — Core sets no `--user` and
injects no uid. Recorded as a platform request (see
[Hosty platform requests](hosty-platform-requests/feature.md), item 16). Until it lands, a
non-root `api` would fail to organize, ingest or mux on any catalog root it does
not happen to own.

## GitHub Actions CI/CD

The v1 workflow must:

- Run on pushes to the main branch and on PRs requiring validation.
- Restore and build the .NET solution; run backend unit tests (xUnit).
- Install frontend dependencies and build the Next.js app.
- Validate the Hosty manifest and `dev` runtime commands.

Image build and GHCR publish land with M4 (Docker delivery); the workflow then also:

- Build `api` and `web` Docker images.
- Publish to GHCR, tagged with at least the commit SHA and optionally `latest`.
- Use `GITHUB_TOKEN` with `packages: write`.

Example image names:

- `ghcr.io/<owner>/media-server-api:<sha>`
- `ghcr.io/<owner>/media-server-web:<sha>`

### Dependency updates

Dependabot covers every ecosystem in the repository (`.github/dependabot.yml`):
minor and patch bumps arrive grouped per ecosystem, majors individually. Hosty
App SDK bumps form their own group and auto-merge once the required checks pass
(`.github/workflows/dependabot-auto-merge.yml`).

`Microsoft.OpenApi` is held on the 2.x line, and the bound applies to hand-written
bumps as much as to Dependabot's. Upstream aligns its major versions with
ASP.NET Core: 2.x is the line for AspNetCore OpenAPI 10 and is supported until
November 2028, while 3.x lists no released AspNetCore OpenAPI as supported. Under
3.x the API does not merely go untested — it does not compile: the
`Microsoft.AspNetCore.OpenApi` 10 source generator emits an assignment to
`IOpenApiMediaType.Example`, which 3.x made read-only, so the generated
`OpenApiXmlCommentSupport.generated.cs` fails with `CS0200`. The bound lifts when
a released `Microsoft.AspNetCore.OpenApi` adopts 3.x.

## Manifest Update Discipline

Keep stable across releases: app id, service keys (`api`, `web`), endpoint keys
(`ui`, `jellyfin`, `internal` port), setting keys, and app data semantics.
Before publishing an update, review changes to images/tags, manifest version,
ports/endpoints, settings, app data layout, UI navigation, and dependencies.

## Validation

- Restore/build the .NET solution; run backend unit tests.
- Build the Next.js app.
- Run through the `dev` profile under Core for Host-facing behavior.
- Build `api` and `web` Docker images (M4).
- Install via the `docker` profile for container networking, external host-path
  mounts, and lifecycle.

## Testing Expectations

Backend tests use xUnit and Imposter. CI must build both services and run the
backend test suite. Image build and GHCR publish land with M4 (Docker delivery).
