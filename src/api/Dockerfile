# syntax=docker/dockerfile:1
# Build context: src/api (the self-contained .NET solution for the `api` service).

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the project file alone first so the package layer caches across source-only changes.
COPY MediaServer.Api/MediaServer.Api.csproj MediaServer.Api/
RUN dotnet restore MediaServer.Api/MediaServer.Api.csproj

COPY MediaServer.Api/ MediaServer.Api/
RUN dotnet publish MediaServer.Api/MediaServer.Api.csproj -c Release -o /app/publish --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# ffprobe (from ffmpeg) for media probing — v1 does no transcoding, so the full encoder is not needed,
# but ffmpeg is the canonical ffprobe distribution. FfprobePath defaults to a PATH lookup of "ffprobe".
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./

# In a container HostyKestrel defers to ASPNETCORE_URLS; bind both the internal and Jellyfin ports.
# (Core publishes hostPort:containerPort and injects HOSTY_* for everything else.) Downloading runs in
# the external torrent-engine app, so this image no longer binds a raw BitTorrent port.
ENV ASPNETCORE_URLS=http://+:8080;http://+:8096
EXPOSE 8080 8096

ENTRYPOINT ["dotnet", "MediaServer.Api.dll"]
