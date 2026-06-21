# Background Tasks and Progress

Status: Implemented
Created: 2026-06-15
Updated: 2026-06-21

## Description

Media Server uses background jobs for long-running work and reports progress to
the UI through SignalR. Jobs back the automation pipeline and other operations.
Work must be observable, cancellable where practical, and represented with stable
job state that survives restarts.

## Job Types

- Torrent downloads.
- Organizer (move) operations.
- Media scans.
- Identify and metadata enrich/refresh (including language backfill).
- Media probing.
- Media file operations: move/organize, remap, `.incoming/` staging cleanup,
  library item deletion, and large-file streaming support work where progress is
  useful.
- Reconciler runs that re-drive stuck pipeline items.

## Progress Model

Each job has: job id, type, related entity (e.g. ingest item, torrent), status,
progress 0–100, attempt count, and optional error. Pipeline stages (see
[Automation pipeline](automation-pipeline.md)) emit jobs so the UI can show the
full flow per item, not just isolated tasks.

Torrent download progress is a special case: it is **not persisted** to the
database. The torrent engine reports it from memory and the hub broadcasts it;
only torrent **state transitions** are written and trigger pipeline actions (see
[Torrents and organizer](torrents-and-organizer.md)).

## SignalR Events

- `jobStarted`
- `jobProgress`
- `jobCompleted`
- `jobFailed`

Plus pipeline/torrent events for stage transitions, download progress, and
seeding state. Clients subscribe once and receive updates for all active work.

Transport (WebSocket, SSE, long-polling fallback) must be validated through the
Hosty Shell embed route, because behavior can depend on it (see
[Hosty runtime app](hosty-runtime-app.md)).

## Observability

Beyond the live SignalR job feed (for the UI), the app emits **OpenTelemetry**
signals for operator-grade debugging and metrics:

- **Traces.** An `ActivitySource` opens a root span per ingest item and a child
  span per pipeline stage, so one item's journey (intake → publish) is a single
  distributed trace; the trace id is the correlation id.
- **Logs.** Structured `ILogger` logging carries consistent attributes
  (`ingestItemId`, `downloadId`, `catalogId`, `stage`, `jobId`) and the active
  trace/span id, so logs join the trace.
- **Metrics.** A `Meter` exposes counters/gauges: active downloads, items per
  stage, review-queue depth, probe/enrich duration, and provider request
  count/latency.
- **Linkage.** The `Job` and `IngestItem` records store the trace id, so a UI
  activity row links to its trace.
- **Export.** OTLP exporter with a configurable endpoint; in dev it can fall back
  to console. The collector is optional — the app must run without one.
- **Redaction.** Tokens, PINs, and `api_key` query values never appear in spans,
  logs, or metric attributes (see [Security](security.md)).

## Persistence and Recovery

Job and pipeline state is persisted under `HOSTY_APP_DATA_DIR`. On restart, the
reconciler resumes non-terminal items and re-drives stuck work with bounded
retries and backoff. The reconciler claims each item with a lease
(`LeaseOwner`/`LeaseUntil`) before driving it, so it never double-processes an
item the orchestrator or an operator action is already handling (see
[Domain model](domain-model.md)).

## Testing Expectations

Backend tests should use xUnit and Imposter. Required coverage:

- Job lifecycle transitions.
- Progress update publication.
- Failure state and error propagation.
- Cancellation behavior for supported job types.
- Recovery: reconciler resumes persisted non-terminal jobs after restart.
