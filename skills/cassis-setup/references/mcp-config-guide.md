# Cassis MCP Configuration by Framework

Cassis runs an HTTP MCP server at `http://localhost:3003/mcp/`.
One URL serves every open Rhino, on Mac and Windows. Tool calls need the header `X-Cassis-Root` set to the project folder. A missing header is refused.
Configure it as HTTP or Streamable HTTP, not stdio.

## Codex

**CLI command (recommended):**
```bash
codex mcp add cassis --url http://localhost:3003/mcp/
```

**Manual config** in `~/.codex/config.toml`. Codex does not expand `${workspaceFolder}`. Replace the path with the project folder:

```toml
[mcp_servers.cassis]
url = "http://localhost:3003/mcp/"
http_headers = { "X-Cassis-Root" = "/absolute/path/to/project" }
```

Verify the connection with `codex mcp get cassis` or `/mcp`.

Docs: https://developers.openai.com/codex/mcp

## Claude Code

**CLI command (recommended):**
```bash
claude mcp add --transport http --scope local cassis http://localhost:3003/mcp/ --header "X-Cassis-Root: /absolute/path/to/project"
```

Replace the path with the project folder. Claude Code does not expand `${workspaceFolder}`. Use `--scope local` so this header stays with that project.

**Manual config** in `~/.claude.json` under `mcpServers`:
```json
{
  "mcpServers": {
    "cassis": {
      "type": "http",
      "url": "http://localhost:3003/mcp/",
      "headers": {
        "X-Cassis-Root": "/absolute/path/to/project"
      }
    }
  }
}
```

Scope options: `--scope local` (this project) or `--scope user` (every project, one folder).

Docs: https://code.claude.com/docs/en/mcp

## VS Code (GitHub Copilot)

[Install Cassis in VS Code](vscode:mcp/install?%7B%22name%22%3A%22cassis%22%2C%22type%22%3A%22http%22%2C%22url%22%3A%22http%3A%2F%2Flocalhost%3A3003%2Fmcp%2F%22%7D)

The install link does not set the header. Create or edit `.vscode/mcp.json` in the workspace root. VS Code expands `${workspaceFolder}`:
```json
{
  "servers": {
    "cassis": {
      "type": "http",
      "url": "http://localhost:3003/mcp/",
      "headers": {
        "X-Cassis-Root": "${workspaceFolder}"
      }
    }
  }
}
```

Or add the same `headers` block to user `settings.json` under `mcp.servers` for global access. A global entry can name only one folder.

Docs: https://code.visualstudio.com/docs/copilot/customization/mcp-servers

## Cursor

Create or edit `.cursor/mcp.json` in the project root (or `~/.cursor/mcp.json` for global):
```json
{
  "mcpServers": {
    "cassis": {
      "type": "url",
      "url": "http://localhost:3003/mcp/",
      "headers": {
        "X-Cassis-Root": "${workspaceFolder}"
      }
    }
  }
}
```

Docs: https://cursor.com/docs/context/mcp

## OpenCode

Add to `opencode.json` in the project root. OpenCode does not expand `${workspaceFolder}`. Replace the path with that project folder:
```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "cassis": {
      "type": "remote",
      "url": "http://localhost:3003/mcp/",
      "enabled": true,
      "headers": {
        "X-Cassis-Root": "/absolute/path/to/project"
      }
    }
  }
}
```

Docs: https://opencode.ai/docs/mcp-servers/

## Windsurf

Create or edit `~/.codeium/windsurf/mcp_config.json`. This file is global. Windsurf does not expand `${workspaceFolder}`. Replace the path with the project folder that contains the Grasshopper file:
```json
{
  "mcpServers": {
    "cassis": {
      "serverUrl": "http://localhost:3003/mcp/",
      "headers": {
        "X-Cassis-Root": "/absolute/path/to/project"
      }
    }
  }
}
```

## Generic MCP Client

Any MCP-compatible client can connect using:
- **Transport**: HTTP (Streamable HTTP)
- **URL**: `http://localhost:3003/mcp/`
- **Header**: `X-Cassis-Root` set to the project folder. Cursor and VS Code expand `${workspaceFolder}`. Other clients need the absolute folder.
- **Protocol version**: `2025-06-18`
- **Authentication**: none; Cassis trusts processes on the local machine

Do not run Cassis on a shared or untrusted machine or session. Connected clients can edit Grasshopper documents and scripts with Rhino's privileges.
