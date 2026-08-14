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
log="$(mktemp)"
trap 'rm -f "$log"' EXIT
swift package plugin --allow-writing-to-package-directory generate-code-from-openapi --target MediaServerAPI 2>&1 | tee "$log"

# The generator skips what it cannot represent and carries on. A property described as a union with
# `null` — which is how .NET writes a nullable reference — vanishes from the generated type with only a
# warning, so the field is silently unavailable and nothing fails to compile. That is worse than either
# outcome generating a client was meant to buy, and it had already eaten eight properties before anyone
# looked. `NullableRefSchemaTransformer` on the server keeps the document in a shape that survives; this
# is what notices if something else stops surviving.
if grep -q "is not supported, reason" "$log"; then
    echo "" >&2
    echo "error: the generator skipped part of the document, so the Swift types are incomplete." >&2
    grep "is not supported, reason" "$log" | sed 's/^/  /' >&2
    exit 1
fi

shasum -a 256 "$document" | cut -d' ' -f1 > "$stamp"
echo "Generated from $(cat "$stamp")"
