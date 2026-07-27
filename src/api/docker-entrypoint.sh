#!/bin/sh
set -e

# Drops from root to an unprivileged user before starting the API.
#
# The uid is NOT fixed to the base image's `app` user (1654). Hosty Core bind-mounts the app data
# directory from ~/.hosty/apps/<id>/data on the host, owned by the user running Core — which is not
# root: `hosty` installs under $HOME and Core binds only unprivileged ports. Chowning that tree to the
# image's uid would take it away from Core on any host whose Core uid differs, and Core's uninstall path
# swallows the resulting UnauthorizedAccessException, so "remove app with data" would report success
# while leaving the data behind. Backups read the same tree.
#
# So: adopt the mount's existing owner rather than rewriting it. Ownership is only ever assigned when the
# directory is root-owned — a fresh volume, or a Core that genuinely runs as root — and root keeps full
# access to app-owned files either way.
DATA_DIR="${HOSTY_APP_DATA_DIR:-/app/data}"

# Already unprivileged (the runtime passed --user, or an operator did): nothing to drop.
if [ "$(id -u)" -ne 0 ]; then
    exec "$@"
fi

mkdir -p "$DATA_DIR"

owner="$(stat -c '%u:%g' "$DATA_DIR" 2>/dev/null || echo '0:0')"
case "$owner" in
    0:*)
        # Root-owned: nobody else has a claim on it, so hand it to the image user.
        chown app:app "$DATA_DIR" 2>/dev/null || true
        owner="app:app"
        ;;
esac

exec gosu "$owner" "$@"
