# syntax=docker/dockerfile:1
# Build context: src/api (the self-contained .NET solution for the `api` service).

# Bases pinned by digest for reproducible builds, matching how this repo pins its GitHub Actions.
# To update, re-pin with:
#   docker buildx imagetools inspect mcr.microsoft.com/dotnet/<repo>:10.0 --format '{{.Manifest.Digest}}'

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664 AS build
WORKDIR /src

# Restore against the project file alone first so the package layer caches across source-only changes.
COPY MediaServer.Api/MediaServer.Api.csproj MediaServer.Api/
RUN dotnet restore MediaServer.Api/MediaServer.Api.csproj

COPY MediaServer.Api/ MediaServer.Api/
RUN dotnet publish MediaServer.Api/MediaServer.Api.csproj -c Release -o /app/publish --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:1fa23fc4872d95fd71c2833ebe65d7e84a43b2d51a31d119516852f13d9505a7 AS runtime
WORKDIR /app

# ffprobe (from ffmpeg) for media probing — v1 does no transcoding, so the full encoder is not needed,
# but ffmpeg is the canonical ffprobe distribution. FfprobePath defaults to a PATH lookup of "ffprobe".
# gosu lets the entrypoint drop from root to the base image's unprivileged `app` user (uid 1654) after
# fixing data-dir ownership (see docker-entrypoint.sh).
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg gosu \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build --chown=app:app /app/publish ./
COPY docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN chmod +x /usr/local/bin/docker-entrypoint.sh \
    && install -d -o app -g app /app/data

# In a container HostyKestrel defers to ASPNETCORE_URLS; bind both the internal and Jellyfin ports.
# (Core publishes hostPort:containerPort and injects HOSTY_* for everything else.) Downloading runs in
# the external torrent-engine app, so this image no longer binds a raw BitTorrent port. Both ports are
# above 1024, so the unprivileged user can bind them.
ENV ASPNETCORE_URLS=http://+:8080;http://+:8096
EXPOSE 8080 8096

ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh", "dotnet", "MediaServer.Api.dll"]
