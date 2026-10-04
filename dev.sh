#!/usr/bin/env bash
# Runs the portal with hot reload: PostgreSQL (Docker), the server (dotnet watch) and the SPA (Vite).
#
#   ./dev.sh
#
# Open http://localhost:5173. UI edits reload in the browser, C# edits restart the server. Ctrl-C stops everything
# (the database container keeps running; `docker compose -p mcpal-dev down -v` removes it with its data). Needs Docker, the .NET 10 SDK and Node.js.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"
# Own project name: a separate database volume, so `docker compose up` data and dev data never mix.
compose=(docker compose -p mcpal-dev -f docker-compose.yml -f docker-compose.dev.yml)

"${compose[@]}" up -d --wait postgres

[[ -d server/portal/node_modules ]] || (cd server/portal && npm ci)

# Vite (5173) proxies /api, /mcp, /hub and /oauth to the server (8080), so the portal's public URL is the Vite one.
export ASPNETCORE_ENVIRONMENT=Development
export ConnectionStrings__Mcpal="Host=localhost;Port=5433;Database=mcpal;Username=mcpal;Password=mcpal"
export Mcpal__PublicUrl="http://localhost:5173"
# Keeps Data Protection keys across restarts; otherwise the stored signing keys cannot be read after each restart.
export Mcpal__DataProtectionPath="$PWD/.dev/keys"

# Each service runs in its own process group (setsid): dotnet watch and npm start grandchildren that a plain kill would orphan.
pids=()
cleanup() {
  trap - EXIT INT TERM
  for pid in "${pids[@]}"; do kill -INT -- "-$pid" 2>/dev/null || true; done
  # Background jobs of a script may ignore SIGINT, so give them a moment, then kill what is left.
  for _ in 1 2 3 4 5 6 7 8 9 10; do
    alive=0
    for pid in "${pids[@]}"; do kill -0 -- "-$pid" 2>/dev/null && alive=1; done
    ((alive)) || break
    sleep 0.5
  done
  for pid in "${pids[@]}"; do kill -KILL -- "-$pid" 2>/dev/null || true; done
}
trap cleanup EXIT INT TERM

setsid dotnet watch run --project server/MCPal.Server --non-interactive &
pids+=($!)
(cd server/portal && exec setsid npm run dev) &
pids+=($!)

# One of them ending (a crash, a build error that stops watch) takes the other one down too.
wait -n
