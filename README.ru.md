# mcp-tooling

>  🌐 **Язык**: [English](README.md) | **Русский**

Общий инструментарий для наших .NET-серверов [Model Context Protocol](https://modelcontextprotocol.io).

## `Mcp.ToolsDoc` — генератор справочника инструментов

Config-driven **.NET-тул**, который генерирует Markdown-справочник инструментов MCP-сервера
из атрибутов `[McpServerToolType]` / `[McpServerTool]` / `[Description]` (Roslyn, только
синтаксис — без сборки, <1с), и режим `--check`, который роняет CI, когда закоммиченный
документ расходится с кодом. Переиспользуется на любом .NET-сервере ModelContextProtocol.

### Установка (в каждом репозитории-потребителе)

Добавить в локальный tool-манифест:

```bash
dotnet new tool-manifest        # если ещё нет .config/dotnet-tools.json
dotnet tool install Mcp.ToolsDoc
```

### Конфигурация — `toolsdoc.json` в корне репо

```jsonc
{
  // Одна секция на MCP-сервер. toolsDir — относительно корня репо.
  "servers": [
    {
      "id": "telegram-bot",
      "displayName": "telegram-bot",
      "toolsDir": "servers/telegram-bot/src/TelegramMCP/Tools",
      "blurb": "Cloud bot-API MCP server."
    }
  ],
  "generatedOutput": "docs/TOOLS.generated.md",
  // опционально: держать маркеры <!-- toolcount:NAME -->N<!-- /toolcount:NAME --> в синхроне
  "markerFiles": ["README.md", "docs/INSTALL.md"],
  // опционально: hand-curated cheatsheet, в котором должен упоминаться каждый инструмент
  "cheatsheet": "docs/TOOLS.md"
}
```

Обязателен только `servers`. `markerFiles` и `cheatsheet` — по желанию.

### Запуск

```bash
dotnet tool run mcp-toolsdoc            # --write (по умолчанию): (пере)генерировать доки на месте
dotnet tool run mcp-toolsdoc --check    # CI: ненулевой код выхода при рассинхроне
```

Опции: `--config <path>` (по умолчанию `<repo-root>/toolsdoc.json`), `--repo-root <path>`
(по умолчанию — git-корень от текущей директории).

### Интеграция в CI

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

Сгенерированный `TOOLS.generated.md` — только на английском; если в репо есть гейт
двуязычности, добавьте этот файл в его ignore-список.

## `Mcp.I18nCheck` — гейт двуязычности

Config-less **.NET-тул**, который в CI обеспечивает нашу конвенцию двуязычности: английский —
каноничный; у каждого английского дока должна быть русская пара, и русский файл не должен быть
заглушкой (≥ 200 байт).

- `docs/<path>.md` ↔ `docs/ru/<path>.md` (зеркало).
- `X.md` ↔ `X.ru.md` (суффикс) для корня репо, `plugins/*`, `examples/**`, `servers/*`, `infra/`.

Исключения — корневой **`.i18nignore`** (EN-only / генерируемые / агент-файлы, по пути на
строку). Пары вне стандартных мест — **`.i18npairs`** (`en:ru` на строку).

```bash
dotnet tool install Mcp.I18nCheck
dotnet tool run mcp-i18ncheck            # ненулевой код выхода при отсутствии пары/заглушке
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

## Релизы

Версия каждого тула живёт в его csproj (`src/Mcp.ToolsDoc`, `src/Mcp.I18nCheck`). Пуш тега
`v X.Y.Z` пакует **оба** и публикует на nuget.org через `.github/workflows/publish.yml`
(`--skip-duplicate`, поэтому неизменённые версии — no-op; требуется секрет `NUGET_API_KEY`).
Перед тегом — бампнуть нужный csproj `<Version>`.

Лицензия: MIT.
