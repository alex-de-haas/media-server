#!/usr/bin/env bash
#
# Launch the Media Server for local development via Hosty Core.
#
# This is the ONLY supported dev launch path. The app is a Hosty runtime app and needs
# Core for identity, catalog mounts, and web->api service discovery. Core manages both
# services (`next dev` for web, `dotnet run` for api) as child processes — do NOT start
# them standalone and do NOT use the Claude preview tool (preview_start) for this app;
# that spawns a duplicate `next dev` and yields an unauthenticated, non-functional UI.
#
# Idempotent: ensures Core and the app are running, then prints a fresh authenticated
# URL to open in the browser. The `?code=` in the URL is single-use — re-run for a new
# browser session.
#
# Usage:
#   scripts/dev.sh [hosty-user-email]
#   HOSTY_DEV_USER=you@example.com scripts/dev.sh
#
set -euo pipefail

APP_ID="com.haas.media-server"
APP_DIR="."   # manifest.json lives at the repo root; run this script from there
USER_EMAIL="${1:-${HOSTY_DEV_USER:-$(git config user.email 2>/dev/null || true)}}"

if [[ -z "${USER_EMAIL}" ]]; then
  echo "error: no user email. Pass one as the first argument or set HOSTY_DEV_USER" >&2
  echo "       (the Hosty user assigned to ${APP_ID})." >&2
  exit 2
fi

# 1) Hosty Core up? (Start only if not running, to avoid clobbering a from-source Core.)
if ! hosty status 2>/dev/null | grep -qi 'running'; then
  echo "Hosty Core is not running — starting it…"
  hosty core start
fi

# 2) App installed under the dev runtime?
if ! hosty apps list 2>/dev/null | grep -q "${APP_ID}"; then
  echo "Installing ${APP_ID} (dev runtime)…"
  hosty apps install "${APP_DIR}" --runtime dev
fi

# 3) Ensure it is started (idempotent — a no-op if already running).
hosty apps start "${APP_ID}" >/dev/null 2>&1 || true

# 4) Print a fresh, authenticated URL to open.
echo
echo "${APP_ID} is running under Hosty Core. Open this URL (one-time code):"
hosty apps open "${APP_ID}" --user "${USER_EMAIL}" --format url
