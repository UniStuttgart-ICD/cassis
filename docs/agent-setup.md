# Connect an AI client to Cassis

Installing Cassis from Rhino Package Manager installs the Grasshopper side only.
Your AI client must also be connected to Cassis.

## Start Cassis

1. Restart Rhino after installing or updating Cassis.
2. Open Grasshopper. MCP **auto-starts** on load (all tools enabled) at
   `http://localhost:3003/mcp/`.
3. Configure your AI client with:

   - Transport: Streamable HTTP
   - URL: `http://localhost:3003/mcp/`
   - Authentication: none

The server is available while Rhino and Grasshopper are running. You do **not**
need a Cassis component on the canvas for MCP to listen.

### Optional: control panel and opt-out

Place the **Cassis** component for Start/Stop UI and tool toggles. Right-click it
and uncheck **Auto-start MCP when Grasshopper loads** to opt out (applies the next
time Grasshopper loads; does not stop a running server). With auto-start off,
place the component and click **Start Server**.

### Agent launch helper

From a clone of this repo, agents can bring Rhino + Grasshopper up and wait for
MCP:

```bash
./scripts/launch-cassis.sh
```

Windows:

```powershell
./scripts/launch-cassis.ps1
```

If MCP is already up, the script exits immediately.

## Configure your AI client

### Codex

Run:

```bash
codex mcp add cassis --url http://localhost:3003/mcp/
```

Or add this to `~/.codex/config.toml`:

```toml
[mcp_servers.cassis]
url = "http://localhost:3003/mcp/"
```

You can also open Codex settings, select **MCP servers**, and add a Streamable HTTP
server with the URL above.

### Claude Code

Run:

```bash
claude mcp add --transport http --scope user cassis http://localhost:3003/mcp/
```

Use `--scope local` instead if Cassis should be available only in the current
project.

### VS Code with GitHub Copilot

[Install Cassis in VS Code](vscode:mcp/install?%7B%22name%22%3A%22cassis%22%2C%22type%22%3A%22http%22%2C%22url%22%3A%22http%3A%2F%2Flocalhost%3A3003%2Fmcp%2F%22%7D)

Or create `.vscode/mcp.json`:

```json
{
  "servers": {
    "cassis": {
      "type": "http",
      "url": "http://localhost:3003/mcp/"
    }
  }
}
```

### Cursor

Create `.cursor/mcp.json` in the project or `~/.cursor/mcp.json` for all projects:

```json
{
  "mcpServers": {
    "cassis": {
      "type": "url",
      "url": "http://localhost:3003/mcp/"
    }
  }
}
```

### OpenCode

Add this to `opencode.json`:

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "cassis": {
      "type": "remote",
      "url": "http://localhost:3003/mcp/",
      "enabled": true
    }
  }
}
```

### Windsurf

Add this to `~/.codeium/windsurf/mcp_config.json`:

```json
{
  "mcpServers": {
    "cassis": {
      "serverUrl": "http://localhost:3003/mcp/"
    }
  }
}
```

### Other MCP clients

Use these values:

```text
Name: cassis
Transport: Streamable HTTP
URL: http://localhost:3003/mcp/
Authentication: none
```

See the
[detailed configuration reference](../skills/cassis-setup/references/mcp-config-guide.md)
for client documentation and file locations.

## Let an agent handle setup

Copy this prompt into your AI client:

> Cassis is already installed through Rhino Package Manager. Read and follow
> https://raw.githubusercontent.com/UniStuttgart-ICD/cassis/main/skills/cassis-setup/SKILL.md

The setup skill detects the client, adds the appropriate MCP configuration with your
permission, and guides connection verification. It does not reinstall Cassis when
the Package Manager installation already exists.

## Verify the connection

With the Cassis server running, ask your AI client:

> List all components on the Grasshopper canvas.

If the client cannot connect, confirm that:

- Grasshopper is open (MCP auto-starts unless Auto-start was disabled);
- the URL includes the trailing slash: `http://localhost:3003/mcp/`;
- another process is not already using port `3003`; and
- the client has reloaded its MCP configuration.

Cassis accepts connections only from the local machine and has no application-level
authentication. Use it only on a trusted machine and session.
