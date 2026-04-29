# Media Server – Technical Specification

## 1. Overview

The Media Server is a self-hosted application for managing files, torrents, and media libraries (movies and TV series).  
It provides a web-based UI built with **Next.js (TypeScript)** with **Tailwind** and **ShadCN components**. The backend is built with **ASP.NET Core** and exposes its REST API through **Minimal API** endpoint definitions. The distributed application is orchestrated with **.NET Aspire**, using an Aspire AppHost as the code-first application model for the backend, frontend, and supporting infrastructure. The system uses **SignalR** for real-time updates and background task progress reporting.

The system is designed to work on home servers using **Docker** and supports multiple physical storage roots for media files. Build and deployment artifacts are published as Docker images to **GitHub Container Registry (GHCR)** by GitHub Actions. It integrates with **TMDb API** for rich metadata about movies and TV series.

## 2. High-Level Architecture

```mermaid
flowchart TB
	AH[".NET Aspire AppHost<br/>(Application Model)"]
	FE["Next.js Frontend<br/>(TypeScript)"]
	BE["ASP.NET Core Backend<br/>(Minimal API)"]
	SR["SignalR Hub"]
	BG["Background Services<br/>- Torrent Engine<br/>- Media Scanner<br/>- Metadata Fetcher"]
	DB["SQLite Database"]
	ST["Physical Storage<br/>(Multiple Roots)"]
	TMDB["TMDb API"]

	AH -.orchestrates.-> FE
	AH -.orchestrates.-> BE
	AH -.configures.-> DB
	FE <-->|REST HTTPS| BE
	FE <-->|WebSocket SignalR| SR
	SR --- BE
	BE --> BG
	BG --> ST
	BE --> DB
	BG <--> TMDB
```

## 3. Technology Stack

### Frontend
- Next.js (App Router)
- TypeScript
- React Server Components (where applicable)
- ShadCN UI components
- Tailwind CSS
- SignalR JavaScript Client
- REST API consumption via fetch/axios

### Backend
- ASP.NET Core
- Minimal API endpoint definitions
- SignalR
- MonoTorrent (torrent engine)
- BackgroundServices / HostedServices
- SQLite
- TMDb API integration

### Orchestration & Deployment
- .NET Aspire AppHost
- Aspire service defaults for shared telemetry, health checks, and service discovery
- Aspire Docker hosting integration for Docker Compose artifact generation
- Docker / Docker Compose for local and self-hosted deployment
- GitHub Actions for CI/CD
- GitHub Container Registry (GHCR) for published container images

## 4. Core Features

TODO: describe core features in more detail, e.g. file management, torrent management, media libraries, background tasks, real-time updates, etc.

## 5. File & Directory Management

### 5.1 Storage Roots

- Ability to attach multiple physical directories (storage roots)
- Each root has:
  - Unique ID
  - Display name
  - Absolute physical path
  - Read/Write permissions
  - Free / total space

Example:
```json
{
  "id": "{uuid}",
  "name": "Movies Disk",
  "path": "/mnt/media/movies"
}
```

### 5.2 File Operations

Supported operations:
- Upload files (multipart / resumable optional)
- Copy files
- Move files
- Delete files
- Rename files

Constraints:
- Operations restricted to attached storage roots
- Atomic operations where supported by OS
- Large file handling (stream-based)
- Multiple files operations support

### 5.3 Directory Operations

Supported operations:
- Create directory
- Copy directory (recursive)
- Move directory
- Delete directory (recursive)
- Rename directory

Additional behavior:
- Progress reporting for long operations via SignalR
- Validation against directory traversal attacks

### 5.4 API Endpoints (Example)

GET    /api/files?path=/movies
POST   /api/files/upload
POST   /api/files/copy
POST   /api/files/move
DELETE /api/files
POST   /api/directories

## 6. Torrent Management (MonoTorrent)

### 6.1 Torrent Engine
- MonoTorrent runs as a background service
- Supports:
  - Magnet links
  - .torrent files
  - Pause / Resume / Stop
  - Sequential download (for streaming use cases)
  - Per-torrent configuration:
    - Download directory
    - Speed limits
    - Ratio limits

### 6.2 Torrent Lifecycle

States:
- Queued
- Downloading
- Paused
- Completed
- Seeding
- Error

Each torrent stores:
- InfoHash
- Name
- Progress
- Download speed
- Upload speed
- ETA
- Save path

### 6.3 Torrent API (Example)

POST   /api/torrents/add
POST   /api/torrents/{id}/pause
POST   /api/torrents/{id}/resume
DELETE /api/torrents/{id}
GET    /api/torrents

### 6.4 Real-Time Updates
- SignalR hub broadcasts:
  - Torrent progress
  - Speed updates
  - State changes
- Client subscribes once and receives updates for all active torrents

## 7. Media Libraries

### 7.1 Library Types

Supported library types:
- Movies
- TV Series

Library configuration:
```json
{
  "id": "{uuid}",
  "type": "movie",
  "name": "Movies Library",
  "paths": ["/mnt/media/movies"]
}
```

### 7.2 Media Scanning
- Manual and scheduled scans
- Scans attached directories for media files
- Supported formats:
  - .mp4, .mkv, .avi, .mov, .webm
  - Filename parsing for title, year, season, episode

### 7.3 Metadata Management (TMDb)

Capabilities:
- Fetch metadata for newly discovered files
- Re-scan and refresh metadata
- Manual match override

Metadata includes:
- Title
- Original title
- Description
- Genres
- Release date
- Runtime
- Posters & backdrops
- Cast & crew
- Seasons & episodes (for series)

Caching:
- Local metadata cache to avoid excessive TMDb requests

### 7.4 Media Entity Model (Simplified)

```json
{
  "id": "{uuid}",
  "type": "movie",
  "title": "Inception",
  "year": 2010,
  "path": "/mnt/media/movies/Inception (2010).mkv",
  "tmdbId": 27205, // TODO: change to providers dictionary for multiple metadata sources
  "metadata": {}
}
```

### 7.5 Movie Entity Fields

Movie contains fields:
- Id
- OriginalTitle
- OriginalLanguage
- Title
- Overview
- VoteAverage
- VoteCount
- ReleaseDate
- Budget
- Revenue
- PosterPath
- BackdropPath
- LogoPath
- Genres
- Crew
- Cast
- ReleaseDates
- OfficialRating

## 8. Background Tasks & Progress Tracking

### 8.1 Background Jobs
- Torrent downloads
- File operations (copy/move/delete)
- Media scans
- Metadata fetching

### 8.2 Progress Reporting
Each job has:
- Job ID
- Type
- Status
- Progress (0–100)
- Error (optional)

SignalR events:
- jobStarted
- jobProgress
- jobCompleted
- jobFailed

## 9. Streaming

API for Jellyfin protocol and support clients like Infuse.

## 10. Frontend Application (Next.js)

### 10.1 Pages / Sections
- Dashboard
- File Browser
- Torrents
- Media Libraries
- Movies
- TV Series
- Settings

### 10.2 State Management
- Server data via REST
- Real-time updates via SignalR
- Optional client cache (React Query / SWR)

### 10.3 UI Features
- File explorer (tree + list)
- Torrent list with live progress bars
- Media grids with posters
- Media detail pages
- Background task notifications

## 11. Security
- Authentication (JWT / Cookie-based)
- Authorization per operation
- Path sandboxing
- Rate limiting on public endpoints
- API key for TMDb stored securely

## 12. Configuration

### 12.1 Server Configuration
- Storage roots
- TMDb API key
- Torrent limits
- Scan schedules

### 12.2 Environment Variables

```
MEDIA_STORAGE_ROOTS
TMDB_API_KEY
DATABASE_CONNECTION
TORRENT_MAX_DOWNLOAD_SPEED
TORRENT_MAX_UPLOAD_SPEED
```

## 13. Build, Packaging & Deployment

### 13.1 .NET Aspire Application Model

The project must include a .NET Aspire AppHost that acts as the single source of truth for the distributed application topology.

The AppHost must model:
- ASP.NET Core Minimal API backend
- Next.js web frontend
- Supporting infrastructure services, such as database, cache, reverse proxy, or observability components when added
- Service references between frontend and backend
- Environment variables required for local development and containerized deployment

The AppHost should be used for local orchestration and for producing deployment artifacts. Production containers should run the actual application services, not the AppHost itself.

### 13.2 Containerization Strategy

The backend and frontend should be packaged as separate Docker images:
- Backend image: ASP.NET Core application exposing Minimal API endpoints.
- Frontend image: Next.js application running with the production Next.js server unless the frontend is later converted to a fully static export.

Docker Compose should be the primary self-hosted deployment format. Generated or maintained Compose files must make service dependencies explicit and configure backend/frontend networking so that the frontend can reach the backend API and SignalR hub.

### 13.3 GitHub Actions CI/CD

A GitHub Actions workflow must be created to build and publish container artifacts.

The workflow must:
- Run on pushes to the main branch and on pull requests where validation is required.
- Restore and build the .NET solution.
- Install frontend dependencies and build the Next.js application.
- Run backend unit tests with xUnit.
- Build Docker images for the backend and frontend.
- Publish Docker images to GitHub Container Registry (GHCR).
- Tag images with at least the Git commit SHA and optionally `latest` for the main branch.
- Use `GITHUB_TOKEN` with `packages: write` permissions for GHCR publishing.

Example target image names:
- `ghcr.io/<owner>/<repository>/media-server-api:<sha>`
- `ghcr.io/<owner>/<repository>/media-server-web:<sha>`

### 13.4 Deployment Flow

```mermaid
flowchart LR
	DEV["Developer Push"]
	GHA["GitHub Actions"]
	TEST["Build & Test<br/>.NET + Next.js"]
	IMG["Build Docker Images"]
	GHCR["GitHub Container Registry"]
	HOST["Docker Host"]
	DC["docker compose pull && docker compose up -d"]

	DEV --> GHA
	GHA --> TEST
	TEST --> IMG
	IMG --> GHCR
	GHCR --> HOST
	HOST --> DC
```

The deployment host should pull the published images from GHCR and run them using Docker Compose. Secrets and environment-specific values must be provided through Compose environment files or host-level secret management, not baked into images.

## 14. Future Enhancements
- Media streaming (HLS/DASH)
- Transcoding pipeline
- User profiles
- Watch history
- Subtitle management
- Plugin system

## 15. Non-Goals
- Public torrent indexing
- DRM-protected content playback
- Cloud-only storage (initial version)

## 16. Summary

This Media Server provides a modular, scalable foundation for:
- File and directory management
- Torrent-based content acquisition
- Rich media libraries powered by TMDb
- Real-time progress tracking via SignalR

The architecture cleanly separates UI, API, background processing, and deployment orchestration, ensuring long-term maintainability and extensibility.
