# Политика безопасности

>  🌐 **Язык**: [English](SECURITY.md) | **Русский**

В этом репозитории живут общие `dotnet tool`'ы, которые потребляются по всему MCP-флоту
(`Mcp.ToolsDoc`, `Mcp.I18nCheck`, `Mcp.LinkCheck`, `Mcp.FleetLint`, `Mcp.SkillLint`,
`Mcp.InjectionGuard`), плюс переиспользуемые GitHub Actions workflow'ы, которые downstream
MCP-репо вызывают как `uses:`. Уязвимость в любом тулзе или workflow имеет fleet-wide blast
radius.

## Поддерживаемые версии

Security-фиксы получает только **последняя опубликованная версия** каждого тула на nuget.org.
LTS-ветки нет — флот-репо ожидают всегда последний тег.

Reusable workflow'ы (`.github/workflows/*.yml`, на которые downstream вызывает
`uses: <owner>/mcp-tooling/.github/workflows/<file>@main`) всегда отражают текущее состояние
`main`. Мы не пиним workflow-ссылки на теги в downstream'е; security-фиксы распространяются
сразу как только лендятся на `main`.

## Модель угроз

Тулзы и workflow'ы в этом репо работают в двух контекстах:

1. **CI на consumer-репо** — каждый push и pull-request в один из MCP-репо запускает эти тулзы.
   Компрометация могла бы заложить malicious code в fleet-wide build pipeline или
   эксфильтрировать `GITHUB_TOKEN`/per-job секреты consumer-репо.
2. **Developer workstation** — мейнтейнеры запускают тулзы локально через `dotnet tool` против
   своего worktree. Компрометация могла бы читать/изменять local source tree.

Реалистичные attack vectors против которых защищаемся:

- **Supply chain через nuget.org publish** — credential для publish (`NUGET_API_KEY` GitHub
  secret) только у мейнтейнеров репо. Тег `vX.Y.Z` — единственный путь, который публикует;
  per-push публикации нет.
- **Supply chain через транзитивные зависимости** — каждая зависимость в `*.csproj`
  ревьюится в PR; мы предпочитаем Microsoft-owned пакеты (`Microsoft.CodeAnalysis.*`,
  `Markdig`, `Polly`) niche-пакетам с одним мейнтейнером.
- **Malicious reusable workflow** — `main` защищён; PR-review обязателен; force-push'ей нет.

Против чего **не** защищаемся:

- Компрометация maintainer-аккаунта одновременно с repo write access И секретом
  `NUGET_API_KEY`. Это уровень компромиссии, который требует out-of-band митигации
  (account recovery, key rotation).
- Insider-атаки от самого мейнтейнера.
- Компрометация nuget.org или GitHub.

## Сообщить об уязвимости

**Не открывайте публичный issue.** Используйте GitHub private security advisory:

1. Откройте <https://github.com/Platonenkov/mcp-tooling/security/advisories/new>
2. Заполните advisory: шаги воспроизведения, затронутые версии, предлагаемое исправление.

Мы стремимся подтвердить получение в течение 72 часов и выпустить fix в течение 14 дней для
high-severity issues.

## Для контрибьюторов

При добавлении нового тула или модификации существующего:

- **Roslyn syntax-only**, без MSBuild workspace. Уменьшает attack surface — мы никогда не
  загружаем произвольный код или `.csproj` MSBuild логику, только парсим текст.
- **Никаких filesystem write'ов вне явно переданного `--repo-root`** (или worktree для тестов).
- **Никаких сетевых вызовов.** Все тулзы в этом репо — чистые local-tree анализаторы.
- **Никакого `Process.Start` внешних бинарей.** Если будущая фича потребует subprocess —
  обоснуйте в PR.
- **Валидируйте consumer-supplied JSON config** (`toolsdoc.json`, `fleet-lint.json`,
  `skilllint.json`, `injectionguard.json`). Path-traversal и glob-injection атаки должны
  ловиться: пути из config считайте untrusted.

При добавлении нового reusable workflow:

- **Pin-аем SHA** для любого third-party action (`actions/setup-dotnet@<sha>`), не floating tag.
- **Минимизируем `permissions:` блок** — просим только то, что workflow реально использует.
- **Никогда `pull_request_target`**, если без него никак, и никогда на default-branch
  permissions. `pull_request_target` запускается на BASE ref с секретами BASE-ветки — код из
  PR форка может хайджекнуть этот токен.

## Связанные документы

- [`CLAUDE.md`](CLAUDE.md) — конвенции репо для AI-агентов, работающих в этом codebase
- [Phase 4.4 rollout discussion](https://github.com/Platonenkov/mcp-tooling/pull/10) — контекст
  по дизайну `Mcp.InjectionGuard` и fleet-wide prompt-injection защите
