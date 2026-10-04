MCPal bridge @VERSION@ (@RID@)
===============================

The bridge runs inside your network and opens an outbound connection to the MCPal server. No inbound port is needed.

Install (as a systemd service):

    sudo ./install.sh --enroll mcpale_...     # the one-time code from the portal's Setup page
    # or: sudo ./install.sh --api-key mcpal_...   # a bridge key you created in the portal (API keys)
    sudo nano /etc/mcpal/mcpal.json           # the MCPal server URL
    sudo nano /etc/mcpal/mcp.json             # your local MCP servers (see below), then: sudo systemctl restart mcpal-bridge

The script creates the user "mcpal", installs the binary to /opt/mcpal, keeps an existing /etc/mcpal/mcpal.json and
/etc/mcpal/mcp.json, writes secrets to /etc/mcpal/bridge.env (mode 600; the key of --enroll goes to /etc/mcpal/credentials.json, mode 640), and starts the service only when "mcpal-bridge
check" passes.

Your local MCP servers go into mcp.json next to mcpal.json, in the format of Claude Code's .mcp.json: copy your existing
file (a "mcpServers" block with "command"/"args"/"env" or "url"/"headers" per server). MCPal-only settings per server
(includeTools, excludeTools, userContext, userTokenHeader) go into "serverOptions" in mcpal.json. "mcpal.mcpServersFile"
names another file. Servers can also stay inline in mcpal.json ("mcpServers"); a name must be in only one place.

Try it without a service:

    ./mcpal-bridge check --config mcpal.json    # start the local servers (mcp.json next to it) and list their tools
    MCPAL_API_KEY=mcpal_... ./mcpal-bridge run --config mcpal.json

Secrets: mcpal.json may refer to environment variables as ${NAME}; put them into /etc/mcpal/bridge.env.

The unit runs with ProtectSystem=strict and ProtectHome=yes. A local stdio server that needs other paths: sudo systemctl edit
mcpal-bridge and add ReadWritePaths= / ReadOnlyPaths= (see the comments in mcpal-bridge.service).

Status for monitoring: set "statusFile" in mcpal.json, then run "mcpal-bridge status --config /etc/mcpal/mcpal.json".
Uninstall: sudo ./uninstall.sh [--purge]
