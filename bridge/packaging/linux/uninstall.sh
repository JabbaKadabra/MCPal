#!/usr/bin/env bash
# Removes the MCPal bridge service and binary. The configuration (/etc/mcpal) and state (/var/lib/mcpal) stay unless --purge is given.
set -euo pipefail

purge=0
[[ "${1:-}" == "--purge" ]] && purge=1
[[ "$(id -u)" -eq 0 ]] || { echo "Run this script as root." >&2; exit 1; }

systemctl disable --now mcpal-bridge.service 2>/dev/null || true
rm -f /etc/systemd/system/mcpal-bridge.service
systemctl daemon-reload
rm -rf /opt/mcpal
if [[ "$purge" -eq 1 ]]; then
  rm -rf /etc/mcpal /var/lib/mcpal
  userdel mcpal 2>/dev/null || true
  echo "Removed the bridge, its configuration and the mcpal user."
else
  echo "Removed the bridge. Kept /etc/mcpal and /var/lib/mcpal (use --purge to delete them)."
fi
