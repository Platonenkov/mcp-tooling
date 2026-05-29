# mcp-tooling

>  🌐 **Language**: **English** | [Русский](README.ru.md)

Shared tooling for our .NET [Model Context Protocol](https://modelcontextprotocol.io) servers.

## `Mcp.ToolsDoc` — tool reference generator

A config-driven **.NET tool** that generates a Markdown tool reference for an MCP server
from its `[McpServerToolType]` / `[McpServerTool]` / `[Description]` attributes (Roslyn,
syntax-only — no build, runs in <1s), and a `--check` mode that fails CI when the committed
doc drifts from the code. Reusable across every .NET ModelContextProtocol MCP server.

### Install (per consuming repo)

Add it to a local tool manifest:

```bash
dotnet new tool-manifest        # if you don't have .config/dotnet-tools.json yet
dotnet tool install Mcp.ToolsDoc
```

### Configure — `toolsdoc.json` at the repo root

```jsonc
{
  // One section per MCP server. toolsDir is repo-relative.
  "servers": [
    {
      "id": "telegram-bot",
      "displayName": "telegram-bot",
      "toolsDir": "servers/telegram-bot/src/TelegramMCP/Tools",
      "blurb": "Cloud bot-API MCP server."
    }
  ],
  "generatedOutput": "docs/TOOLS.generated.md",
  // optional: keep <!-- toolcount:NAME -->N<!-- /toolcount:NAME --> markers in sync
  "markerFiles": ["README.md", "docs/INSTALL.md"],
  // optional: a hand-curated cheatsheet that must mention every tool by name
  "cheatsheet": "docs/TOOLS.md"
}
```

Only `servers` is required. `markerFiles` and `cheatsheet` are opt-in.

### Run

```bash
dotnet tool run mcp-toolsdoc            # --write (default): (re)generate docs in place
dotnet tool run mcp-toolsdoc --check    # CI: exit non-zero if anything is out of sync
```

Options: `--config <path>` (default `<repo-root>/toolsdoc.json`), `--repo-root <path>`
(default: the git root found from the current directory).

### CI integration

```yaml
# .github/workflows/docs-codegen.yml
on: { push: { branches: [main] }, pull_request: { branches: [main] } }
jobs:
  toolsdoc:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '10.0.x' }
      - run: dotnet tool restore
      - run: dotnet tool run mcp-toolsdoc --check
```

The generated `TOOLS.generated.md` is English-only; if your repo enforces a bilingual-docs
gate, list it in that gate's ignore file.

## `Mcp.I18nCheck` — bilingual-docs gate

A config-less **.NET tool** that enforces our bilingual-docs convention in CI: English is
canonical; every English doc must have its Russian counterpart and the Russian file must be
non-stub (≥ 200 bytes).

- `docs/<path>.md` ↔ `docs/ru/<path>.md` (mirror subtree).
- `X.md` ↔ `X.ru.md` (suffix) for the repo root, `plugins/*`, `examples/**`, `servers/*`, `infra/`.

Exemptions: a repo-root **`.i18nignore`** (English-only / generated / agent files, one
repo-relative path per line). Pairs outside the conventional locations: **`.i18npairs`**
(`en:ru` per line).

```bash
dotnet tool install Mcp.I18nCheck
dotnet tool run mcp-i18ncheck            # exit non-zero if any pair is missing/stub
```

```yaml
# .github/workflows/docs-i18n.yml
on: { push: { branches: [main] }, pull_request: { branches: [main] } }
jobs:
  bilingual-docs:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '10.0.x' }
      - run: dotnet tool restore
      - run: dotnet tool run mcp-i18ncheck
```

## Releasing

Each tool's version lives in its csproj (`src/Mcp.ToolsDoc`, `src/Mcp.I18nCheck`). Pushing a
`v X.Y.Z` tag packs **both** and publishes them to nuget.org via
`.github/workflows/publish.yml` (`--skip-duplicate`, so unchanged versions are no-ops; requires
the repo secret `NUGET_API_KEY`). Bump the relevant csproj `<Version>` before tagging.

License: MIT.
