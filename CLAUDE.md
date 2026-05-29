# CLAUDE.md — repo conventions for AI agents

Shared tooling for our .NET MCP servers. Ships two `dotnet tool`s, consumed by the MCP repos
via `.config/dotnet-tools.json`:
- **`Mcp.ToolsDoc`** (`src/Mcp.ToolsDoc`, command `mcp-toolsdoc`) — config-driven (`toolsdoc.json`)
  generator of a Markdown tool reference from `[McpServerToolType]` / `[McpServerTool]` /
  `[Description]` attributes (Roslyn, syntax-only), with a `--check` CI mode.
- **`Mcp.I18nCheck`** (`src/Mcp.I18nCheck`, command `mcp-i18ncheck`) — config-less bilingual-docs
  gate (EN canonical + RU counterpart; `.i18nignore` / `.i18npairs`). This is the **canonical**
  implementation of our docs-i18n gate; consuming repos call it via `dotnet tool` rather than a
  copied `check-translations.sh`.

## Documentation — bilingual, enforced by CI

**English is canonical; every doc has a Russian counterpart.**

- Repo root, `plugins/*`, `examples/*`, `servers/*`, `infra/`: `X.md` (EN) + `X.ru.md` (RU).
- Under `docs/`: `docs/<path>.md` (EN) + `docs/ru/<path>.md` (RU).

When you add or change a doc, **update BOTH languages** — same content; translate only prose,
keep code/commands/paths/links byte-identical. The gate `scripts/check-translations.sh`
(workflow `.github/workflows/docs-i18n.yml`) fails CI if an English doc lacks its Russian
counterpart, or a Russian file is a stub (< 200 bytes). English-only / agent files →
`.i18nignore`; odd-location pairs → `.i18npairs`. This gate is the same one the consuming MCP
repos use (this repo is its canonical source — copy `scripts/check-translations.sh` verbatim).

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
