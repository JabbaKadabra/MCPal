#!/usr/bin/env bash
# Publishes MCPal.Agent as a self-contained single file into ./artifacts/agent/<rid>.
#
#   scripts/publish-agent.sh                       all platforms, version 0.0.0-local
#   scripts/publish-agent.sh --version 1.1.0       stamps the version into the binary (the agent reports it to the cloud)
#   scripts/publish-agent.sh linux-x64             only the given runtime identifiers
set -euo pipefail
cd "$(dirname "$0")/.."

version="0.0.0-local"
rids=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) version="${2:?--version needs a value}"; shift 2 ;;
    *) rids+=("$1"); shift ;;
  esac
done
[[ ${#rids[@]} -gt 0 ]] || rids=(linux-x64 linux-arm64 win-x64)

for rid in "${rids[@]}"; do
  dotnet publish src/MCPal.Agent/MCPal.Agent.csproj -c Release -r "$rid" --self-contained \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:Version="$version" \
    -o "artifacts/agent/$rid"
done
