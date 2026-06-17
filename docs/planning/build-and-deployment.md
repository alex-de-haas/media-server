# Build and Deployment

Status: Draft
Created: 2026-06-15
Updated: 2026-06-15

## Description

Media Server is developed and delivered as a Hosty runtime app. `dev`
(`localCommand`) is the primary local development loop; `docker` is the v1
delivery target (`defaultRuntime: docker`), unblocked now that Hosty Core
provides external host-path mounts for catalog roots and Cloudflare-tunnel
ingress.

## Repository Layout

```text
apps/media-server/
  manifest.json            # schemaVersion app.0.1
  api/                     # .NET solution (api service)
  web/                     # Next.js app (web service)
docs/                      # this documentation
```

## Local Development (`dev` profile)

```bash
# Run from the repository root; the app directory holding manifest.json is apps/media-server.
hosty core start
hosty apps install apps/media-server --runtime dev
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

- `api` image: ASP.NET Core app exposing internal `/api` + SignalR, the public
  `jellyfin` surface, and the raw `torrent` listener, with `ffprobe` available in
  the image.
- `web` image: Next.js production server (or static export if later converted).
- `docker` is the default install profile; `dev` is used for local development.
  Catalog roots are bound through Hosty external host-path mounts (see
  [Storage and data](storage-and-data.md)). Image build/publish lands in M4 (see
  [Implementation plan](implementation-plan.md)).

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
