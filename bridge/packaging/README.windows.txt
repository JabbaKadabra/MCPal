MCPal bridge @VERSION@ (@RID@)
===============================

The bridge runs inside your network and opens an outbound connection to the MCPal server. No inbound port is needed.

Install (as a Windows service), in an elevated PowerShell:

    .\install.ps1 -ApiKey mcpal_...          # use a bridge key from the portal (API keys > used by: a bridge)
    notepad C:\ProgramData\MCPal\mcpal.json   # MCPal server URL and your mcpServers, then: Restart-Service MCPalBridge

The script copies the binary to C:\Program Files\MCPal, keeps an existing C:\ProgramData\MCPal\mcpal.json, restricts that
directory to Administrators, SYSTEM and the service account (the config can hold secrets), registers the service with
restart-on-failure, and starts it only when "mcpal-bridge check" passes.

The binary is not code signed unless the release says so; Windows SmartScreen may warn when you run it from a download.

Try it without a service:

    .\mcpal-bridge.exe check --config mcpal.json
    $env:MCPAL_API_KEY = 'mcpal_...'; .\mcpal-bridge.exe run --config mcpal.json

Uninstall: .\uninstall.ps1 [-RemoveData]
