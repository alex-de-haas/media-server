# Security and Configuration

## Description

Media Server must protect local files, media libraries, torrent operations,
administrator settings, and streaming endpoints. Configuration is provided
through environment variables, Docker Host module settings, or host-level secret
management.

## Security Requirements

Core requirements:

- Authentication through JWT or cookie-based application sessions.
- Authorization per operation.
- Path sandboxing for all file access.
- Rate limiting on public endpoints.
- TMDb API key stored securely.
- Query-string tokens accepted only for explicitly supported compatibility
  endpoints.
- Secrets must be redacted from logs and metrics.

Docker Host identity requirements are documented in
[Docker Host module](docker-host-module.md).

Jellyfin-compatible token requirements are documented in
[Jellyfin-compatible streaming](jellyfin-compatible-streaming.md).

## Server Configuration

Configurable server areas:

- Storage roots.
- TMDb API key.
- Torrent limits.
- Scan schedules.
- Jellyfin compatibility.
- Streaming direct play, HLS, and transcoding behavior.

## Environment Variables

Core environment variables:

```text
MEDIA_STORAGE_ROOTS
TMDB_API_KEY
DATABASE_CONNECTION
TORRENT_MAX_DOWNLOAD_SPEED
TORRENT_MAX_UPLOAD_SPEED
```

Streaming environment variables are documented in
[Jellyfin-compatible streaming](jellyfin-compatible-streaming.md).

Docker Host module settings are documented in
[Docker Host module](docker-host-module.md).

## Testing Expectations

Backend tests should use xUnit and Imposter.

Required coverage:

- Authentication success and failure paths.
- Authorization checks for file, torrent, media, and streaming operations.
- Path sandboxing and traversal rejection.
- Secret redaction.
- Configuration binding and validation.
