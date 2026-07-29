# Package Manager to MCP client setup

## Question

How should Cassis tell Rhino Package Manager users that installing the Grasshopper
plug-in is only the first half of setup, and help them configure their AI client?

## Conclusion

Use a layered setup:

1. Make Yak's `description` and `url` point users to a short, dedicated setup page.
2. Put copy-ready instructions for each supported client on that page.
3. Keep the repository-hosted `cassis-setup` Agent Skill as an optional automation
   path, linked from the same page.
4. Repeat the setup link inside the Cassis component until an MCP client connects.

The Package Manager note is the essential discovery mechanism. The skill is a
supplement, not a replacement, because a Yak-only user has not cloned the repository
where agents discover project skills.

## Findings

### Yak can show a description and direct users to documentation

McNeel's documented Yak manifest has four required fields (`name`, `version`,
`authors`, and `description`) and three recommended fields (`url`, `keywords`, and
`icon`). The description may be long-form YAML. McNeel explicitly says that `url`
may point to tutorials or any other information about the plug-in
([Yak manifest reference](https://developer.rhino3d.com/en/guides/yak/the-package-manifest/)).

The documented schema does not define release notes, install notes, a post-install
message, lifecycle hooks, or commands that run after installation. Rhino's Package
Manager documentation describes discovery, install, automatic update, and uninstall,
but no post-install instruction surface
([Rhino PackageManager](https://docs.mcneel.com/rhino/8/help/en-us/commands/packagemanager.htm)).
Accordingly, Cassis should not depend on an undocumented Yak hook.

Practical use of the available fields:

```yaml
description: >
  MCP server for Grasshopper. After installation, configure your AI client and
  connect it to http://localhost:3003/mcp/. Setup instructions:
  https://github.com/UniStuttgart-ICD/cassis/blob/main/docs/agent-setup.md
url: https://github.com/UniStuttgart-ICD/cassis/blob/main/docs/agent-setup.md
```

The exact copy should stay short because the Package Manager is a discovery surface,
not the full manual. Even if its UI truncates text or does not link URLs inside the
description, the dedicated `url` field remains the canonical route out.

### MCP client configuration is host-specific

MCP standardizes the client-server protocol and Streamable HTTP transport, but the
host application owns the client connection and user experience
([MCP architecture](https://modelcontextprotocol.io/docs/learn/architecture),
[Streamable HTTP transport](https://modelcontextprotocol.io/specification/2025-06-18/basic/transports)).
There is no cross-client local configuration file that Yak can install once for every
AI application. The setup page therefore needs per-client instructions.

First-party client documentation supports a simple HTTP URL workflow:

- Codex supports Streamable HTTP and stores connections in `~/.codex/config.toml`;
  its desktop app and IDE also expose an **Add server** UI
  ([OpenAI Codex MCP documentation](https://developers.openai.com/codex/mcp)).
- Claude Code supports `claude mcp add --transport http <name> <url>` and user,
  project, and local scopes
  ([Claude Code MCP documentation](https://code.claude.com/docs/en/mcp)).
- VS Code supports **MCP: Add Server**, user-profile or workspace `mcp.json`, and
  HTTP entries with `type` and `url`
  ([VS Code MCP documentation](https://code.visualstudio.com/docs/agent-customization/mcp-servers)).

For Cassis, user scope is the most convenient default because Rhino is installed for
the user and the same server is useful from different working folders. Project scope
is a reasonable alternative for users who only want Cassis enabled in selected
projects. The guide should explain that the connection will be unavailable while
Rhino, Grasshopper, or the Cassis server is not running.

The setup page should start with the invariant information before client-specific
tabs or sections:

```text
Transport: Streamable HTTP
URL: http://localhost:3003/mcp/
Authentication: none
```

Then provide one copyable command or configuration block per supported client, plus
a verification step. The existing
[`mcp-config-guide.md`](../../skills/cassis-setup/references/mcp-config-guide.md)
already contains most of this material and should remain the technical source for the
client-specific examples.

### A repository skill is valuable, but it does not solve discovery by itself

Agent Skills are a portable open format built around a `SKILL.md` file
([Agent Skills specification](https://agentskills.io/specification)). VS Code
documents project skills under `.github/skills/`, `.claude/skills/`, and
`.agents/skills/`, and user skills under the corresponding home-directory locations
([VS Code Agent Skills](https://code.visualstudio.com/docs/agent-customization/agent-skills)).

This makes the existing Cassis setup skill a good way to:

- identify the user's client;
- write the correct client configuration;
- guide the Rhino-side setup;
- verify the connection; and
- keep framework-specific detail out of the short Yak description.

However, project skills are discovered from a repository/workspace. Installing a
`.yak` does not place the skill in the user's AI-client skill directory. A link to a
skill on GitHub is therefore useful only after a human or agent follows that link and
installs or reads it. The setup page should offer both:

- **Manual setup:** choose a client and copy one command/configuration block.
- **Agent-guided setup:** install or ask the agent to follow the canonical
  `cassis-setup/SKILL.md`.

Keep one canonical skill and generate or verify the client-specific mirror paths.
Otherwise their setup instructions can drift.

### The public MCP Registry is not the right primary path

The official MCP Registry is still in preview. It accepts metadata for publicly
accessible remote servers or installation methods hosted in supported public package
registries
([MCP Registry overview](https://modelcontextprotocol.io/registry/about)).
The remote-server rules explicitly require the URL to be publicly accessible
([publishing remote servers](https://modelcontextprotocol.io/registry/remote-servers)).

Cassis is a loopback server started inside Rhino and distributed through Yak, so
`http://localhost:3003/mcp/` is not a public remote service and Yak is not a
documented Registry package type. Registry publication should not be the onboarding
plan unless Cassis later gains a supported public launcher package or a public remote
service.

## Recommended implementation

### 1. Create one stable setup page

Add `docs/agent-setup.md` and use it as the canonical human entry point. Keep the
first screen short:

1. Install Cassis in Rhino.
2. Restart Rhino and open Grasshopper.
3. Place Cassis and start the server.
4. Configure the selected AI client for `http://localhost:3003/mcp/`.
5. Verify by listing the Grasshopper canvas components.

Below that, include Codex, Claude Code, VS Code, Cursor, OpenCode, Windsurf, and a
generic Streamable HTTP section. Link to the setup skill as the automated option.
A GitHub Pages site could improve presentation later, but a version-controlled
Markdown page is sufficient and easier to maintain now.

### 2. Change Yak metadata in the next release

Update `manifest.yml` so:

- `description` says that agent-side setup is required, includes the endpoint, and
  names the setup page;
- `url` links directly to `docs/agent-setup.md`, not the repository home page.

This reaches users at the point where the missing step occurs without relying on new
Yak features.

### 3. Add an in-product reminder

Package metadata is read before installation and is easy to forget after restarting
Rhino. Until Cassis has observed an MCP client connection, show a compact message in
the component such as:

```text
Server ready at http://localhost:3003/mcp/
Agent setup: <short setup URL>
```

A context-menu action to **Copy MCP URL** and **Open agent setup** would remove
typing errors. This is more dependable than an automatic post-install action and
does not modify another application's configuration without consent.

### 4. Retain the skill as optional automation

Keep `skills/cassis-setup/` and the standard discovery mirrors, but link it from the
setup page and describe how to install or ask an agent to follow it. Add a check that
the mirrors match the canonical skill.

## Tradeoffs

| Option | Strength | Limitation | Decision |
| --- | --- | --- | --- |
| Yak description and URL | Reaches every Package Manager user at install time | Limited space; may be overlooked after restart | Required |
| Dedicated setup page | Stable, linkable, supports every client | User must leave Rhino | Required |
| Repository Agent Skill | Can perform and verify setup for compatible agents | Not auto-discovered by Yak-only users; client support varies | Recommended supplement |
| In-component setup link | Appears exactly when the server is used | Requires a small product change | Strongly recommended |
| Automatic client config | Lowest number of clicks | Surprising cross-application writes, different client formats and scopes | Do not implement |
| MCP Registry | Good marketplace discovery for supported public servers/packages | Cassis is loopback-only and Yak is unsupported | Not currently applicable |

## Acceptance criteria

- A new user can discover from Rhino Package Manager that agent-side setup is
  required.
- Package Manager links to one stable setup page.
- The setup page has copy-ready configuration and verification steps for every
  supported client.
- The page offers the setup skill as an optional automated path.
- Cassis itself exposes the endpoint and setup link before the first successful
  client connection.
- No installer or plug-in silently edits third-party AI-client configuration.
