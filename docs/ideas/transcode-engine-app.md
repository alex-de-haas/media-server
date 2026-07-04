# Idea: Standalone Transcode Engine App (VAAPI-in-docker)

Status: Implemented (movies-only v1; later items deferred)
Created: 2026-06-26
Updated: 2026-07-04

> **Shipped (2026-06-26, PR #38).** The standalone `transcode-engine` app exists (sibling repo
> `../transcode-engine`: manifest + Dockerfile + the ffmpeg engine and full HTTP/SSE control API, with
> tests). The media-server consumer trio is wired (`ITranscodeEngine` / `RemoteTranscodeEngine` /
> `DisabledTranscodeEngine`, discovered via `HOSTY_DEPENDENCY_TRANSCODE_ENGINE_URL`, with a disabled
> fallback), and the full user-facing surface (backend job/service, output→version import, and the
> movie-page "Convert" UI) is in place — all five rollout phases below are done. Only the explicit
> **(later)** follow-ups remain (series, resolution scaling/audio re-encode, live transcode, etc.).
> Built as a deliberate mirror of [Torrent engine app](torrent-engine-app.md).

## Motivation

We want ffmpeg-based conversion (re-encode library files to HEVC/AV1, remux containers) with **hardware
encoding**. Two reasons not to do this in-process inside `api`:

1. **Hardware access is privileged.** VAAPI hardware encoding needs a host `/dev/dri` render node passed
   into the container. Confining that privileged passthrough to one small, single-purpose app keeps it out
   of `api`, which holds the database, tokens, and the Jellyfin surface.
2. **Resource isolation.** Encoding is CPU/GPU-bound and long-running; it should not compete with the
   request-serving `api` process and should survive `api` restart/backup independently.

This is the same isolation argument that produced [`torrent-engine`](torrent-engine-app.md), so the design
deliberately mirrors it: a single job/SSE control API, a shared host-path mount for zero-copy file hand-off,
and a consumer that drives it as a cross-app dependency.

## Decision

- **Standalone runtime app** `transcode-engine` (its own image + manifest), not a service inside
  `media-server`. `media-server` declares it as a cross-app `dependency` (optional — `required: false`).
- **Batch/job model**, not live per-session transcoding: `POST /jobs` runs ffmpeg to completion, progress
  on SSE. This is the direct analog of a torrent download and is the high-value, low-risk case. Live
  transcoding for streaming clients is a separate, later epic.
- **Primary use case: shrink an oversized movie file.** The output is written as a **new version alongside**
  the original (a second `MediaSource` of the same movie) so the operator can verify the smaller file before
  deleting the original — a safe, non-destructive "replace". The multi-version model is the verification
  staging area; "replace" is just deleting the original source afterward.
- **Movies only for now.** Series are deferred (they need a "transcode the whole season" batch flow worth
  designing separately); movies are the first cut.
- **VAAPI-in-docker via device passthrough** (decided): the earlier hardware-encoding concern was a docker
  problem, but Hosty Core **already** grants `/dev/dri` through the manifest's `devices` (the same mechanism
  `torrent-engine` uses for `/dev/net/tun`). The user's hardware (AMD Radeon 890M) is a VAAPI device, which
  is exactly the case that works in docker today — no new platform feature and no "binary" runtime support
  required. NVENC (NVIDIA) would need the NVIDIA Container Toolkit, a *separate* docker-host feature, and is
  out of scope.
- **Single consumer for now** (`media-server`). Clean API boundary so it *can* be reused, but **no**
  multi-tenancy until a real second consumer exists (YAGNI — same call as torrent-engine).

## Components

```
┌─────────────────────────┐        ┌──────────────────────────────────┐
│ media-server (api, web) │        │ transcode-engine app             │
│  bridge networking      │  HTTP  │  bridge networking +             │
│  - ITranscodeEngine ────┼─────► │  /dev/dri device passthrough     │
│    = RemoteTranscode... │  SSE   │  - ffmpeg (VAAPI / software)     │
│  - (job/UI surface TBD) │ ◄───── │  - job API + progress stream     │
└───────────┬─────────────┘ events └───────────────┬──────────────────┘
            └──────── shared media mount (one filesystem; read in, write out) ──┘
```

## Hard parts (design must solve these)

### 1. Device passthrough — already solved by the platform

VAAPI needs `--device /dev/dri`. Hosty Core already passes manifest `devices` through (`RuntimeAppManifest`,
install-review gated), so the engine's manifest just declares `"devices": ["/dev/dri"]`. App containers run
as root by default (Core sets no `--user`), so the render node is accessible without `--group-add`. A
non-root container would need a `group_add: video|render` capability — the only *potential* new platform
request, and only if we drop root. See [Hosty platform requests](../features/hosty-platform-requests.md).

### 2. File hand-off — read input, write output on one filesystem

A job reads an input file and writes an output file. Both must land on a filesystem the consumer shares, so
the output appears in the catalog with no cross-container copy.

- **Plan:** **shared host-path mounts** (Hosty `externalMounts`) bound into *both* apps at each catalog root,
  with matching **labels**. The engine's `media` mount is `multiple`; the consumer sends `mountLabel` + a
  relative path for input and output, and the engine resolves each against its own labelled `media` root
  with the same host path. Identical to the torrent app's `downloads` label contract.

### 3. Control API + auth

`media-server` drives the engine over HTTP/SSE via `RemoteTranscodeEngine`:

- Discovery via cross-app `HOSTY_DEPENDENCY_TRANSCODE_ENGINE_URL`.
- Sketch:
  - `POST /jobs` `{ inputMountLabel?, inputPath, outputMountLabel?, outputPath, videoCodec?, hardwareAcceleration?, crf? }` → `{ jobId, ... }`
  - `GET /jobs` / `GET /jobs/{jobId}` → snapshot (state, percent, fps, speed, eta, output size)
  - `POST /jobs/{jobId}/cancel`
  - `DELETE /jobs/{jobId}` `?deleteOutput=`
  - `GET /events` (SSE: progress, started, completed, errored)
  - `GET /hardware` (detected VAAPI device(s))
- Same non-public-endpoint caveat as torrent-engine: trusted single-tenant until the shared cross-app docker
  network / app-identity story lands.

## Phased rollout

1. **(done) transcode-engine app**: manifest (`/dev/dri` device), Dockerfile (ffmpeg + VA-API userspace),
   the ffmpeg job engine, control API + SSE, multiple `media` mounts, tests.
2. **(done) media-server**: the consumer trio (`ITranscodeEngine` / `RemoteTranscodeEngine` /
   `DisabledTranscodeEngine`), DI selection on `TranscodeEngineUrl`, the optional manifest dependency.
3. **(done) media-server backend (Phase A)**: the `TranscodeJob` entity + migration, `TranscodeService`
   (`POST /api/transcode` from a movie `sourceId` → resolves input via `ICatalogPathSandbox`, computes a
   sibling output, maps to a catalog `mountLabel`, calls the engine, persists the row), `TranscodeEndpoints`,
   and `TranscodeCoordinator` (reconciles engine snapshots/events → `TranscodeJob` state). Movies only.
4. **(done) output → version (Phase B)**: on completion the coordinator runs `TranscodeOutputImporter`,
   which probes the output and attaches it directly as a new `MediaSource` (with a `VersionName` of `HEVC`/
   `H.264`, no `SourceFileId`) to the original's movie — no re-identify, so it can't become a new item. It
   surfaces in the version picker via `LibraryReadService.GetDetailAsync`. Idempotent (dedup on item+path
   plus an in-process guard); a completed-but-missing output flips the job to Failed.
5. **(done) web UI (Phase C)**: on the movie page each source has a "Convert" action (codec / encoder / CRF
   dialog) and a per-version delete (with "delete file from disk" — the "replace" step); this movie's
   conversions show inline with live progress and auto-refresh the version list on completion. The Activity
   page gets a Conversions card listing all jobs. Backed by `/api/transcode` and a new
   `DELETE /api/library/sources/{id}` (single-source delete in `LibraryDeleteService`). All movies-only,
   admin-only. tsc + eslint clean, 289 API tests green.
6. (later) resolution scaling + audio re-encode options; series ("transcode a whole season");
   live/streaming transcode as a separate epic; transcode unit tests; engine-restart zombie-row hardening.

## Open questions

- Where transcode jobs surface in the UI (a per-source action on the file/source view? a batch queue page?).
- Whether jobs are persisted (survive a media-server restart) or stay in-engine only, like torrent
  fast-resume lives in the torrent app.
- Output naming/placement policy (replace the source in place vs. write a sibling version the library picks
  up on the next scan).
- NVENC/QSV support and the docker-host GPU-passthrough feature it would require (separate from `/dev/dri`).

## Relationship to other work

- **[Torrent engine app](torrent-engine-app.md)** — the structural template; this app is a deliberate mirror.
- **[Hosty platform requests](../features/hosty-platform-requests.md)** — the `devices` capability this
  relies on is already implemented; only a non-root `group_add` and NVIDIA GPU passthrough would be new.
