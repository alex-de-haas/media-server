# Idea: Standalone Torrent Engine App (VPN-isolated)

Status: Proposed
Created: 2026-06-22

## Motivation

Two problems with the torrent engine being **in-process** inside `api`:

1. **VPN-only-for-torrent.** We want torrent traffic to egress through a VPN while
   everything else (TMDb, Jellyfin serving, the web UI) stays on the direct
   connection. This is impossible while the engine shares the `api` process —
   routing `api` through a VPN would drag the entire HTTP surface with it.
2. **Throughput vs. exposure under docker.** BitTorrent's connection churn collapses
   under the docker bridge NAT (worst on Windows/WSL2 and macOS Docker Desktop).
   `network: "host"` fixes throughput but, because it is per-container and the `api`
   image binds all interfaces, it also exposes the internal `8080` surface on the
   host and breaks HTTP portability (see the rejected approach in
   [media-server#21](https://github.com/alex-de-haas/media-server/pull/21)).

Both reduce to **"apply a network policy to torrent traffic only"**, which requires
the torrent client to live in its **own network namespace** — i.e. its own
container. So we extract the engine into a standalone Hosty runtime app that
`media-server` consumes as a dependency.

This mirrors the proven self-hosting pattern (a torrent client isolated behind a VPN,
with the media manager on the normal network talking to it over an API and a shared
downloads volume).

## Decision

- **Standalone runtime app** `torrent-engine` (its own image + manifest), not a
  service inside `media-server`. `media-server` declares it as a cross-app
  `dependency`.
- **Single consumer for now** (`media-server`). Build a clean API boundary so it
  *can* be reused later, but **do not** build multi-tenancy (per-consumer ownership,
  quotas, isolation) until a real second consumer exists (YAGNI).
- **VPN inside the torrent container** (decided): the image runs **OpenVPN** itself
  and routes torrent traffic through the tunnel, with a killswitch so peer traffic
  cannot leak to the direct connection if the tunnel drops. No VPN sidecar / netns
  sharing — keeps the platform change smaller.

### Why VPN-in-container also likely fixes throughput

From the host/WSL2 perspective, all the torrent's peer connections are encapsulated
in a **single** tunnel flow (one OpenVPN UDP/TCP socket). The per-peer NAT churn that
throttles the bridge largely disappears, and many VPN providers offer port-forwarding
for inbound peers. So this design may make docker-host host networking unnecessary for
torrent — though host networking (docker-host#57) remains a valid general capability
and a possible non-VPN fallback.

## Components

```
┌─────────────────────────┐        ┌──────────────────────────────────┐
│ media-server (api, web) │        │ torrent-engine app               │
│  bridge networking      │  HTTP  │  bridge networking +             │
│  - pipeline/identify    │ ─────► │  in-container OpenVPN (killswitch)│
│  - catalog/library      │  SSE   │  - MonoTorrent engine            │
│  - Jellyfin surface     │ ◄───── │  - control API + progress stream │
└───────────┬─────────────┘ events └───────────────┬──────────────────┘
            │                                       │
            └──────── shared downloads volume ──────┘
                  (one filesystem; zero-copy move)
```

## Hard parts (design must solve these)

### 1. Platform gap in docker-host: container capabilities + device

Running OpenVPN inside a container needs `--cap-add=NET_ADMIN`, `--device
/dev/net/tun`, and possibly a sysctl. Hosty Core today only sets `--network`
(user-network or host) and does **not** grant capabilities/devices. **New docker-host
feature required**: a manifest way to request elevated container capabilities and
device mounts, gated by install review (this is privileged — it must be explicit and
visible). This is the critical-path platform change; it is *separate from* and smaller
than netns-sharing.

### 2. File hand-off — keep the zero-copy move

`torrents-and-organizer.md` is built on an **atomic, same-filesystem move** from
`.incoming/<downloadId>/` into the catalog. If the engine is a different container,
downloaded files must land on a filesystem `media-server` can move from in place.

- **Plan:** a **shared host-path mount** (Hosty `externalMounts`) bound into *both*
  apps at the catalog root, so the torrent app writes into
  `<catalog.root>/.incoming/<downloadId>/` and `media-server` performs the existing
  in-place move. No cross-container copy.
- **Open:** how the catalog-root selection (currently a media-server concept) is
  shared with the torrent app — the torrent app should be told only "write under this
  staging path", staying ignorant of catalogs.

### 3. Control API + auth

`media-server` stops calling MonoTorrent in-process (`TorrentService`,
`TorrentCoordinator`, SSE) and instead calls the torrent app over HTTP/SSE:

- Discovery via cross-app `HOSTY_DEPENDENCY_TORRENT_ENGINE_URL`; auth via Hosty app
  identity (the existing service-token/identity mechanism).
- Sketch:
  - `POST /downloads` `{ source (magnet|torrent), savePath, limits, keepSeeding }` → `{ infoHash }`
  - `GET  /downloads` / `GET /downloads/{infoHash}` → snapshot (state, progress, rates, peers, files, size)
  - `POST /downloads/{infoHash}/pause|resume|stop`
  - `DELETE /downloads/{infoHash}` `?deleteFiles=`
  - `GET  /events` (SSE: progress, metadata-received, completed, errored)
- `media-server`'s coordinator becomes a thin client that re-drives the ingest
  pipeline off these remote events instead of in-process engine events.

## Phased rollout

1. **(now, done)** Revert `api` to bridge + raw-port; document the WSL2 mirrored /
   per-OS throughput guidance. Interim throughput = 3–5 MB/s on WSL2-mirrored.
2. **docker-host**: add the capabilities/device manifest feature (NET_ADMIN +
   /dev/net/tun), install-review gated.
3. **torrent-engine app**: extract MonoTorrent + control API + SSE into a standalone
   app image with in-container OpenVPN + killswitch.
4. **media-server**: replace the in-process engine with the API client; wire the
   shared downloads mount; re-plumb the coordinator/SSE.
5. (later, only on demand) generalize to multiple consumers.

## Open questions

- OpenVPN config delivery: as a Hosty secret setting (`.ovpn` + creds) vs. a mounted
  config file. Killswitch implementation (iptables default-deny except tun).
- Whether `media-server` still owns seeding policy (`keepSeeding`) or it moves to the
  engine app.
- Backups: the torrent app's fast-resume/metadata state vs. media-server's DB
  ownership of in-flight downloads after the hand-off.
- Health/lifecycle coupling: media-server behavior when the torrent app is down.

## Relationship to other work

- **docker-host#57 (host networking)** — orthogonal and kept; a possible non-VPN
  throughput fallback, not used by this design.
- **Raw-port + WSL2 mirrored** — the documented interim until this app exists.
