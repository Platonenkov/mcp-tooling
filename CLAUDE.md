# CLAUDE.md — repo conventions for AI agents

Shared tooling for our .NET MCP servers. **This repo is public** (it hosts reusable workflows
that repos under two different GitHub owners must call, which GitHub only allows from a public
host). Keep everything here repo-agnostic — no internal hostnames, server paths, or fleet
specifics in docs/examples; use generic placeholders (`my-mcp`, `src/MyMcp.Server/Tools`,
`my-mcp.example.com`). It ships two `dotnet tool`s, consumed by the MCP repos via
`.config/dotnet-tools.json`:
- **`Mcp.ToolsDoc`** (`src/Mcp.ToolsDoc`, command `mcp-toolsdoc`) — config-driven (`toolsdoc.json`)
  generator of a Markdown tool reference from `[McpServerToolType]` / `[McpServerTool]` /
  `[Description]` attributes (Roslyn, syntax-only), with a `--check` CI mode.
- **`Mcp.I18nCheck`** (`src/Mcp.I18nCheck`, command `mcp-i18ncheck`) — config-less bilingual-docs
  gate (EN canonical + RU counterpart; `.i18nignore` / `.i18npairs`). This is the **canonical**
  implementation of our docs-i18n gate; consuming repos call it via `dotnet tool` rather than a
  copied `check-translations.sh`.

It also hosts two **reusable GitHub Actions workflows** (`.github/workflows/`), referenced by
consumers as `uses: <owner>/mcp-tooling/.github/workflows/<file>@main`:
- **`docker-build-push.yml`** — build + push multi-arch ghcr.io images from a JSON image matrix
  (optional `github_token` build-secret for private GH Packages restore).
- **`deploy-vps.yml`** — ship a published image to a VPS over an SSH pipe into a forced-command
  `deploy.sh` (no registry login on the host), then smoke-test a healthz URL.

## Documentation — bilingual, enforced by CI

**English is canonical; every doc has a Russian counterpart.**

- Repo root, `plugins/*`, `examples/*`, `servers/*`, `infra/`: `X.md` (EN) + `X.ru.md` (RU).
- Under `docs/`: `docs/<path>.md` (EN) + `docs/ru/<path>.md` (RU).

When you add or change a doc, **update BOTH languages** — same content; translate only prose,
keep code/commands/paths/links byte-identical. The gate is the `Mcp.I18nCheck` tool itself
(workflow `.github/workflows/docs-i18n.yml` runs it from local build here — no self-dependency
on the published package). It fails CI if an English doc lacks its Russian counterpart, or a
Russian file is a stub (< 200 bytes). English-only / agent files → `.i18nignore`; odd-location
pairs → `.i18npairs`. Consuming MCP repos run the published tool via `dotnet tool`.

## The tool — `Mcp.ToolsDoc`

- Keep it **config-driven** and repo-agnostic: nothing repo-specific belongs in the code — it
  all goes in the consumer's `toolsdoc.json` (servers + output/marker/cheatsheet paths).
- Roslyn syntax-only (no build, no MSBuild workspace) so it stays fast and dependency-light.
  Attribute matching tolerates `Foo` / `FooAttribute` / namespaced spellings; string args are
  statically folded (literals + `+` concatenation + simple interpolation).
- The generated `TOOLS.generated.md` is English-only by design (consumers list it in their
  `.i18nignore`).
- Validate changes against a real repo, e.g.:
  `dotnet run --project src/Mcp.ToolsDoc -- --repo-root <repo> --config <cfg.json>`.

## Releases

Version is in `src/Mcp.ToolsDoc/Mcp.ToolsDoc.csproj` (`<Version>`). Pushing a `v X.Y.Z` tag
publishes to nuget.org (`publish.yml`, secret `NUGET_API_KEY`). Bump the version on any
behavior/format change (the rendered output is committed in consumers, so format changes ripple
into their `--check` gate — coordinate the bump with regenerating their docs).
