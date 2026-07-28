# Cassis MCP Configuration by Framework

Cassis runs an HTTP MCP server at `http://localhost:3003/mcp/`.
Configure it as HTTP or Streamable HTTP, not stdio.

## Codex

**CLI command (recommended):**
```bash
codex mcp add cassis --url http://localhost:3003/mcp/
```

**Manual config** in `~/.codex/config.toml`:
```toml
[mcp_servers.cassis]
url = "http://localhost:3003/mcp/"
```

Verify the connection with `codex mcp get cassis` or `/mcp`.

Docs: https://developers.openai.com/codex/mcp

## Claude Code

**CLI command (recommended):**
```bash
claude mcp add --transport http --scope user cassis http://localhost:3003/mcp/
```

**Manual config** in `~/.claude.json` under `mcpServers`:
```json
{
  "mcpServers": {
    "cassis": {
      "type": "http",
      "url": "http://localhost:3003/mcp/"
    }
  }
}
```

Scope options: `--scope user` (all projects), `--scope local` (current project only).

Docs: https://code.claude.com/docs/en/mcp

## VS Code (GitHub Copilot)

Create or edit `.vscode/mcp.json` in the workspace root:
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

Or add to user `settings.json` under `mcp.servers` for global access.

Docs: https://code.visualstudio.com/docs/copilot/customization/mcp-servers

## Cursor

Create or edit `.cursor/mcp.json` in the project root (or `~/.cursor/mcp.json` for global):
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

Docs: https://cursor.com/docs/context/mcp

## OpenCode

Add to `~/.config/opencode/opencode.json` or to `opencode.json` in the project root:
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

Docs: https://opencode.ai/docs/mcp-servers/

## Windsurf

Create or edit `~/.codeium/windsurf/mcp_config.json`:
```json
{
  "mcpServers": {
    "cassis": {
      "serverUrl": "http://localhost:3003/mcp/"
    }
  }
}
```

## Generic MCP Client

Any MCP-compatible client can connect using:
- **Transport**: HTTP (Streamable HTTP)
- **URL**: `http://localhost:3003/mcp/`
- **Protocol version**: `2025-06-18`
- **Authentication**: none; Cassis trusts processes on the local machine

Do not run Cassis on a shared or untrusted machine or session. Connected clients can edit Grasshopper documents and scripts with Rhino's privileges.
