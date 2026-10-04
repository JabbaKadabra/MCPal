#!/usr/bin/env bash
# Tests install.sh without root, systemd or a real bridge: everything goes below a temporary root and the service steps are skipped.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

archive="$work/archive"
root="$work/root"
mkdir -p "$archive" "$root"
printf '#!/bin/sh\necho stub bridge\n' > "$archive/mcpal-bridge"
chmod 755 "$archive/mcpal-bridge"
echo '{ "mcpal": { "url": "https://example.test" } }' > "$archive/mcpal.example.json"
cp "$here/mcpal-bridge.service" "$here/install.sh" "$archive/"

fail() { echo "FAIL: $*" >&2; exit 1; }
mode() { stat -c '%a' "$1"; }
run_install() { MCPAL_INSTALL_ROOT="$root" MCPAL_SKIP_SERVICE=1 "$archive/install.sh" "$@" > "$work/out.txt" 2>&1 || { cat "$work/out.txt" >&2; fail "install.sh failed"; }; }

# First install: files, modes and the API key.
run_install --api-key mcpal_test_key
[[ -x "$root/opt/mcpal/mcpal-bridge" ]] || fail "binary missing or not executable"
[[ -f "$root/etc/mcpal/mcpal.json" ]] || fail "config missing"
[[ "$(mode "$root/etc/mcpal/mcpal.json")" == "640" ]] || fail "config mode is $(mode "$root/etc/mcpal/mcpal.json"), want 640"
[[ "$(mode "$root/etc/mcpal/bridge.env")" == "600" ]] || fail "env file mode is $(mode "$root/etc/mcpal/bridge.env"), want 600"
grep -q '^MCPAL_API_KEY=mcpal_test_key$' "$root/etc/mcpal/bridge.env" || fail "API key not written to the env file"
[[ -f "$root/etc/systemd/system/mcpal-bridge.service" ]] || fail "unit missing"
grep -q '^Type=notify$' "$root/etc/systemd/system/mcpal-bridge.service" || fail "unit is not Type=notify"

# Second install: an edited config and env file survive, the binary is replaced.
echo '{ "edited": true }' > "$root/etc/mcpal/mcpal.json"
echo 'MCPAL_API_KEY=edited' > "$root/etc/mcpal/bridge.env"
printf '#!/bin/sh\necho new stub\n' > "$archive/mcpal-bridge"
run_install --api-key mcpal_other_key
grep -q 'edited' "$root/etc/mcpal/mcpal.json" || fail "existing config was overwritten"
grep -q '^MCPAL_API_KEY=edited$' "$root/etc/mcpal/bridge.env" || fail "existing env file was overwritten"
grep -q 'new stub' "$root/opt/mcpal/mcpal-bridge" || fail "binary was not replaced"
grep -q 'was not changed' "$work/out.txt" || fail "no hint that the API key was ignored"

# Unpacked files missing: refuse.
rm "$archive/mcpal.example.json"
if MCPAL_INSTALL_ROOT="$root" MCPAL_SKIP_SERVICE=1 "$archive/install.sh" > "$work/out.txt" 2>&1; then fail "install.sh accepted an incomplete archive"; fi
grep -q 'Missing mcpal.example.json' "$work/out.txt" || fail "no clear message for a missing file"

# Without the skip flag a non-root user is refused.
if [[ "$(id -u)" -ne 0 ]]; then
  if "$archive/install.sh" > "$work/out.txt" 2>&1; then fail "install.sh ran without root"; fi
  grep -q 'as root' "$work/out.txt" || fail "no clear message for a non-root run"
fi

echo "install.sh tests passed"
