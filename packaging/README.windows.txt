MCPal agent @VERSION@ (@RID@)
===============================

The agent runs inside your network and opens an outbound connection to the MCPal cloud. No inbound port is needed.

Install (as a Windows service), in an elevated PowerShell:

    .\install.ps1 -ApiKey mcpal_...          # use an agent key from the portal (API keys > used by: an agent)
    notepad C:\ProgramData\MCPal\mcpal.json   # cloud URL and your mcpServers, then: Restart-Service MCPalAgent

The script copies the binary to C:\Program Files\MCPal, keeps an existing C:\ProgramData\MCPal\mcpal.json, restricts that
directory to Administrators, SYSTEM and the service account (the config can hold secrets), registers the service with
restart-on-failure, and starts it only when "mcpal-agent check" passes.

The binary is not code signed unless the release says so; Windows SmartScreen may warn when you run it from a download.

Try it without a service:

    .\mcpal-agent.exe check --config mcpal.json
    $env:MCPAL_API_KEY = 'mcpal_...'; .\mcpal-agent.exe run --config mcpal.json

Uninstall: .\uninstall.ps1 [-RemoveData]
