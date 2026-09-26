---
name: cassis-setup
description: Install or connect the Cassis MCP plugin for Grasshopper (Rhino 8) on Windows or macOS and configure the MCP connection in an AI coding tool. Use when user says "install cassis", "set up cassis", "configure cassis MCP", "launch cassis", or "set up grasshopper AI". Supports Codex, Claude Code, VS Code Copilot, Cursor, OpenCode, and Windsurf.
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

Consult [references/mcp-config-guide.md](references/mcp-config-guide.md) for the exact config format and file location for the detected framework.

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

## Step 4: Start Grasshopper (server auto-starts)

MCP starts automatically when Grasshopper loads Cassis (all tools enabled). No
canvas component is required.

1. Restart Rhino if it was open during install
2. Open Grasshopper (`_Grasshopper`)
3. Wait briefly for the server at `http://localhost:3003/mcp/`

**From this repository**, if MCP is down, run the launch helper instead of asking
the user to click around:

```bash
./scripts/launch-cassis.sh
```

Windows PowerShell:

```powershell
./scripts/launch-cassis.ps1
```

The script is idempotent: if MCP is already healthy it exits 0 immediately.
If MCP is down but Rhino or port 3003 is stuck, it quits stale Rhino / kills
leftover listeners, then relaunches. Set `CASSIS_SKIP_CLEANUP=1` to disable that.
On macOS it also searches `/Volumes/Storage/00_Applications/` for Rhino 8.

Optional UI: place the **Cassis** component for Start/Stop and tool toggles.
Right-click → uncheck **Auto-start MCP when Grasshopper loads** to opt out
(applies on next Grasshopper load). With auto-start off, place Cassis and click
**Start Server**.

4. Test the connection (e.g. ask the AI to "list all components on the Grasshopper canvas")

## Troubleshooting

### Plugin not loading
- Reinstall Cassis through `_PackageManager`
- Restart Rhino after installation
- Ensure Rhino 8 (not 7 or earlier)

### MCP server not starting
- Confirm Grasshopper is open (auto-start runs on GH load)
- Check that port 3003 is not already in use
- If Auto-start was disabled, place Cassis and click **Start Server**, or re-enable Auto-start in the component menu
- Look at the Cassis component (if present) for error messages

### Connection refused from AI tool
- Confirm Grasshopper is open and MCP auto-started (or Start Server was clicked)
- Verify the MCP URL is exactly `http://localhost:3003/mcp/` (trailing slash required)
- Check the AI tool's MCP server status (e.g., `/mcp` in Claude Code)
- From this repo, run `./scripts/launch-cassis.sh` (or `.ps1` on Windows)

### Script/tool errors
- Use the `get_csharp_script_errors` or `get_python_script_errors` tools to diagnose
