MCPal agent @VERSION@ (@RID@)
===============================

The agent runs inside your network and opens an outbound connection to the MCPal cloud. No inbound port is needed.

Install (as a systemd service):

    sudo ./install.sh --api-key mcpal_...     # use an agent key from the portal (API keys > used by: an agent)
    sudo nano /etc/mcpal/mcpal.json           # cloud URL and your mcpServers, then: sudo systemctl restart mcpal-agent

The script creates the user "mcpal", installs the binary to /opt/mcpal, keeps an existing /etc/mcpal/mcpal.json, writes
secrets to /etc/mcpal/agent.env (mode 600), and starts the service only when "mcpal-agent check" passes.

Try it without a service:

    ./mcpal-agent check --config mcpal.json    # start the local servers and list their tools
    MCPAL_API_KEY=mcpal_... ./mcpal-agent run --config mcpal.json

Secrets: mcpal.json may refer to environment variables as ${NAME}; put them into /etc/mcpal/agent.env.

The unit runs with ProtectSystem=strict and ProtectHome=yes. A local stdio server that needs other paths: sudo systemctl edit
mcpal-agent and add ReadWritePaths= / ReadOnlyPaths= (see the comments in mcpal-agent.service).

Status for monitoring: set "statusFile" in mcpal.json, then run "mcpal-agent status --config /etc/mcpal/mcpal.json".
Uninstall: sudo ./uninstall.sh [--purge]
