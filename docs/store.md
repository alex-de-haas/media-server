![Media Server](../assets/icon.svg)

# Media Server

A **self-hosted media server** delivered as a Hosty runtime app. It ingests torrents,
organizes/identifies/probes media automatically, and exposes a **Jellyfin-compatible**
streaming surface for native clients such as Infuse.

## What it does

- **Torrent ingest** — delegates all downloading to the **Torrent Engine** app over a
  required cross-app dependency, so downloading runs VPN-isolated in its own container.
- **Automatic organize / identify / probe** — new media is matched against TMDb,
  organized into your catalog, and probed with ffprobe for stream metadata.
- **Jellyfin surface** — advertises a Jellyfin-compatible server so native clients
  (Infuse and friends) can browse and stream, with optional local-network auto-discovery.
- **Optional transcoding** — an optional dependency on the **Transcode Engine** app adds
  hardware-accelerated re-encoding on demand.
- **Catalog mounts** — one or more labelled host paths hold your library; the app reads
  and writes there directly.

## Two services

- **`api`** — an ASP.NET Core Minimal API (EF Core + SQLite). Exposes the internal
  management surface, the public Jellyfin surface, and validates every request's Host
  identity against Hosty Core.
- **`web`** — a Next.js App Router app and backend-for-frontend. Holds the app-origin
  session and proxies REST + the SSE stream to `api`, keeping the browser same-origin and
  iframe-safe. Reaches `api` through Core-injected intra-app service discovery.

## Configuration

A **TMDb API key** is required for metadata. Supported metadata languages, the Jellyfin
server name and discovery toggle, an optional ffprobe path override, and global
download/upload rate limits (forwarded to the torrent engine) round out the settings.

## Using it

Install from the marketplace. Install the required **Torrent Engine** dependency (and,
optionally, **Transcode Engine**), point a catalog mount at your library, set your TMDb
key, and open the app from the sidebar.
