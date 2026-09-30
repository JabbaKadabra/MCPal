MCPal bridge @VERSION@ (@RID@)
===============================

The bridge runs inside your network and opens an outbound connection to the MCPal server. No inbound port is needed.

Install (as a systemd service):

    sudo ./install.sh --api-key mcpal_...     # use a bridge key from the portal (API keys > used by: a bridge)
    sudo nano /etc/mcpal/mcpal.json           # MCPal server URL and your mcpServers, then: sudo systemctl restart mcpal-bridge

The script creates the user "mcpal", installs the binary to /opt/mcpal, keeps an existing /etc/mcpal/mcpal.json, writes
secrets to /etc/mcpal/bridge.env (mode 600), and starts the service only when "mcpal-bridge check" passes.

Try it without a service:

    ./mcpal-bridge check --config mcpal.json    # start the local servers and list their tools
    MCPAL_API_KEY=mcpal_... ./mcpal-bridge run --config mcpal.json

Secrets: mcpal.json may refer to environment variables as ${NAME}; put them into /etc/mcpal/bridge.env.

The unit runs with ProtectSystem=strict and ProtectHome=yes. A local stdio server that needs other paths: sudo systemctl edit
mcpal-bridge and add ReadWritePaths= / ReadOnlyPaths= (see the comments in mcpal-bridge.service).

Status for monitoring: set "statusFile" in mcpal.json, then run "mcpal-bridge status --config /etc/mcpal/mcpal.json".
Uninstall: sudo ./uninstall.sh [--purge]
