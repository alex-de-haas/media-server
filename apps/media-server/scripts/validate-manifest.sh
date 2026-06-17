#!/usr/bin/env bash
# Lightweight Hosty manifest validation for CI: JSON validity, required keys, and that the
# `dev` localCommand working directories and build targets actually exist in the repo. This is
# not a full schema check (Core validates on install) but catches the common breakages.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"        # apps/media-server
REPO_ROOT="$(cd "$APP_DIR/../.." && pwd)"       # repo root
MANIFEST="$APP_DIR/manifest.json"

fail() { echo "manifest validation FAILED: $*" >&2; exit 1; }

command -v jq >/dev/null 2>&1 || fail "jq is required"
[ -f "$MANIFEST" ] || fail "manifest.json not found at $MANIFEST"

# 1. Valid JSON.
jq empty "$MANIFEST" || fail "manifest.json is not valid JSON"

# 2. Required top-level keys.
[ "$(jq -r '.schemaVersion' "$MANIFEST")" = "app.0.1" ] || fail "schemaVersion must be 'app.0.1'"
for key in id version name; do
  [ -n "$(jq -r ".$key // empty" "$MANIFEST")" ] || fail "missing top-level key: $key"
done

# 3. At least one service.
service_count="$(jq '.services | length' "$MANIFEST")"
[ "$service_count" -ge 1 ] || fail "no services declared"

# 4. Every dev localCommand working directory exists (relative to repo root).
while IFS= read -r working_dir; do
  [ -z "$working_dir" ] && continue
  [ -d "$REPO_ROOT/$working_dir" ] || fail "dev workingDirectory not found: $working_dir"
done < <(jq -r '.services[].runtimes.dev | select(.type == "localCommand") | .workingDirectory // empty' "$MANIFEST")

# 5. Build targets the dev commands rely on.
[ -f "$REPO_ROOT/apps/media-server/api/MediaServer.Api/MediaServer.Api.csproj" ] || fail "api project missing"
[ -f "$REPO_ROOT/apps/media-server/web/package.json" ] || fail "web package.json missing"

# 6. Public endpoints declared.
jq -e '.endpoints[] | select(.key == "ui")' "$MANIFEST" >/dev/null || fail "missing 'ui' endpoint"
jq -e '.endpoints[] | select(.key == "jellyfin")' "$MANIFEST" >/dev/null || fail "missing 'jellyfin' endpoint"

echo "manifest OK: $(jq -r .id "$MANIFEST") v$(jq -r .version "$MANIFEST") — ${service_count} services"
