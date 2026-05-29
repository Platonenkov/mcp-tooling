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

## Releasing

`Mcp.ToolsDoc` version lives in `src/Mcp.ToolsDoc/Mcp.ToolsDoc.csproj` (`<Version>`). Pushing a
`v X.Y.Z` tag publishes that version to nuget.org via `.github/workflows/publish.yml`
(requires the repo secret `NUGET_API_KEY`).

License: MIT.
