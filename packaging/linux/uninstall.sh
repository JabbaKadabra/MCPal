#!/usr/bin/env bash
# Removes the MCPal agent service and binary. The configuration (/etc/mcpal) and state (/var/lib/mcpal) stay unless --purge is given.
set -euo pipefail

purge=0
[[ "${1:-}" == "--purge" ]] && purge=1
[[ "$(id -u)" -eq 0 ]] || { echo "Run this script as root." >&2; exit 1; }

systemctl disable --now mcpal-agent.service 2>/dev/null || true
rm -f /etc/systemd/system/mcpal-agent.service
systemctl daemon-reload
rm -rf /opt/mcpal
if [[ "$purge" -eq 1 ]]; then
  rm -rf /etc/mcpal /var/lib/mcpal
  userdel mcpal 2>/dev/null || true
  echo "Removed the agent, its configuration and the mcpal user."
else
  echo "Removed the agent. Kept /etc/mcpal and /var/lib/mcpal (use --purge to delete them)."
fi
