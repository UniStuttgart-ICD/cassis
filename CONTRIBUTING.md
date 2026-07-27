# Contributing

## Prerequisites

- Rhino 8
- .NET 8 SDK

## Setup

```bash
git clone https://github.com/UniStuttgart-ICD/cassis.git
cd cassis
dotnet build -f net8.0-windows src/Cassis/Cassis.csproj
```

The built plugin deploys to `%APPDATA%\Grasshopper\Libraries`.

## Project Structure

- `src/Cassis/` -- plugin source (component, transport, tools)
- `src/Cassis/Tools/` -- MCP tool implementations grouped by category
- `src/Cassis/Tools/GrasshopperPrompts.cs` -- MCP prompt definitions
- `tests/` -- unit and integration tests

## Coding Conventions

- PascalCase for public members
- Nullable reference types enabled
- 4-space indentation
- Wrap tool logic in `McpExtensions.SafeExecuteAsync()` for consistent error handling

## Adding a Tool

Register your tool in the appropriate category under `Tools/`. See the README for detailed instructions.

## Testing

Run the Rhino-independent transport tests:

```bash
dotnet test tests/ModelContextProtocol.HttpListener.Tests/ModelContextProtocol.HttpListener.Tests.csproj
```

The Grasshopper test project requires a local Rhino 8 installation:

```bash
dotnet test tests/Cassis.Tests/Cassis.Tests.csproj -f net48
```

## Pull Request Process

1. Fork the repo and create a feature branch
2. Use [Conventional Commits](https://www.conventionalcommits.org/) (`feat:`, `fix:`, `docs:`)
3. Keep PRs focused -- one feature or fix per PR
4. Run the transport tests and the Rhino-hosted tests relevant to your change
