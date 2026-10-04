#!/usr/bin/env bash
# Builds the bridge image and checks that it starts the sample local server (npx needs Node in the image), that uvx is
# there, and that a missing MCPAL_URL or mcp.json gives a clear message. Needs Docker, no MCPal server.
#
#   bridge/packaging/docker/test-image.sh   (set IMAGE to test an image you built already)
set -euo pipefail
cd "$(dirname "$0")/../../.."

image="${IMAGE:-mcpal-bridge:test}"
if [[ -z "${IMAGE:-}" ]]; then
  docker build -q -f bridge/Dockerfile --build-arg VERSION=0.0.0-test -t "$image" .
fi

fail() { echo "FAIL: $1" >&2; exit 1; }

out="$(docker run --rm -e MCPAL_URL=http://localhost:1 -v "$PWD/bridge/MCPal.Bridge/mcp.example.json:/config/mcp.json:ro" "$image" check 2>&1)" \
  || fail "check with the sample mcp.json failed: $out"
grep -q "everything: 1 tool(s)" <<<"$out" || fail "the sample server did not list its echo tool: $out"

docker run --rm --entrypoint uvx "$image" --version >/dev/null || fail "uvx is missing in the image"

out="$(docker run --rm -v "$PWD/bridge/MCPal.Bridge/mcp.example.json:/config/mcp.json:ro" "$image" run 2>&1 || true)"
grep -q "MCPAL_URL" <<<"$out" || fail "no clear message for a missing MCPAL_URL: $out"

out="$(docker run --rm -e MCPAL_URL=http://localhost:1 "$image" run 2>&1 || true)"
grep -q "/config/mcp.json" <<<"$out" || fail "no clear message for a missing mcp.json: $out"

# Not connected, so the health check (the status verb) must report unhealthy rather than crash.
docker run --rm -e MCPAL_URL=http://localhost:1 --entrypoint dotnet "$image" /app/MCPal.Bridge.dll status >/dev/null 2>&1 && fail "status exited 0 without a status file"

echo "ok: $image"
