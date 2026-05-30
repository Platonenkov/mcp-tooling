# Security Policy

>  🌐 **Language**: **English** | [Русский](SECURITY.ru.md)

This repository hosts shared `dotnet tool`s consumed across the MCP fleet (`Mcp.ToolsDoc`,
`Mcp.I18nCheck`, `Mcp.LinkCheck`, `Mcp.FleetLint`, `Mcp.SkillLint`, `Mcp.InjectionGuard`) plus
the reusable GitHub Actions workflows that downstream MCP repos call. A vulnerability in any
tool here has fleet-wide blast radius.

## Supported versions

Only the **latest published version** of each tool on nuget.org receives security fixes. There
is no LTS support track — the fleet repos are expected to consume the latest tag.

Reusable workflows in this repository (`.github/workflows/*.yml` that downstream repos call via
`uses: <owner>/mcp-tooling/.github/workflows/<file>@main`) always reflect the current `main`
branch state. We never tag-pin workflow references downstream; security fixes propagate the
moment they land on `main`.

## Threat model

The tools and workflows in this repo run in two contexts:

1. **CI on consumer repos** — every push and pull-request to one of the MCP repos runs these
   tools. A compromise here could plant malicious code in fleet-wide build pipelines or exfil
   the consumer repos' `GITHUB_TOKEN` / per-job secrets.
2. **Developer workstations** — maintainers run the tools locally via `dotnet tool` against
   their working tree. A compromise could read or modify the local source tree.

The realistic attack vectors we defend against:

- **Supply-chain through nuget.org publish** — only repository maintainers have the publish
  credential (`NUGET_API_KEY` GitHub secret). Tagging a `vX.Y.Z` release is the only path that
  publishes; no per-push publishing.
- **Supply-chain through transitive dependencies** — every dependency in `*.csproj` is reviewed
  on PR; we prefer Microsoft-owned packages (`Microsoft.CodeAnalysis.*`, `Markdig`, `Polly`) over
  niche packages with single maintainers.
- **Malicious reusable workflow** — `main` is protected; PR review required; no force-pushes.

We do **not** defend against:

- Compromise of a maintainer account with both repo write access AND the `NUGET_API_KEY`
  secret. That class of compromise requires out-of-band mitigation (account recovery, key
  rotation).
- Insider attacks by a maintainer.
- Compromise of nuget.org or GitHub itself.

## Reporting a vulnerability

**Do not open a public issue.** Use GitHub's private security advisory:

1. Go to <https://github.com/Platonenkov/mcp-tooling/security/advisories/new>
2. Fill in the advisory with reproduction steps, affected versions, and suggested remediation.

We aim to acknowledge within 72 hours and ship a fix within 14 days for high-severity issues.

## For contributors

When adding a new tool or modifying an existing one:

- **Roslyn syntax-only**, no MSBuild workspace. Reduces the attack surface — we never load
  arbitrary code or `.csproj` MSBuild logic, only parse text.
- **No filesystem writes outside the explicitly-passed `--repo-root`** (or worktree for tests).
- **No network calls.** All tools in this repo are pure local-tree analyzers.
- **No `Process.Start` of external binaries.** If a future feature needs a subprocess, justify
  it in PR.
- **Validate consumer-supplied JSON config files** (e.g. `toolsdoc.json`, `fleet-lint.json`,
  `skilllint.json`, `injectionguard.json`). Path-traversal and glob-injection attacks should be
  caught by treating config-supplied paths as untrusted.

When adding a new reusable workflow:

- **Use a pinned action SHA** for any third-party action (e.g. `actions/setup-dotnet@<sha>`),
  not a floating tag.
- **Minimize `permissions:` block** — request only what the workflow actually needs.
- **Never `pull_request_target`** unless absolutely required, and never on the default branch
  permissions. `pull_request_target` runs against the BASE ref with the BASE branch's secrets
  — code from a fork PR can hijack that token.

## Related

- [`CLAUDE.md`](CLAUDE.md) — repo conventions for AI agents working in this codebase
- [Phase 4.4 rollout discussion](https://github.com/Platonenkov/mcp-tooling/pull/10) — context
  on the `Mcp.InjectionGuard` design and the fleet-wide prompt-injection defence
