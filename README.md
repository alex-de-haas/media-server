# Media Server

A self-hosted media server delivered as a [Hosty](../docker-host) runtime app
(`schemaVersion: app.0.1`, app id `com.haas.media-server`). It ingests torrents,
organizes/identifies/probes media automatically, and exposes a
Jellyfin-compatible streaming surface for native clients such as Infuse.

Planning docs live in [`docs/`](docs/root.md); the execution plan and milestones
are in [`docs/features/implementation-plan.md`](docs/features/implementation-plan.md).

## Repository layout

```text
manifest.json              # Hosty app manifest (app.0.1)
src/
  api/                     # .NET 10 backend (api service): Minimal API, EF Core + SQLite
  web/                     # Next.js 16 frontend + BFF (web service): App Router, Tailwind, shadcn/ui
scripts/
  dev.sh                   # one-shot dev launch via Hosty Core
  validate-manifest.sh
.github/workflows/
  ci.yml                   # build + test both services, validate manifest
  publish.yml              # build + push api/web images to GHCR (docker delivery)
docs/                      # planning documentation
```

Two services run under Hosty Core:

- **`api`** — ASP.NET Core Minimal API. Exposes the internal management surface
  (`internal` port), a public Jellyfin surface (`jellyfin` port, M2), and a raw
  `torrent` listener (M1). Validates every request's Host identity against Core.
- **`web`** — Next.js App Router app and backend-for-frontend. Holds the Hosty
  app-origin session and proxies REST + the SSE stream to `api`, so the browser stays
  same-origin and iframe-safe. Reaches `api` via the Core-injected
  `HOSTY_SERVICE_API_URL` (intra-app service discovery; `web` `dependsOn` `api`).

## Local development (dev runtime)

Requires the [Hosty CLI](../docker-host), .NET 10 SDK, Node 24, and pnpm 11.

The app runs **only** under Hosty Core — it needs Core for identity, catalog mounts,
and `web`→`api` discovery. Do not run `next dev` / `dotnet run` standalone (Core
manages both as child processes) and do not point a generic dev-server/preview tool at
the app.

Quickest, from the repository root:

```bash
scripts/dev.sh <you@example.com>   # ensures Core + the app are up, prints a fresh authenticated URL
```

The printed URL carries a single-use `?code=`; re-run for a new browser session. The
script wraps the explicit lifecycle:

```bash
# From the repository root:
hosty core start
hosty apps install . --runtime dev
hosty apps start com.haas.media-server
hosty apps open com.haas.media-server --user <you@example.com>   # launches the UI in the Shell
hosty apps logs com.haas.media-server
```

Core assigns loopback ports and injects the `HOSTY_*` environment (data dir,
core origin, service token, per-service ports, and `HOSTY_SERVICE_API_URL`); the
app never hard-codes ports, origins, or paths.

### Running the pieces directly

```bash
# api
cd src/api && dotnet test && dotnet run --project MediaServer.Api

# web
cd src/web && pnpm install && pnpm test && pnpm dev
```

## Install from a manifest URL (docker runtime)

For a non-development install, point Hosty Core straight at the published
`manifest.json` — no clone, no local checkout. Because `defaultRuntime` is
`docker`, Core pulls the prebuilt images from GHCR (`media-server-api` +
`media-server-web`, published by
[`.github/workflows/publish.yml`](.github/workflows/publish.yml)) instead of
building from source.

```bash
hosty core start
hosty apps install https://raw.githubusercontent.com/alex-de-haas/media-server/main/manifest.json
hosty apps start com.haas.media-server
hosty apps open com.haas.media-server --user <you@example.com>
```

- `install` defaults to the `docker` runtime here (`defaultRuntime: docker`); pass
  `--runtime docker` to be explicit. A URL install supports `docker` only —
  `--runtime dev` needs a local checkout, since the manifest ships no
  `source.repository` for Core to clone.
- The `main` manifest tracks the `:latest` images — swap `main` for a release tag
  (e.g. `v0.1.0`) to pin a specific build.

Before the app is functional, configure the required pieces through the Shell
(Core does not enforce them at `start`):

- **`TMDB_API_KEY`** — required app setting (TMDb API key, stored as a secret).
- **Catalog roots** — at least one host path for the required `catalogRoots`
  external mount; published media lives in canonical folders at the root and
  in-flight downloads stage under `.incoming/` (one root = one filesystem).

## Status

**M0 (Scaffold & platform integration) — complete.** Both services boot under
the Core lifecycle, the UI loads in the Shell, Host identity validates end to end
(web BFF and api both revalidate against Core), `/health` is green, and CI builds
and tests both services. Subsequent milestones (ingest, Jellyfin Direct Play,
playback state, automation polish) are tracked in the implementation plan.

## Telemetry

Both services export OpenTelemetry traces, metrics, and logs over OTLP when Hosty Core injects the
`OTEL_*` environment (operator has enabled observability **and** the collector is running; docker
runtime only). The `api` uses the OpenTelemetry .NET SDK (`src/api/MediaServer.Api/Hosty/HostyTelemetry.cs`);
the `web` BFF uses `@vercel/otel` for traces/metrics plus a `console`→OTLP logs bridge
(`src/web/src/instrumentation.ts`, `src/web/src/otel-logs.ts`). With no endpoint — the `dev` runtime,
or observability off — both emit nothing. Opt-in is the `telemetry` block in `manifest.json`. See the
platform's `docs/features/observability.md`.
