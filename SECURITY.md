# Security Policy

## Supported Versions

| Version | Supported |
|---------|-----------|
| 1.3.x   | Yes       |
| 1.2.x and earlier | No |

## Reporting a Vulnerability

If you discover a security issue:

1. **Sensitive issues** -- use **Report a vulnerability** on the repository's Security tab. If that option is unavailable while the repository is private, contact a repository administrator through the project's existing private channel. Do not open a public issue.
2. **Non-sensitive issues** -- open a [GitHub Issue](../../issues).

We aim to acknowledge reports within 7 days and provide a fix or mitigation plan within 30 days.

## Scope

Cassis is a Grasshopper plugin that runs locally inside Rhino. It is not a web service. The HTTP transport accepts loopback addresses only, limits request bodies to 4 MiB, and limits concurrent requests. Network-exposed deployments are not supported.

The local server has no application-level authentication. Any process on the same Windows machine can call tools with Rhino's privileges, including tools that edit documents and scripts. Do not run Cassis on a shared or untrusted machine or session.
