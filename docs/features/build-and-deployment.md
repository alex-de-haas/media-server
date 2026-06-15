# Build and Deployment

## Description

Media Server is developed and delivered as a Hosty runtime app. Local development
runs through the `dev` (`localCommand`) runtime profile under Core lifecycle.
Production images are built and published to GitHub Container Registry and run
through the `docker` runtime profile once external host-path mounts for catalog
roots are available.

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
hosty core start
hosty apps install . --runtime dev
hosty apps start com.haas.media-server
hosty apps logs com.haas.media-server
```

- `api` and `web` run as local command services; Core assigns loopback ports and
  injects `HOSTY_PORT_{KEY}`, `PORT`, and `HOSTY_DEPENDENCY_API_URL`.
- Validate identity, Shell embedding, SignalR, and public endpoints through this
  Core-managed lifecycle — not by forging tokens.

## Production Images (`docker` profile)

- `api` image: ASP.NET Core app exposing internal `/api` + SignalR and the public
  `jellyfin` surface, with `ffprobe` available in the image.
- `web` image: Next.js production server (or static export if later converted).
- The `docker` profile is the manifest default, but v1 installs run under `dev`
  until catalog-root mounts are defined (see [Storage and data](storage-and-data.md)).

## GitHub Actions CI/CD

The workflow must:

- Run on pushes to the main branch and on PRs requiring validation.
- Restore and build the .NET solution; run backend unit tests (xUnit).
- Install frontend dependencies and build the Next.js app.
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
- Build `api` and `web` Docker images.
- Run through the `dev` profile under Core for Host-facing behavior.
- Install via the `docker` profile for container networking, mounts, and
  lifecycle once mounts are defined.

## Testing Expectations

Backend tests use xUnit and Imposter. CI must build both services and run the
backend test suite before publishing images.
