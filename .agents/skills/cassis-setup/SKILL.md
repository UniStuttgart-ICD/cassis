<!-- Canonical source: skills/cassis-setup/SKILL.md -->
---
name: cassis-setup
description: Install or connect the Cassis MCP plugin for Grasshopper (Rhino 8) on Windows or macOS and configure the MCP connection in an AI coding tool. Use when user says "install cassis", "set up cassis", "configure cassis MCP", or "set up grasshopper AI". Supports Codex, Claude Code, VS Code Copilot, Cursor, OpenCode, and Windsurf.
---

# Cassis Setup

Guides the user through installing the Cassis Grasshopper plugin and connecting it to their AI coding tool via MCP.

## Step 1: Check the Grasshopper installation

Confirm that Rhino 8 is installed on Windows or macOS. Other platforms are not
supported.

If the user says Cassis is already installed through Rhino Package Manager, or the
**Cassis** component is available in Grasshopper, do not reinstall it. Continue to
Step 2.

Otherwise, ask the user to:

1. Start Rhino
2. Run `_PackageManager`
3. Search for **Cassis**
4. Install the latest version
5. Restart Rhino

Yak installs `net8.0-windows` on Windows and `net8.0` on macOS.

## Step 2: Detect AI framework

Determine which AI tool the user is running. Check in order:

1. Running as `codex`, `$CODEX_HOME` is set, or `.codex/` exists -- **Codex**
2. Running as `claude` CLI process, or `CLAUDE.md` exists in workspace -- **Claude Code**
3. `$VSCODE_PID` is set or `.vscode/` directory exists -- **VS Code Copilot**
4. `.cursor/` directory exists or `$CURSOR_TRACE_ID` is set -- **Cursor**
5. `opencode.json` exists or `$OPENCODE_CONFIG` is set -- **OpenCode**
6. `.windsurf/` directory exists -- **Windsurf**

If detection is ambiguous, ask the user which tool they are using.

## Step 3: Configure MCP

Consult [the MCP configuration guide](../../../skills/cassis-setup/references/mcp-config-guide.md) for the exact config format and file location for the detected framework.

Cassis must be configured as an HTTP or Streamable HTTP MCP server, not stdio.

**Before writing config**, fetch the framework's latest MCP documentation if you have web access:
- Claude Code: `https://code.claude.com/docs/en/mcp`
- Codex: `https://developers.openai.com/codex/mcp`
- VS Code: `https://code.visualstudio.com/docs/copilot/customization/mcp-servers`
- Cursor: `https://cursor.com/docs/context/mcp`
- OpenCode: `https://opencode.ai/docs/mcp-servers/`

Frameworks name this transport differently (`http`, `url`, or `serverUrl`). The endpoint is always `http://localhost:3003/mcp/`.

Before changing a client configuration, show the user the planned file, scope, and
change, then get permission. Preserve existing servers and settings.

## Step 4: Verify installation

Tell the user to:
1. Restart Rhino (if it was open)
2. Open Grasshopper
3. Search for "Cassis" in the component search bar
4. Place the Cassis component on the canvas and click **Start Server**
5. Test the connection from their AI tool (e.g., ask the AI to "list all components on the Grasshopper canvas")

## Troubleshooting

### Plugin not loading
- Reinstall Cassis through `_PackageManager`
- Restart Rhino after installation
- Ensure Rhino 8 (not 7 or earlier)

### MCP server not starting
- Check that port 3003 is not already in use
- Look at the Cassis component for error messages on the canvas

### Connection refused from AI tool
- Confirm the Cassis component is placed on the Grasshopper canvas and the server is running
- Verify the MCP URL is exactly `http://localhost:3003/mcp/` (trailing slash required)
- Check the AI tool's MCP server status (e.g., `/mcp` in Claude Code)

### Script/tool errors
- Use the `get_csharp_script_errors` or `get_python_script_errors` tools to diagnose
