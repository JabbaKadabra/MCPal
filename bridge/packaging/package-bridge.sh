#!/usr/bin/env bash
# Packs a published bridge (bridge/packaging/publish-bridge.sh) with its config example and install script into
# artifacts/release/mcpal-bridge-<version>-<rid>.tar.gz (Linux) or .zip (Windows).
#
#   bridge/packaging/package-bridge.sh 1.1.0 linux-x64
set -euo pipefail
cd "$(dirname "$0")/../.."

version="${1:?usage: package-bridge.sh <version> <rid>}"
rid="${2:?usage: package-bridge.sh <version> <rid>}"
published="artifacts/bridge/$rid"
[[ -d "$published" ]] || { echo "Run bridge/packaging/publish-bridge.sh --version $version $rid first." >&2; exit 1; }

name="mcpal-bridge-$version-$rid"
stage="artifacts/stage/$name"
out="artifacts/release"
rm -rf "$stage"
mkdir -p "$stage" "$out"

if [[ "$rid" == win-* ]]; then
  cp "$published/MCPal.Bridge.exe" "$stage/mcpal-bridge.exe"
  cp bridge/packaging/windows/install.ps1 bridge/packaging/windows/uninstall.ps1 "$stage/"
else
  cp "$published/MCPal.Bridge" "$stage/mcpal-bridge"
  chmod 755 "$stage/mcpal-bridge"
  cp bridge/packaging/linux/install.sh bridge/packaging/linux/uninstall.sh bridge/packaging/linux/mcpal-bridge.service "$stage/"
  chmod 755 "$stage/install.sh" "$stage/uninstall.sh"
fi
cp bridge/MCPal.Bridge/mcpal.example.json bridge/MCPal.Bridge/mcp.example.json "$stage/"
sed "s/@VERSION@/$version/g; s/@RID@/$rid/g" "bridge/packaging/README.$([[ "$rid" == win-* ]] && echo windows || echo linux).txt" > "$stage/README.txt"

rm -f "$out/$name.tar.gz" "$out/$name.zip"
if [[ "$rid" == win-* ]]; then
  (cd artifacts/stage && zip -qr "../release/$name.zip" "$name")
  echo "$out/$name.zip"
else
  tar -C artifacts/stage -czf "$out/$name.tar.gz" "$name"
  echo "$out/$name.tar.gz"
fi
