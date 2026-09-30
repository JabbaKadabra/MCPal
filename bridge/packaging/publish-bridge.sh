#!/usr/bin/env bash
# Publishes MCPal.Bridge as a self-contained single file into ./artifacts/bridge/<rid>.
#
#   bridge/packaging/publish-bridge.sh                   all platforms, version 0.0.0-local
#   bridge/packaging/publish-bridge.sh --version 1.1.0   stamps the version into the binary (the bridge reports it to the MCPal server)
#   bridge/packaging/publish-bridge.sh linux-x64         only the given runtime identifiers
set -euo pipefail
cd "$(dirname "$0")/../.."

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
  dotnet publish bridge/MCPal.Bridge/MCPal.Bridge.csproj -c Release -r "$rid" --self-contained \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:Version="$version" \
    -o "artifacts/bridge/$rid"
done
