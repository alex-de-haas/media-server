#!/usr/bin/env bash
# Regenerates the Swift client from the committed OpenAPI document.
#
# Run this whenever /native/v1 changes shape. The document itself is not copied — the Swift target
# holds a symlink to src/api/openapi/, so there is one copy in the repository and it cannot drift.
# What can go stale is the generated code, which is why the hash below is recorded and CI checks it.
#
# Needs a Mac with Xcode: the package targets tvOS, and the client is built locally by decision
# (see src/apple/README.md).
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
document="$root/src/api/openapi/MediaServer.Api_native.json"
package="$root/src/apple/MediaKit"
stamp="$package/Sources/MediaServerAPI/GeneratedSources/.openapi-sha256"

cd "$package"
swift package plugin --allow-writing-to-package-directory generate-code-from-openapi --target MediaServerAPI

shasum -a 256 "$document" | cut -d' ' -f1 > "$stamp"
echo "Generated from $(cat "$stamp")"
