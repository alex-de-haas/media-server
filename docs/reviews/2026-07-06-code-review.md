# media-server — Code Review

- **Date:** 2026-07-06
- **Baseline:** `main` @ `c6a2b83`
- **Scope:** entire repo — `src/api` (C#/ASP.NET Core, net10.0, EF Core + SQLite) and `src/web` (Next.js 16 BFF/web), `manifest.json`, `scripts/`, `.github/workflows`, docs (light pass), git hygiene. Cross-checked against the sibling engine repos and Hosty Core.
- **Method:** full read of the manifest, workflows, and all security-critical API code (auth, streaming, sandbox, engines, pipeline, data layer); lighter pass over the web app. Top findings re-verified line-by-line in the main session. No builds/tests were run.

**Brief corrections:** `src/api` is C#, not Node/TS (only `src/web` is TypeScript). `.DS_Store` and `.pnpm-store` exist in the working tree but are **not tracked** (`git ls-files` = 367 files, clean; `.gitignore` covers both plus DBs/env/test artifacts). `manifest.json` version (0.12.0) matches `src/web/package.json` — no drift. No secrets in tracked files.

**Severity scale.** *Critical* — remote-unauthenticated compromise or guaranteed data loss. *High* — real security/data-loss path or actively misleading behavior. *Medium* — correctness/robustness with a plausible trigger. *Low* — hardening, UX, hygiene.

**Totals:** 0 Critical / 2 High / 6 Medium / 10 Low.

---

## Executive summary

This is the most careful codebase of the four reviewed. Every route group has explicit auth; all filesystem access goes through a real sandbox (`CatalogPathSandbox` = lexical containment **plus** symlink-target resolution); SQL is 100 % EF-parameterized; app tokens are SHA-256-hashed at rest; PIN login has argon2id + timing-equalized username probing + per-credential lockout. Streaming addresses media by public id and re-sandboxes the stored path. Malicious torrent file paths are neutralized because the organizer re-resolves every source/target through the sandbox. No Critical findings.

The two High items are both **exposure/DoS**, not auth bypass: the public Jellyfin port also serves the entire internal management API (H1), and the PIN-login rate limiter keys on a source IP that collapses to one bucket behind ingress (H2). The Medium tier is dominated by two structural themes: **process/engine calls without timeouts** (a single hung ffprobe stalls the single-worker pipeline; engine-lost jobs strand forever) and **hot-endpoint query hygiene** (full TMDb blobs and in-memory pagination on 5 s-polled paths).

---

## High

### H1 — Public Jellyfin port also serves the internal management API (no port segregation)
`src/api/MediaServer.Api/Program.cs:306-329` maps both surfaces (`/api/*` management + Jellyfin) onto one pipeline; `Hosty/HostyKestrel.cs:15-38` and `src/api/Dockerfile:30` (`ASPNETCORE_URLS=http://+:8080;http://+:8096`) bind **both** ports on all interfaces. *(Verified: the container's `ASPNETCORE_URLS` overrides the `localhost` binding HostyKestrel would otherwise use — see the Dockerfile comment at `:27` and HostyKestrel `:19`.)* The manifest publishes the `jellyfin` port (8096) publicly (`manifest.json:21,59`), so with cloudflared attached **every internal endpoint — torrent add, catalog config, settings, SSE, Jellyfin credential management — is internet-reachable on the public origin**, differing from the internal surface only by expected auth scheme. Auth is enforced (Hosty token revalidated at Core), so this is exposure, not bypass — but the internal admin surface was designed to sit behind the BFF on a private port. **Failure scenario:** any future auth-handler bug, Core-token leak, or DoS-able endpoint becomes internet-exploitable instead of requiring host access. **Fix:** gate route groups by `HttpContext.Connection.LocalPort` (internal group → internal port only; Jellyfin group → jellyfin port only) resolved from `HostyOptions`/bound addresses; reject cross-surface requests with 404.

### H2 — PIN-login rate limiter keys on `RemoteIpAddress` with no forwarded-header handling
`Program.cs:195-202` partitions the `/Users/AuthenticateByName` limiter (10 req/30 s) by `Connection.RemoteIpAddress`; there is **no** `UseForwardedHeaders`/`X-Forwarded-For` processing anywhere in the API (grep-verified). Behind cloudflared and/or docker-proxy all external clients share one source IP → one global bucket. **Failure scenarios:** (a) a single attacker trivially locks out PIN login for every legitimate Infuse user (sustained 429s); (b) per-IP throttling gives no per-attacker isolation, leaving only the per-credential lockout (`Jellyfin/Auth/JellyfinCredentialService.cs:236-257`) against 6-digit PIN guessing. **Fix:** honor `CF-Connecting-IP`/`X-Forwarded-For` from the trusted ingress hop only, and keep a modest global limiter as a backstop.

---

## Medium

### M1 — `ffprobe` has no execution timeout: one hung probe stalls the whole pipeline
`Probe/FfprobeMediaProbe.cs:35-49` awaits `WaitForExitAsync` with only the caller's token; for pipeline stages that is the host `stoppingToken` (never cancelled until shutdown). `Pipeline/PipelineWorkers.cs:10-27` drives ingests **serially on a single worker**, so an ffprobe wedged on a corrupt/network-mounted file blocks every other ingest forever; the lease reconciler re-enqueues but the worker is still stuck. Also used from `TranscodeOutputImporter.cs:49` and `LibraryMaintenanceService.cs:88`. **Fix:** linked CTS with a per-probe deadline (~2 min) + `Kill(entireProcessTree: true)`, mirroring the engine-control-call pattern.

### M2 — Library import walker aborts on permission errors and follows directory symlinks
`Library/LibraryImportService.cs:137-148` uses `Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)`: (a) that overload throws `UnauthorizedAccessException` from `MoveNext` on the first unreadable subdir — the try/catch at `:70-79` only wraps `FileInfo.Length`, so the whole `POST /api/library/import` scan 500s on one bad directory; (b) it recurses into directory symlinks with no cycle detection — a self-referencing link under a catalog root (plausible on NAS mounts) loops until path-length explosion, and links pointing outside the root get their targets ingested. **Fix:** `new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }`.

### M3 — Transcode jobs stranded non-terminal when the engine forgets them; the duplicate-output guard then blocks retries
`Transcoding/TranscodeCoordinator.cs:136-141` — `Apply` no-ops when `GetSnapshot` returns null, so a job the engine lost (engine restart wipes its state; the seed at `RemoteTranscodeEngine.cs:43-59` only imports jobs the engine still knows) stays `Queued`/`Running` forever. `TranscodeService.cs:96-100` then rejects any new job for the same output path until an admin manually removes the row. **Fix:** in the reconcile tick, mark jobs with no engine snapshot as `Failed` after a grace period (the engine's `/jobs` list is authoritative at startup). *(This is the consumer-side mirror of transcode-engine's in-memory-only job model — the two contracts should be designed together.)*

### M4 — Hot-endpoint query hygiene: full TMDb blobs and in-memory pagination on polled paths
- `Library/LibraryReadService.cs:407-419` (`MetadataByItemAsync`) loads **entire `MetadataRecord` rows including the multi-KB `Raw` TMDb JSON, one per language**, for every item on every `/api/library` call; the web grid polls it every 5 s (`src/web/src/components/library-grid.tsx:16`) despite SSE invalidation existing. O(items × languages) blob hydration per client per 5 s.
- `Jellyfin/JellyfinLibraryService.cs:104-115` materializes all matching items then applies `StartIndex`/`Limit` with in-memory `Skip/Take` — Infuse's paged `/Items` calls each load the whole library.
- `Pipeline/IngestService.cs:26-43` lists every ingest row ever (no paging), polled every 20 s by the Activity page.

**Fix:** project only needed columns for list surfaces (exclude `Raw`), push paging into SQL, cap/page ingest history.

### M5 — `RemoteTorrentEngine.Dispose` is not idempotent (a fixed bug pattern left unfixed in the twin class)
`Torrents/RemoteTorrentEngine.cs:401-406` does `_cts?.Cancel(); _cts?.Dispose();` — the same instance is registered three ways (concrete, `ITorrentEngine`, hosted service; `Program.cs:72-77`), and the sibling `Transcoding/RemoteTranscodeEngine.cs:267-279` **explicitly documents** that the DI container disposes it more than once and guards with `Interlocked.Exchange`. The torrent client lacks the guard, so the second dispose calls `Cancel()` on a disposed CTS → `ObjectDisposedException` during shutdown. **Fix:** copy the transcode client's guarded Dispose.

### M6 — The internal API surface silently accepts three token channels (incl. an ambient cookie)
`Hosty/HostyAuthenticationHandler.cs:62-81` accepts bearer, an `X-Docker-Host-Identity` compatibility header, and a cookie. The cookie path makes the API itself cookie-authenticated; CSRF is currently mitigated only incidentally (JSON model binding + no CORS). Since the BFF always sends bearer, **especially given H1 exposes this handler publicly**, consider dropping the cookie fallback on the API side (keep it on the Next.js BFF only) to remove the ambient-credential channel.

---

## Low

- **L1.** `/api/me` first-login upsert race → 500 (`Program.cs:345-367`): two concurrent first requests both insert an `AppUser`; the unique `HostUserId` index makes the loser throw an unhandled `DbUpdateException`. Catch and re-read.
- **L2.** `TorrentCoordinator.BroadcastProgressAsync` reads the whole `Downloads` table every 1.5 s (`:115-119`); Error-state rows accumulate. Filter by snapshot info-hashes.
- **L3.** Remote image fetch has no size cap and buffers fully in memory (`Jellyfin/JellyfinImageService.cs:110,156`). Provider-constructed URLs (no traversal/SSRF), so a robustness cap, not a vuln.
- **L4.** `ProbeStage` bypasses the sandbox (`Pipeline/Stages/ProcessingStages.cs:175` uses raw `Path.Combine`) while every other consumer uses `ICatalogPathSandbox.TryResolve`. Use the sandbox for consistency/defense-in-depth.
- **L5.** Non-admin users can add torrents, pause/resume, stop seeding, retry/match ingests (`Torrents/TorrentEndpoints.cs:14-39`, `Pipeline/IngestEndpoints.cs:10-36` — only deletes are admin). Likely intended for a household server (free-space refusal exists), but confirm intent.
- **L6.** Dev request log captures query strings including Jellyfin `api_key` tokens (`Diagnostics/RequestLoggingMiddleware.cs:32-34` + query-token acceptance for `/Videos`//Images/). Development-only; mask `api_key`.
- **L7.** Manifest/platform drift: `pullPolicy: "always"` + floating `latest` tags (`manifest.json:18,41`) — `pullPolicy` is dead in current Core; clean up on the next bump.
- **L8.** CI/workflow inconsistencies: `ci.yml` tag-pins actions while `publish.yml` SHA-pins; CI builds web on Node 24 (`ci.yml:55`) while the runtime image is `node:22` (`src/web/Dockerfile`). Align both.
- **L9.** Stale "hardlink / `library/` subtree" comments contradict the actual move-based layout (`Catalogs/CatalogService.cs:10`, `Library/LibraryDeleteService.cs:9`, `Library/LibraryEndpoints.cs:93,146`, `Catalogs/CatalogModels.cs:54` vs the authoritative `Catalogs/CatalogPaths.cs:8-10`). Also `MediaServer.Api.csproj` keeps `AllowUnsafeBlocks` "required by [LibraryImport] P/Invoke" — no `[LibraryImport]` exists anymore; drop the flag.
- **L10.** Identity token in `sessionStorage` as cross-site-cookie fallback (`src/web/src/lib/api.ts:5-28`). XSS-reachable by design; acceptable given no XSS sinks exist — worth a comment that any future raw-HTML rendering re-opens this.

**Verified non-findings worth recording:** TMDb v3 key is *not* logged (net10.0 + OTel redact query values by default); streaming re-sandboxes the stored path with framework range handling; malicious torrent `../` paths are neutralized by the organizer sandbox; cross-user Jellyfin access is consistently gated via `CanActAs`/`ResolveActingUserIdAsync`; SQL is fully EF-parameterized; SSE fan-out uses bounded drop-oldest channels with heartbeats.

---

## Architecture observations

- Clean two-service Hosty app: C# API (internal :8080 + Jellyfin-compatible :8096) + a Next.js BFF that proxies same-origin and attaches the validated Hosty bearer — the browser never talks to the API directly.
- Two fully separated auth planes: Hosty identity (Core-revalidated, 30 s positive cache) for management, and app-owned PIN → opaque hashed tokens for the Jellyfin surface; a polling `DirectoryReconcileService` revokes Jellyfin credentials when users are unassigned (no Core webhooks).
- Engine integration is one consistent pattern ×2 (torrent, transcode): `Remote*Engine` = HTTP control + SSE mirror into in-memory snapshots; `*Coordinator` = event/timer bridge to persistence; DB stores only durable facts, live progress stays engine-side; cross-app paths exchanged as `mountLabel + relative`, never absolute host paths.
- The ingest pipeline is a small, well-engineered workflow engine (ordered stages, lease + rowversion concurrency token, exponential backoff, needs-review parking, change-tracker rollback, crash-resume reconciler) — but **single-worker**, so one blocking stage (M1) is a global stall.
- Data layer: EF Core/SQLite with WAL + busy-timeout interceptor, periodic online-backup worker, UTC-string DateTimeOffset conversion for in-SQL comparisons, IN-lists chunked at 500.
- All filesystem authority derives from `CatalogPathSandbox` + `CatalogPaths` (`.incoming/` staging vs published root); destructive ops refuse staging/library confusion and prune empty parents with root-boundary checks.
- The web app is thin and disciplined: React Query + one SSE bridge, polling only as SSE-down fallback (some intervals aggressive, see M4).

## Test gaps

Coverage is strong (69 test files across sandbox, auth, streaming, pipeline, engine wire clients, Jellyfin mapping; web unit + mocked-BFF Playwright e2e). Missing:

1. **Port/surface segregation** — nothing asserts internal endpoints are unreachable on the Jellyfin port (would have caught H1; not yet implemented).
2. **`LibraryImportService` FS edge cases** — permission errors, symlink cycles, mid-scan disappearance (M2).
3. **Coordinator event handling** — fire-and-forget handlers, missed-completion self-heal, engine-lost-job reconciliation (M3).
4. **`FfprobeMediaProbe` process behavior** — cancellation/kill/timeout of the real process path (M1).
5. **SSE server endpoint** — heartbeat/disconnect/backpressure under a slow reader.
6. **Auth rate-limiter** partitioning/429 mapping and **`/api/me` concurrent upsert** (L1).
7. **Migrations** — no test applies the full chain to a fresh + seeded DB.

## Priority

1. **H1** port segregation (largest structural exposure).
2. **H2** forwarded-header handling for the login limiter (lockout + brute-force isolation).
3. **M1** ffprobe timeout/kill (single-worker global-stall risk).
4. **M3** engine-lost-job reconciliation; **M5** idempotent torrent-engine Dispose.
5. **M4** query hygiene on polled endpoints; **M2** robust import walker.
