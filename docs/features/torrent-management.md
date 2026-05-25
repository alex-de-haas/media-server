# Torrent Management

## Description

Media Server uses MonoTorrent to manage torrent downloads as backend background
work. Torrent state, progress, and speed updates are exposed to the frontend in
real time through SignalR.

## Torrent Engine

MonoTorrent runs as a background service.

Supported capabilities:

- Magnet links.
- `.torrent` files.
- Pause, resume, and stop.
- Sequential download for future streaming use cases.
- Per-torrent download directory.
- Per-torrent speed limits.
- Per-torrent ratio limits.

## Torrent Lifecycle

Torrent states:

- Queued.
- Downloading.
- Paused.
- Completed.
- Seeding.
- Error.

Each torrent stores:

- InfoHash.
- Name.
- Progress.
- Download speed.
- Upload speed.
- ETA.
- Save path.

## API Endpoints

Example internal endpoints:

```text
POST   /api/torrents/add
POST   /api/torrents/{id}/pause
POST   /api/torrents/{id}/resume
DELETE /api/torrents/{id}
GET    /api/torrents
```

## Real-Time Updates

The SignalR hub broadcasts:

- Torrent progress.
- Speed updates.
- State changes.

The client subscribes once and receives updates for all active torrents.

## Testing Expectations

Backend tests should use xUnit and Imposter.

Required coverage:

- Torrent creation from magnet links and `.torrent` files.
- Pause, resume, delete, and error-state transitions.
- Save path validation against configured storage roots.
- Progress and state events published through the application event model.
