#!/usr/bin/env bash
# Publishes MCPal.Agent as a self-contained single file for Linux and Windows into ./artifacts/agent/<rid>.
set -euo pipefail
cd "$(dirname "$0")/.."
for rid in linux-x64 win-x64; do
  dotnet publish src/MCPal.Agent/MCPal.Agent.csproj -c Release -r "$rid" --self-contained \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "artifacts/agent/$rid"
done
