# Background Tasks and Progress

## Description

Media Server uses background jobs for long-running work and reports progress to
the UI through SignalR. Jobs back the automation pipeline and other operations.
Work must be observable, cancellable where practical, and represented with stable
job state that survives restarts.

## Job Types

- Torrent downloads.
- Organizer (hardlink) operations.
- Media scans.
- Identify and metadata enrich/refresh (including language backfill).
- Media probing.
- File operations (copy, move, delete).
- Reconciler runs that re-drive stuck pipeline items.

## Progress Model

Each job has: job id, type, related entity (e.g. ingest item, torrent), status,
progress 0–100, attempt count, and optional error. Pipeline stages (see
[Automation pipeline](automation-pipeline.md)) emit jobs so the UI can show the
full flow per item, not just isolated tasks.

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

## Persistence and Recovery

Job and pipeline state is persisted under `HOSTY_APP_DATA_DIR`. On restart, the
reconciler resumes non-terminal items and re-drives stuck work with bounded
retries and backoff.

## Testing Expectations

Backend tests should use xUnit and Imposter. Required coverage:

- Job lifecycle transitions.
- Progress update publication.
- Failure state and error propagation.
- Cancellation behavior for supported job types.
- Recovery: reconciler resumes persisted non-terminal jobs after restart.
