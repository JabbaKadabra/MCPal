#!/usr/bin/env bash
# Packs a published agent (scripts/publish-agent.sh) with its config example and install script into
# artifacts/release/mcpal-agent-<version>-<rid>.tar.gz (Linux) or .zip (Windows).
#
#   scripts/package-agent.sh 1.1.0 linux-x64
set -euo pipefail
cd "$(dirname "$0")/.."

version="${1:?usage: package-agent.sh <version> <rid>}"
rid="${2:?usage: package-agent.sh <version> <rid>}"
published="artifacts/agent/$rid"
[[ -d "$published" ]] || { echo "Run scripts/publish-agent.sh --version $version $rid first." >&2; exit 1; }

name="mcpal-agent-$version-$rid"
stage="artifacts/stage/$name"
out="artifacts/release"
rm -rf "$stage"
mkdir -p "$stage" "$out"

if [[ "$rid" == win-* ]]; then
  cp "$published/MCPal.Agent.exe" "$stage/mcpal-agent.exe"
  cp packaging/windows/install.ps1 packaging/windows/uninstall.ps1 "$stage/"
else
  cp "$published/MCPal.Agent" "$stage/mcpal-agent"
  chmod 755 "$stage/mcpal-agent"
  cp packaging/linux/install.sh packaging/linux/uninstall.sh packaging/linux/mcpal-agent.service "$stage/"
  chmod 755 "$stage/install.sh" "$stage/uninstall.sh"
fi
cp src/MCPal.Agent/mcpal.example.json "$stage/"
sed "s/@VERSION@/$version/g; s/@RID@/$rid/g" "packaging/README.$([[ "$rid" == win-* ]] && echo windows || echo linux).txt" > "$stage/README.txt"

rm -f "$out/$name.tar.gz" "$out/$name.zip"
if [[ "$rid" == win-* ]]; then
  (cd artifacts/stage && zip -qr "../release/$name.zip" "$name")
  echo "$out/$name.zip"
else
  tar -C artifacts/stage -czf "$out/$name.tar.gz" "$name"
  echo "$out/$name.tar.gz"
fi
