#!/bin/sh
set -e

# The API runs unprivileged as the `app` user (uid 1654, provided by the aspnet base image). The
# persistent data directory is a Core-managed mount: Core creates it with a plain
# Directory.CreateDirectory, so it arrives owned by whichever user runs Core (root in an installed
# setup). We start as root only to fix that ownership, then drop privileges with gosu before exec'ing
# the app.
DATA_DIR="${HOSTY_APP_DATA_DIR:-/app/data}"
mkdir -p "$DATA_DIR"
chown -R app:app "$DATA_DIR" 2>/dev/null || true

exec gosu app "$@"
