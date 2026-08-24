# syntax=docker/dockerfile:1
# Build context: src/api (the self-contained .NET solution for the `api` service).

# Bases pinned by digest for reproducible builds, matching how this repo pins its GitHub Actions.
# To update, re-pin with:
#   docker buildx imagetools inspect mcr.microsoft.com/dotnet/<repo>:10.0 --format '{{.Manifest.Digest}}'

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c AS build
WORKDIR /src

# Restore against the project file alone first so the package layer caches across source-only changes.
COPY MediaServer.Api/MediaServer.Api.csproj MediaServer.Api/
RUN dotnet restore MediaServer.Api/MediaServer.Api.csproj

COPY MediaServer.Api/ MediaServer.Api/
RUN dotnet publish MediaServer.Api/MediaServer.Api.csproj -c Release -o /app/publish --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:207cc51496778557731c81ff670333d8ade4a4fec22768fd1be8e78474a84ecf AS runtime
WORKDIR /app

# No ffmpeg. This image used to install the whole package for one binary — ffprobe — which dragged in the
# Mesa/LLVM software-GL stack and every encoder as hard dependencies: 418 MB of a 747 MB image, of which the
# two binaries actually used were half a megabyte. Probing now reads container headers in managed code and
# defers to the transcode-engine for what a header cannot state, and merging external audio tracks is a job
# submitted to that same engine, so neither binary is needed here any more.
COPY --from=build /app/publish ./

# This service deliberately still runs as root, unlike `web` (which declares USER node).
#
# `catalogRoots` is a required, multiple, rw `host-path` external mount, and the API does not merely
# read it: OrganizerService creates canonical directories, IngestService recursively deletes
# `.incoming/<downloadId>`, SidecarPlacementService and JellyfinImageService move and delete files in place.
# Those directories belong to the operator — arbitrary host paths, any number of them, each with its
# own owner, and on an existing installation created root-owned by this very image. The container
# cannot take ownership of them (they are the user's media library, not app state) and there is no
# single uid to adopt when several roots disagree.
#
# Dropping privileges here therefore needs a uid/gid or supplementary-group contract that Hosty Core
# does not currently provide — Core sets no `--user` and injects no uid. Until it does, a non-root
# API would fail to organize, ingest or mux on any catalog root it does not happen to own.
#
# In a container HostyKestrel defers to ASPNETCORE_URLS; bind both the internal and Jellyfin ports.
# (Core publishes hostPort:containerPort and injects HOSTY_* for everything else.) Downloading runs in
# the external torrent-engine app, so this image no longer binds a raw BitTorrent port.
ENV ASPNETCORE_URLS=http://+:8080;http://+:8096
EXPOSE 8080 8096

ENTRYPOINT ["dotnet", "MediaServer.Api.dll"]
