# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.3.2] - 2026-07-27

### Fixed

- The MCP listener remains active while concurrent requests trigger Grasshopper recomputations
- Failed listener starts release their server instance so the component can be started again
- Unexpected listener shutdowns record the reason and stack trace in the Cassis diagnostic report

## [1.3.1] - 2026-07-27

### Added

- Tool rows in the Grasshopper panel show their MCP descriptions on hover
- Release builds create a multi-target Rhino 8 Yak package alongside the ZIP archive

### Changed

- Public project, test, and code namespaces consistently use the Cassis name

## [1.3.0] - 2026-07-24

### Added

- `Manage_NamedViews` tool for listing, restoring, adding, renaming, and deleting Rhino named views
- Compact tools for opening, closing, and inspecting Grasshopper documents
- Component keywords and input/output descriptions in component information responses

### Fixed

- Release builds now include the plugin-local `System.Text.Json.dll` required by the MCP runtime
- Removed the unused console logging provider and its dependency from the plugin package
- C# script tool descriptions now explain Rhino 8's lowercase `a` output and link to the Grasshopper C# guide
- Agent installation instructions now install the complete `Cassis.zip` contents and identify the MCP transport as HTTP
- The HTTP listener now rejects non-loopback prefixes and browser origins
- The .NET Framework dependency graph uses the patched `Microsoft.Bcl.Memory` release
- The transport's .NET Standard dependency graph uses the patched `Microsoft.Bcl.Memory` release
- OpenCode setup instructions use the current `opencode.json` MCP format

### Changed

- Public builds exclude internal research integration modules
- Repository cleanup removed editor-specific configuration and an unused logo while preserving agent installation skills
- README language and tool documentation are more restrained and no longer rely on stale tool counts
- Release archives include the project license and third-party notices
- The published release and installation skill are documented as Windows-only

## [1.2.0] - 2026-07-03

### Added

- `Orbit_Object` viewport tool for framing preview geometry, orbiting the camera, and capturing the view
- Component state tools: `Set_Component_Enabled`, `Set_Component_Preview`, and `Get_Component_State`
- MCP hang regression probes in the Python tester

### Fixed

- CORS handling for non-HTTP origins such as `vscode-file://`
- RhinoCode type-hint lookup no longer throws `AmbiguousMatchException`
- HTTP transport and UI dispatch paths are less likely to hang under concurrent MCP traffic
- The side-branch transport file had committed conflict markers; these are removed
- `UiThreadHelper` now compiles for `net48`

### Changed

- Public URLs now point at the GitHub.com repository
- Release install docs now call out that `Cassis.zip` includes required DLL files

## [1.1.0] - 2026-03-24

### Added

- `GetSolutionState` tool for polling canvas solution completion (enabled by default)
- `Phase` field on `GetDetailedComponentInfoById` response showing component computation state
- `Edit_Script` tool for surgical edits on Python and C# script components
- `cassis-setup` installation skill for AI agents
- `Add_Script_Parameter` tool for creating script inputs and outputs
- `Remove_Script_Parameter` tool for deleting script parameters by index
- `Get_Parameter_TypeHints` tool for discovering valid script-parameter type hints

### Fixed

- Transport shutdown lifecycle: `StopAsync` now runs correctly during disposal
- Listen loop catches `HttpListenerException` and `ObjectDisposedException` during shutdown
- `RestartTransport` and all shutdown paths use async disposal instead of blocking `Task.Run().Wait()`
- `ExpireSolution` throttled to max 2/sec to prevent UI churn from MCP message traffic
- Ship dependency DLLs alongside `.gha` (removed Costura.Fody)
- Legacy script parameter type hints now use `IGH_TypeHint` instances from the parameter's `Hints` collection
- `Get_Panel_Text` now supports `format=raw|structured` and reliably reads connected panel data
- `List_Panels` now prefers live `VolatileData` for connected panels
- `Set_Panel_Text` accepts `items` for disconnected multiline panel text

## [1.0.0] - 2026-03-03

### Added

- MCP tools for components, scripts, connections, panels, analytics, grouping, documents, canvas, viewport, solver, and diagnostics
- HTTP/SSE transport for MCP protocol
- Per-tool filtering with safe defaults (13 tools enabled by default)
- 7 AI prompts for common Grasshopper workflows
- Custom Grasshopper component UI with dark theme
- Health check diagnostics
- Windows support for Rhino 8
