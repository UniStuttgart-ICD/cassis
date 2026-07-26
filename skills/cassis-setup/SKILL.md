---
name: cassis-setup
description: Install the Cassis MCP plugin for Grasshopper (Rhino 8) on Windows and configure the MCP connection in your AI coding tool. Use when user says "install cassis", "set up cassis", "configure cassis MCP", or "set up grasshopper AI". Supports Codex, Claude Code, VS Code Copilot, Cursor, OpenCode, and Windsurf.
---

# Cassis Setup

Guides the user through installing the Cassis Grasshopper plugin and connecting it to their AI coding tool via MCP.

## Prerequisites

- Rhino 8 installed
- `Cassis.zip` downloaded from the project's GitHub releases page

## Step 1: Locate the release archive

Ask the user where they saved `Cassis.zip`. Validate that the archive exists.

If the user hasn't downloaded it yet, direct them to the project's GitHub releases page.

## Step 2: Detect OS

Confirm that the operating system is Windows. If it is not, stop and explain that the current Cassis release is Windows-only.

## Step 3: Install the plugin

### Windows

```powershell
$archive = Resolve-Path "<USER_CASSIS_ZIP_PATH>"
$extract = Join-Path $env:TEMP ("cassis-install-" + [guid]::NewGuid())
$libraryRoot = Join-Path $env:APPDATA "Grasshopper\Libraries"
$target = Join-Path $libraryRoot "Cassis"

Expand-Archive -LiteralPath $archive -DestinationPath $extract
$source = Join-Path $extract "Cassis"
if (-not (Test-Path -LiteralPath (Join-Path $source "Cassis.gha"))) {
    throw "Cassis.zip does not contain the expected Cassis folder."
}

New-Item -ItemType Directory -Force -Path $libraryRoot | Out-Null
if (Test-Path -LiteralPath $target) {
    $backupRoot = Join-Path $env:LOCALAPPDATA "Cassis\Backups"
    New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
    $backup = Join-Path $backupRoot ("Cassis-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
    Move-Item -LiteralPath $target -Destination $backup
}
Copy-Item -LiteralPath $source -Destination $libraryRoot -Recurse
Get-ChildItem -LiteralPath $target -File | Unblock-File
```

Replace `<USER_CASSIS_ZIP_PATH>` with the archive path from Step 1. Install the complete folder: the DLL files beside `Cassis.gha` are required.

## Step 4: Detect AI framework

Determine which AI tool the user is running. Check in order:

1. Running as `codex`, `$CODEX_HOME` is set, or `.codex/` exists -- **Codex**
2. Running as `claude` CLI process, or `CLAUDE.md` exists in workspace -- **Claude Code**
3. `$VSCODE_PID` is set or `.vscode/` directory exists -- **VS Code Copilot**
4. `.cursor/` directory exists or `$CURSOR_TRACE_ID` is set -- **Cursor**
5. `opencode.json` exists or `$OPENCODE_CONFIG` is set -- **OpenCode**
6. `.windsurf/` directory exists -- **Windsurf**

If detection is ambiguous, ask the user which tool they are using.

## Step 5: Configure MCP

Consult [references/mcp-config-guide.md](references/mcp-config-guide.md) for the exact config format and file location for the detected framework.

Cassis must be configured as an HTTP or Streamable HTTP MCP server, not stdio.

**Before writing config**, fetch the framework's latest MCP documentation if you have web access:
- Claude Code: `https://code.claude.com/docs/en/mcp`
- Codex: `https://developers.openai.com/codex/mcp`
- VS Code: `https://code.visualstudio.com/docs/copilot/customization/mcp-servers`
- Cursor: `https://cursor.com/docs/context/mcp`
- OpenCode: `https://opencode.ai/docs/mcp-servers/`

Frameworks name this transport differently (`http`, `url`, or `serverUrl`). The endpoint is always `http://localhost:3003/mcp/`.

## Step 6: Verify installation

Tell the user to:
1. Restart Rhino (if it was open)
2. Open Grasshopper
3. Search for "Cassis" in the component search bar
4. Place the Cassis component on the canvas -- the MCP server starts automatically at `http://localhost:3003/mcp/`
5. Test the connection from their AI tool (e.g., ask the AI to "list all components on the Grasshopper canvas")

## Troubleshooting

### Plugin not loading
- Verify the `Cassis` folder contains `Cassis.gha` and the DLL files from `Cassis.zip`
- Do not install `Cassis.gha` by itself
- On Windows, unblock every file in the `Cassis` folder
- Ensure Rhino 8 (not 7 or earlier)

### MCP server not starting
- Check that port 3003 is not already in use: `netstat -an | findstr 3003`
- Look at the Cassis component for error messages on the canvas

### Connection refused from AI tool
- Confirm the Cassis component is placed on the Grasshopper canvas and the server is running
- Verify the MCP URL is exactly `http://localhost:3003/mcp/` (trailing slash required)
- Check the AI tool's MCP server status (e.g., `/mcp` in Claude Code)

### Script/tool errors
- Use the `get_csharp_script_errors` or `get_python_script_errors` tools to diagnose
