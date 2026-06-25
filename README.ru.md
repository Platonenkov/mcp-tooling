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

## `Mcp.FleetLint` — кросс-репозиторный гейт консистентности

**.NET-тул**, который проверяет каждый MCP-репо против канонического **fleet-инвентаря**
([`fleet-lint.json`](fleet-lint.json) в корне этого репо). Ловит класс typo и config-drift'а,
которые локальные тесты увидеть не могут — они переходят границу репо.

Инвентарь — единственный source of truth для: OAuth scope каждого MCP, канонического
`https://<host>/mcp` hostname, OAuth callback port'а его Claude Code плагина, и hostname
authorization-сервера. Downstream-consumer'ы тянут этот файл в CI через
`https://raw.githubusercontent.com/Platonenkov/mcp-tooling/main/fleet-lint.json`.

Per-repo проверки (консистентность каждого MCP с инвентарём):
- **Hostname consistency** — каждая строка `*.staticbit.io` в коммитнутых файлах должна
  быть либо канонический host самого репо, либо AS host, либо другого MCP во флоте.
  Всё остальное флагается с Levenshtein-based «did you mean?» подсказкой.
- **callbackPort** — `oauth.callbackPort` в `.mcp.json` плагин-манифестах Claude Code
  должен совпадать с каноническим портом для MCP этого репо.
- **OAuth scope** — `OAuth.RequiredScope` в `appsettings*.json` должен совпадать с
  инвентарём.
- **AS hostname typos** — любая `auth.*` ссылка с edit distance ≤ 3 от канонического
  AS hostname, но не равная ему точно, флагается. Третьесторонние `auth.example.com`
  ссылки оставляются в покое.

Репо НЕ в инвентаре (`mcp-tooling`, `mcp-auth`, или любой третьесторонний consumer)
получают чистый pass: каждая проверка — no-op.

```bash
dotnet tool install Mcp.FleetLint
dotnet tool run mcp-fleetlint            # write-mode: список issues + summary
dotnet tool run mcp-fleetlint --check    # CI: ненулевой код выхода при любой issue
```

```yaml
# .github/workflows/fleet-lint.yml — тонкий caller переиспользуемого workflow
on: { push: { branches: [main] }, pull_request: { branches: [main] } }
jobs:
  fleetlint:
    uses: Platonenkov/mcp-tooling/.github/workflows/fleet-lint.yml@main
```

## `Mcp.SkillLint` — кросс-плагиновый гейт перекрытия SKILL.md триггеров

**.NET-тул**, который обходит `plugins/*/skills/*/SKILL.md` в вызывающем репо, извлекает
закавыченные триггер-фразы из поля `description` YAML-frontmatter и флагает пересечения
между плагинами. Ловит класс багов, когда Claude Code plugin loader подтягивает не тот
плагин (или сразу оба) на запрос — потому что два плагина в одном репо, как правило
cloud-vs-local пара (`xrpl-cloud` / `xrpl-local`, `telegram-bot` / `telegram-user`,
`x-mcp-cloud` / `x-mcp-local`), объявили один и тот же естественно-языковой триггер.

Проверки:
- **Conflicts (errors)** — идентичный триггер-keyword (case-insensitive, whitespace-collapsed)
  в двух или более SKILL.md разных плагинов, если только не разрешён whitelist'ом.
- **Near-overlaps (warnings)** — Levenshtein distance ≤ 2 между триггерами из разных плагинов
  (по умолчанию `nearOverlapMinLength` = 5 символов). Выявляет typo-level дубликаты и
  near-miss'ы, где автор, скорее всего, *хотел* поделиться. Warnings никогда не роняют CI.

Репо без `plugins/*/skills/*/SKILL.md` (например, сам `mcp-tooling`, третьесторонние
consumer'ы) проходят чисто — каждая проверка no-op.

Опциональный `skilllint.json` в корне репо:

```jsonc
{
  // Триггер-ключевые-слова, осознанно разделяемые двумя или более плагинами. Подавляет
  // conflict-ошибку, когда триггер присутствует ровно в перечисленных плагинах (и только в них).
  "sharedTriggers": [
    { "trigger": "telegram", "plugins": ["telegram-bot", "telegram-user"] }
  ],
  // Имена папок плагинов, которые полностью пропускать. Используйте для архивных плагинов.
  "excludePlugins": [],
  // Максимальный Levenshtein-distance для near-overlap warnings (по умолчанию 2).
  "nearOverlapDistance": 2,
  // Минимальная длина триггера, чтобы вообще рассматривать near-overlap (по умолчанию 5;
  // короче — шум).
  "nearOverlapMinLength": 5
}
```

```bash
dotnet tool install Mcp.SkillLint
dotnet tool run mcp-skilllint            # write-mode: список конфликтов + warnings + summary
dotnet tool run mcp-skilllint --check    # CI: ненулевой код выхода при любом неразрешённом конфликте
```

```yaml
# .github/workflows/skill-lint.yml — тонкий caller переиспользуемого workflow
on: { push: { branches: [main] }, pull_request: { branches: [main] } }
jobs:
  skilllint:
    uses: Platonenkov/mcp-tooling/.github/workflows/skill-lint.yml@main
```

## `Mcp.InjectionGuard` — Roslyn-гейт защиты от prompt-injection

**.NET-тул**, который статически сканирует каждый `[McpServerTool]`-метод в вызывающем
репо и утверждает, что пользовательский контент (HTTP-тела, JSON от сторонних API, вывод
тулов) обёрнут через `UntrustedContent.Wrap(...)` или `UntrustedContent.WrapJson(...)`
прежде чем возвращён. Парится с хелпером `UntrustedContent` из SDK
`Mcp.Auth.ResourceServer` — гейт syntax-only и pattern-матчит вызов независимо от того,
где живёт хелпер, поэтому работает на этапе rollout, когда не каждое репо ещё подцепило
SDK.

Правила классификации (в порядке применения):
- **Opt-in** — `[ExternalContent("origin-hint")]` на методе → wrap обязателен.
- **Opt-out** — `[NotExternalContent]` на методе → освобождение (например, status-/config-тулы).
- **Per-method exemption** — `injectionguard.json:exempt: ["Method", "Type.Method"]`.
- **Эвристика** (если ни один атрибут не сработал) — консервативная; префикс имени метода
  (`Get|Read|Search|List|Find|Fetch|Resolve` плюс `extraNamePrefixes`) И нескалярный тип
  возврата, ИЛИ тип возврата сам по себе — типичный носитель внешнего контента (`string` /
  `JObject` / `JArray` / `IReadOnlyList<...>` / `object`), ИЛИ тело метода вызывает
  известный фрагмент внешнего API (`*.GetAsync`, `*.SendRequestAsync`, `*.ExecuteAsync`,
  `*.InvokeAsync`, `*.QueryAsync`, плюс `extraInvocationFragments`). Тулы, возвращающие
  `Task<bool>` / `Task<int>` / другие скаляры, никогда не классифицируются как external.

Аудит каждого `return` принимает: `throw`, `null`, константные литералы, `return` внутри
`catch`-блока, `return`, корень которого — параметр метода. Всё остальное должно
синтаксически уходить в wrap-вызов — напрямую, через обёрнутую локальную, обёрнутый
object initializer, обёрнутый тернарник или обёрнутый null-coalesce.

Репо без `src/**/Tools/*.cs` (например, сам `mcp-tooling`, третьесторонние consumer'ы)
проходят чисто — каждая проверка no-op.

Опциональный `injectionguard.json` в корне репо:

```jsonc
{
  // Glob-паттерны относительно корня репо. По умолчанию: src/**/Tools/*.cs.
  "include": ["src/**/Tools/*.cs", "servers/*/src/**/Tools/*.cs"],
  // Per-method exemption list. Матчится либо по простому имени метода, либо по Type.Method.
  "exempt": ["GetStatus", "AuthTool.WhoAmI"],
  // Дополнительные reader-style префиксы имён методов (встроенные всегда соблюдаются).
  "extraNamePrefixes": ["Dump", "Export"],
  // Дополнительные подстроки в invocation-выражениях, классифицирующие метод как external.
  "extraInvocationFragments": ["ResolveSecretAsync"]
}
```

```bash
dotnet tool install Mcp.InjectionGuard
dotnet tool run mcp-injectionguard            # write-mode: список находок + summary
dotnet tool run mcp-injectionguard --check    # CI: ненулевой код выхода при любом необёрнутом return
```

```yaml
# .github/workflows/injection-guard.yml — тонкий caller переиспользуемого workflow
on: { push: { branches: [main] }, pull_request: { branches: [main] } }
jobs:
  injectionguard:
    uses: Platonenkov/mcp-tooling/.github/workflows/injection-guard.yml@main
```

## `security-audit` — гейт безопасности Actions (zizmor + actionlint)

Бестулзовый переиспользуемый workflow, который статически аудитит собственные `.github/workflows/`
репозитория на класс **pwn-request / Actions-injection / supply-chain** (семейство "untrusted input"
от GitHub Security Lab): незапиненные сторонние actions, инъекция `github.event.*` в шаблоны, опасные
триггеры, избыточные permissions. Запускает [zizmor](https://github.com/zizmorcore/zizmor)
(`--min-severity high`, заточен под этот класс) и [actionlint](https://github.com/rhysd/actionlint).

Канон политики — `.github/zizmor.yml`, копируется дословно в каждый репо (та же модель, что
`fleet-lint.json`): GitHub-owned (`actions/*`, `github/*`) и свои reusable (`Platonenkov/mcp-tooling/*`)
можно пинить на тег/ref; любой сторонний action обязан быть запинен на полный commit-SHA. `shellcheck`
намеренно отключён (`actionlint -shellcheck=`) — он флагает пред­существующие стилевые ниты в
`run:`-блоках, которые вне scope гейта безопасности Actions. Сторонние actions запинены на SHA и
обновляются конфигом Dependabot `github-actions`.

```yaml
# .github/workflows/security-audit.yml — thin caller of the reusable workflow
on:
  workflow_dispatch:
  pull_request: { paths: ['.github/workflows/**', '.github/zizmor.yml'] }
  push: { branches: [main], paths: ['.github/workflows/**', '.github/zizmor.yml'] }
permissions: { contents: read }
jobs:
  audit:
    permissions: { contents: read }
    uses: Platonenkov/mcp-tooling/.github/workflows/security-audit.yml@main
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

Версия каждого тула живёт в его csproj (`src/Mcp.ToolsDoc`, `src/Mcp.I18nCheck`,
`src/Mcp.LinkCheck`, `src/Mcp.FleetLint`, `src/Mcp.SkillLint`, `src/Mcp.InjectionGuard`).
Пуш тега `v X.Y.Z` пакует
**все** и публикует на nuget.org через `.github/workflows/publish.yml` (`--skip-duplicate`,
поэтому неизменённые версии — no-op; требуется секрет `NUGET_API_KEY`). Перед тегом —
бампнуть нужный csproj `<Version>`.

Лицензия: MIT.
