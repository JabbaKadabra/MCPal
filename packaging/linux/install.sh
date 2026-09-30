#!/usr/bin/env bash
# Installs the MCPal agent as a systemd service. Run as root from the unpacked release archive:
#
#   sudo ./install.sh [--api-key mcpal_...]
#
# Files: /opt/mcpal/mcpal-agent (binary), /etc/mcpal/mcpal.json (config, never overwritten),
# /etc/mcpal/agent.env (MCPAL_API_KEY and other secrets, mode 600), /etc/systemd/system/mcpal-agent.service.
# The service starts only when `mcpal-agent check` passes.
#
# For tests: MCPAL_INSTALL_ROOT prefixes every path, MCPAL_SKIP_SERVICE=1 skips user, systemd and the check run.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="${MCPAL_INSTALL_ROOT:-}"
skip_service="${MCPAL_SKIP_SERVICE:-0}"
api_key="${MCPAL_API_KEY:-}"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --api-key) api_key="${2:?--api-key needs a value}"; shift 2 ;;
    -h|--help) sed -n '2,10p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done

if [[ "$skip_service" != "1" && "$(id -u)" -ne 0 ]]; then
  echo "Run this script as root (sudo ./install.sh)." >&2
  exit 1
fi
for file in mcpal-agent mcpal.example.json mcpal-agent.service; do
  [[ -f "$here/$file" ]] || { echo "Missing $file next to the install script. Run it from the unpacked archive." >&2; exit 1; }
done

bin_dir="$root/opt/mcpal"
config_dir="$root/etc/mcpal"
state_dir="$root/var/lib/mcpal"
unit_dir="$root/etc/systemd/system"
config="$config_dir/mcpal.json"
env_file="$config_dir/agent.env"

if [[ "$skip_service" != "1" ]] && ! id mcpal >/dev/null 2>&1; then
  useradd --system --home-dir /var/lib/mcpal --shell /usr/sbin/nologin mcpal
  echo "Created system user mcpal."
fi

install -d -m 755 "$bin_dir" "$unit_dir"
install -d -m 750 "$config_dir"
install -d -m 750 "$state_dir"
install -m 755 "$here/mcpal-agent" "$bin_dir/mcpal-agent"
echo "Installed $bin_dir/mcpal-agent"

if [[ -e "$config" ]]; then
  echo "Kept the existing $config."
else
  install -m 640 "$here/mcpal.example.json" "$config"
  echo "Created $config from the example. Edit it: cloud URL and your mcpServers."
fi

if [[ -e "$env_file" ]]; then
  echo "Kept the existing $env_file."
  if [[ -n "$api_key" ]]; then
    echo "The API key was not changed because $env_file exists. Edit that file to change it." >&2
  fi
else
  umask 077
  {
    echo "# Secrets of the MCPal agent, read by systemd (mode 600). mcpal.json can refer to them as \${NAME}."
    echo "MCPAL_API_KEY=$api_key"
  } > "$env_file"
  chmod 600 "$env_file"
  echo "Created $env_file."
  if [[ -z "$api_key" ]]; then
    echo "Put the API key of an agent key into $env_file (MCPAL_API_KEY=mcpal_...)."
  fi
fi

if [[ "$skip_service" != "1" ]]; then
  chown -R root:mcpal "$config_dir"
  chown -R mcpal:mcpal "$state_dir"
  chown root:root "$env_file"
fi

install -m 644 "$here/mcpal-agent.service" "$unit_dir/mcpal-agent.service"
echo "Installed $unit_dir/mcpal-agent.service"

if [[ "$skip_service" == "1" ]]; then
  echo "Service steps skipped."
  exit 0
fi

systemctl daemon-reload
systemctl enable mcpal-agent.service >/dev/null

echo "Checking the configuration and the local servers ..."
if ! (set -a; . "$env_file"; set +a; runuser -u mcpal -p -- "$bin_dir/mcpal-agent" check --config "$config"); then
  echo >&2
  echo "The check failed, so the service was not started. Fix $config (and $env_file), then run:" >&2
  echo "  sudo systemctl start mcpal-agent" >&2
  exit 1
fi

systemctl restart mcpal-agent.service
echo "MCPal agent started. Follow it with: journalctl -u mcpal-agent -f"
