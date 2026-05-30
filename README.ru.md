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
      "id": "my-mcp",
      "displayName": "my-mcp",
      "toolsDir": "src/MyMcp.Server/Tools",
      "blurb": "Example MCP server."
    }
  ],
  "generatedOutput": "docs/TOOLS.generated.md",
  // опционально: держать маркеры <!-- toolcount:NAME -->N<!-- /toolcount:NAME --> в синхроне
  // Соглашение: включайте сюда SKILL.md каждого плагина, чтобы headline-счёт
  // его инструментов оставался актуальным. <!-- toolcount:total --> = сумма
  // всех серверов; <!-- toolcount:<server-id> --> = счёт одного сервера.
  "markerFiles": [
    "README.md",
    "docs/INSTALL.md",
    "plugins/<plugin>/skills/<skill>/SKILL.md"
  ],
  // опционально: hand-curated cheatsheet, в котором должен упоминаться каждый инструмент
  "cheatsheet": "docs/TOOLS.md"
}
```

Обязателен только `servers`. `markerFiles` и `cheatsheet` — по желанию.
**Кросс-репозиторное соглашение**: каждый `SKILL.md` плагина, в чьём теле
упоминается количество инструментов, должен быть в `markerFiles` — чтобы его
headline оставался актуальным автоматически, а CI-гейт `--check` ловил drift.
SKILL.md руками поддерживают триггер-ключевые-слова для агента и per-tool
intent-таблицы; headline tool-count — единственная часть, которую *можно*
авто-подставлять, и теперь подставляется.

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
      - uses: actions/checkout@v5
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
      - uses: actions/checkout@v5
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '10.0.x' }
      - run: dotnet tool restore
      - run: dotnet tool run mcp-i18ncheck
```

## `Mcp.LinkCheck` — гейт целостности markdown-ссылок

Config-less **.NET-тул**, который валидирует каждую ссылку `[text](path)` и
`[text](path#anchor)` в `.md` файлах репо против живой файловой системы и
GitHub-flavored heading slug'ов. Ловит устаревшие relative-пути, опечатки в anchor'ах
после переименования heading, битые cross-repo ссылки через
`https://github.com/<owner>/<repo>/blob/main/...` URL'ы.

- **Внутренние пути**: `[..](docs/INSTALL.md)` → проверяет существование файла
  относительно директории содержащего markdown'а.
- **Anchor'ы**: `[..](#быстрый-старт)` и `[..](docs/X.md#section)` → слаг сверяется с
  заголовками через GitHub-алгоритм (`text.downcase.gsub(/[^\p{Word}\- ]/u, '').tr(' ', '-')`).
  HTML-anchor'ы `<a name>` / `id=` тоже распознаются.
- **Same-repo GitHub URL'ы**: автоопределяются из `git remote get-url origin` (настраиваемо).
- **External `http(s)://`**: пропускаются по умолчанию. Opt-in через
  `linkcheck.json:checkExternalLinks=true`.

Опциональный `linkcheck.json` в корне репо:

```jsonc
{
  // Glob-паттерны .md файлов, которые надо скипать. bin/, obj/, node_modules/, .git/
  // и docs/TOOLS.generated.md пропускаются автоматически.
  "excludePaths": ["docs/historical/**/*.md"],
  "checkExternalLinks": false,
  // Anchor ID'ы, которые принимать даже без matching-heading'а — для legacy HTML-anchor'ов,
  // которые не slug'ятся чисто. Использовать аккуратно.
  "allowedAnchors": ["legacy-id"]
}
```

```bash
dotnet tool install Mcp.LinkCheck
dotnet tool run mcp-linkcheck            # write-mode: список битых ссылок + summary
dotnet tool run mcp-linkcheck --check    # CI: ненулевой код выхода при любой битой ссылке
```

```yaml
# .github/workflows/docs-links.yml — тонкий caller переиспользуемого workflow
on: { push: { branches: [main] }, pull_request: { branches: [main] } }
jobs:
  linkcheck:
    uses: Platonenkov/mcp-tooling/.github/workflows/docs-links.yml@main
```

## Переиспользуемые CI/CD workflow

Два [переиспользуемых GitHub Actions workflow](https://docs.github.com/actions/using-workflows/reusing-workflows)
позволяют всем потребляющим репозиториям шарить одно определение того, как образы собираются,
публикуются и деплоятся — caller'ы остаются тонкими и не расходятся между собой. Они лежат под
`.github/workflows/` здесь и подключаются через `uses: <owner>/mcp-tooling/.github/workflows/<file>@main`.

### `docker-build-push.yml` — сборка + пуш ghcr.io образов

Собирает один или несколько multi-arch образов из JSON-матрицы и пушит каждый как
`ghcr.io/<owner-lowercase>/<image_suffix>` с тегами `:<version>` и `:latest`.

```yaml
# .github/workflows/docker.yml в потребляющем репозитории
on:
  push: { tags: ['v*.*.*'] }
  workflow_dispatch:
    inputs:
      version: { description: 'Версия без ведущего v', required: true, type: string }
jobs:
  build-push:
    permissions: { contents: read, packages: write }
    uses: <owner>/mcp-tooling/.github/workflows/docker-build-push.yml@main
    with:
      version: ${{ inputs.version != '' && inputs.version || github.ref_name }}
      images: |
        [
          { "name": "my-mcp", "image_suffix": "my-mcp",
            "dockerfile": "./Dockerfile", "context": ".", "cache_scope": "my-mcp" }
        ]
      # use_gh_packages_secret: true   # если Dockerfile тянет приватный GH Packages feed
    # secrets:
    #   gh_packages_token: ${{ secrets.GITHUB_TOKEN }}
```

| input | смысл |
|-------|-------|
| `version` | тег образа; ведущий `v` срезается; дополнительно пушится `:latest` |
| `images` | JSON-массив `{name, image_suffix, dockerfile, context, cache_scope}` (по записи на образ) |
| `platforms` | платформы buildx (по умолчанию `linux/amd64,linux/arm64`) |
| `use_gh_packages_secret` | смонтировать `github_token` как build-secret для приватного NuGet restore |

### `deploy-vps.yml` — доставка образа на VPS по SSH

Доставляет опубликованный образ на хост **без логина в registry на хосте**: runner тянет образ,
`docker save | ssh` стримит тарбол в forced-command `deploy.sh`, тег приходит как SSH-команда
(повторно валидируется на сервере), затем runner смоук-тестит healthz URL.

```yaml
# .github/workflows/deploy.yml в потребляющем репозитории
on:
  workflow_dispatch:
    inputs:
      tag: { description: 'Тег образа (semver или latest)', required: true, type: string, default: latest }
jobs:
  deploy:
    uses: <owner>/mcp-tooling/.github/workflows/deploy-vps.yml@main
    with:
      image_suffix: my-mcp
      tag: ${{ inputs.tag }}
      healthz_url: https://my-mcp.example.com/healthz
    secrets:
      deploy_ssh_key: ${{ secrets.DEPLOY_SSH_KEY }}
      deploy_host: ${{ secrets.DEPLOY_HOST }}
      deploy_user: ${{ secrets.DEPLOY_USER }}
      deploy_known_hosts: ${{ secrets.DEPLOY_KNOWN_HOSTS }}
```

Требование на хосте (на каждый сервис): CI deploy-ключ, привязанный к forced command в
`~/.ssh/authorized_keys` хоста, чтобы ключ мог запускать только deploy-скрипт и ничего больше:

```
command="/opt/<name>/deploy.sh",no-port-forwarding,no-X11-forwarding,no-agent-forwarding,no-pty ssh-ed25519 AAAA... ci-deploy
```

`deploy.sh` валидирует тег, `docker load`-ит пришедший тарбол, пинит его в compose `.env`,
пересоздаёт контейнер и ждёт healthcheck. Референс-скрипт лежит в каждом потребляющем
репозитории под `deploy/deploy.sh`.

## Релизы

Версия каждого тула живёт в его csproj (`src/Mcp.ToolsDoc`, `src/Mcp.I18nCheck`). Пуш тега
`v X.Y.Z` пакует **оба** и публикует на nuget.org через `.github/workflows/publish.yml`
(`--skip-duplicate`, поэтому неизменённые версии — no-op; требуется секрет `NUGET_API_KEY`).
Перед тегом — бампнуть нужный csproj `<Version>`.

Лицензия: MIT.
