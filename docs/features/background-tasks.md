# Background Tasks and Progress

## Description

Media Server uses background jobs for long-running work and reports progress to
the frontend through SignalR. Background work must be observable, cancellable
where practical, and represented with stable job state.

## Background Jobs

Initial background job types:

- Torrent downloads.
- File operations such as copy, move, and delete.
- Media scans.
- Metadata fetching.

## Progress Model

Each job has:

- Job ID.
- Type.
- Status.
- Progress from 0 to 100.
- Optional error.

## SignalR Events

Progress events:

- `jobStarted`
- `jobProgress`
- `jobCompleted`
- `jobFailed`

## Testing Expectations

Backend tests should use xUnit and Imposter.

Required coverage:

- Job lifecycle transitions.
- Progress update publication.
- Failure state and error propagation.
- Cancellation behavior for supported job types.
