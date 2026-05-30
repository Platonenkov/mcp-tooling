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
      "id": "my-mcp",
      "displayName": "my-mcp",
      "toolsDir": "src/MyMcp.Server/Tools",
      "blurb": "Example MCP server."
    }
  ],
  "generatedOutput": "docs/TOOLS.generated.md",
  // optional: keep <!-- toolcount:NAME -->N<!-- /toolcount:NAME --> markers in sync
  // Convention: include each plugin's SKILL.md here so its headline tool-count
  // stays accurate. <!-- toolcount:total --> = sum across all servers;
  // <!-- toolcount:<server-id> --> = a single server's count.
  "markerFiles": [
    "README.md",
    "docs/INSTALL.md",
    "plugins/<plugin>/skills/<skill>/SKILL.md"
  ],
  // optional: a hand-curated cheatsheet that must mention every tool by name
  "cheatsheet": "docs/TOOLS.md"
}
```

Only `servers` is required. `markerFiles` and `cheatsheet` are opt-in. The
**cross-repo convention** is that every plugin `SKILL.md` whose body mentions a
tool count is listed in `markerFiles`, so its headline stays accurate
automatically and the `--check` CI gate fails on drift. SKILL.md files
hand-maintain agent-trigger keywords and per-tool intent tables; the tool-count
headline is the one piece that *can* be auto-substituted, and now is.

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
      - uses: actions/checkout@v5
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
      - uses: actions/checkout@v5
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '10.0.x' }
      - run: dotnet tool restore
      - run: dotnet tool run mcp-i18ncheck
```

## `Mcp.LinkCheck` — markdown link integrity gate

A config-less **.NET tool** that validates every `[text](path)` and `[text](path#anchor)`
link in the repo's `.md` files against the live filesystem and GitHub-flavored heading
slugs. Catches stale relative paths, mis-typed anchors after a heading rename, broken
cross-repo references via `https://github.com/<owner>/<repo>/blob/main/...` URLs.

- **Internal paths**: `[..](docs/INSTALL.md)` → asserts the file exists relative to the
  containing markdown's directory.
- **Anchors**: `[..](#быстрый-старт)` and `[..](docs/X.md#section)` → slugs target headings
  with the GitHub algorithm (`text.downcase.gsub(/[^\p{Word}\- ]/u, '').tr(' ', '-')`).
  HTML `<a name>` / `id=` anchors are also recognised.
- **Same-repo GitHub URLs**: auto-detected from `git remote get-url origin` (configurable).
- **External `http(s)://`**: skipped by default. Opt-in via `linkcheck.json:checkExternalLinks=true`.

Optional `linkcheck.json` at the repo root:

```jsonc
{
  // Glob patterns of .md files to skip. bin/, obj/, node_modules/, .git/, and
  // docs/TOOLS.generated.md are skipped automatically.
  "excludePaths": ["docs/historical/**/*.md"],
  "checkExternalLinks": false,
  // Anchor IDs to accept even when no heading matches — for legacy HTML anchors that
  // don't slugify cleanly. Use sparingly.
  "allowedAnchors": ["legacy-id"]
}
```

```bash
dotnet tool install Mcp.LinkCheck
dotnet tool run mcp-linkcheck            # write-mode: lists broken links + summary
dotnet tool run mcp-linkcheck --check    # CI: exit non-zero on any broken link
```

```yaml
# .github/workflows/docs-links.yml — thin caller of the reusable workflow
on: { push: { branches: [main] }, pull_request: { branches: [main] } }
jobs:
  linkcheck:
    uses: Platonenkov/mcp-tooling/.github/workflows/docs-links.yml@main
```

## `Mcp.FleetLint` — cross-repo consistency gate

A **.NET tool** that validates each MCP repo against the canonical **fleet inventory**
([`fleet-lint.json`](fleet-lint.json) at this repo's root). Catches the class of typos and
config drift that local tests cannot see — they cross repo boundaries.

The inventory is the single source of truth for: each MCP's OAuth scope, the canonical
`https://<host>/mcp` hostname, the OAuth callback port number used by its Claude Code
plugin, and the authorization-server hostname. Downstream consumers fetch this file at
CI time via `https://raw.githubusercontent.com/Platonenkov/mcp-tooling/main/fleet-lint.json`.

Per-repo checks (each MCP's own consistency vs. the inventory):
- **Hostname consistency** — every `*.staticbit.io` string in committed files must match
  the repo's own canonical host, the AS host, or another MCP in the fleet. Anything else
  is flagged with a Levenshtein-based "did you mean?" suggestion.
- **callbackPort** — Claude Code plugin `.mcp.json` manifests' `oauth.callbackPort` must
  match the canonical port for the repo's MCP.
- **OAuth scope** — `appsettings*.json` `OAuth.RequiredScope` must match the inventory.
- **AS hostname typos** — any `auth.*` reference within edit distance 3 of the canonical
  AS hostname but not exactly equal to it is flagged. Third-party `auth.example.com`
  references are left alone.

Repos NOT in the inventory (`mcp-tooling`, `mcp-auth`, or any third-party consumer) get
a clean pass: every check is a no-op.

```bash
dotnet tool install Mcp.FleetLint
dotnet tool run mcp-fleetlint            # write-mode: lists issues + summary
dotnet tool run mcp-fleetlint --check    # CI: exit non-zero on any issue
```

```yaml
# .github/workflows/fleet-lint.yml — thin caller of the reusable workflow
on: { push: { branches: [main] }, pull_request: { branches: [main] } }
jobs:
  fleetlint:
    uses: Platonenkov/mcp-tooling/.github/workflows/fleet-lint.yml@main
```

## Reusable CI/CD workflows

Two [reusable GitHub Actions workflows](https://docs.github.com/actions/using-workflows/reusing-workflows)
let every consuming repo share one definition of how images are built, published, and deployed —
callers stay thin and never drift apart. They live under `.github/workflows/` here and are
referenced with `uses: <owner>/mcp-tooling/.github/workflows/<file>@main`.

### `docker-build-push.yml` — build + push ghcr.io images

Builds one or more multi-arch images from a JSON image matrix and pushes each as
`ghcr.io/<owner-lowercase>/<image_suffix>` with `:<version>` and `:latest`.

```yaml
# .github/workflows/docker.yml in the consuming repo
on:
  push: { tags: ['v*.*.*'] }
  workflow_dispatch:
    inputs:
      version: { description: 'Version without leading v', required: true, type: string }
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
      # use_gh_packages_secret: true   # when the Dockerfile restores a private GH Packages feed
    # secrets:
    #   gh_packages_token: ${{ secrets.GITHUB_TOKEN }}
```

| input | meaning |
|-------|---------|
| `version` | image tag; a leading `v` is stripped; `:latest` is also pushed |
| `images` | JSON array of `{name, image_suffix, dockerfile, context, cache_scope}` (one entry per image) |
| `platforms` | buildx platforms (default `linux/amd64,linux/arm64`) |
| `use_gh_packages_secret` | mount `github_token` as a build-secret for private NuGet restore |

### `deploy-vps.yml` — ship an image to a VPS over SSH

Ships a published image to a host **without any registry login on the host**: the runner pulls
the image, `docker save | ssh` streams the tarball into a forced-command `deploy.sh`, the tag
arrives as the SSH command (re-validated server-side), then the runner smoke-tests a healthz URL.

```yaml
# .github/workflows/deploy.yml in the consuming repo
on:
  workflow_dispatch:
    inputs:
      tag: { description: 'Image tag (semver or latest)', required: true, type: string, default: latest }
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

Host prerequisite (per service): a CI deploy key locked to the forced command in the host's
`~/.ssh/authorized_keys`, so the key can only run the deploy script and nothing else:

```
command="/opt/<name>/deploy.sh",no-port-forwarding,no-X11-forwarding,no-agent-forwarding,no-pty ssh-ed25519 AAAA... ci-deploy
```

`deploy.sh` validates the tag, `docker load`s the piped tarball, pins it in the compose `.env`,
recreates the container, and waits for the healthcheck. A reference script is kept in each
consuming repo under `deploy/deploy.sh`.

## Releasing

Each tool's version lives in its csproj (`src/Mcp.ToolsDoc`, `src/Mcp.I18nCheck`). Pushing a
`v X.Y.Z` tag packs **both** and publishes them to nuget.org via
`.github/workflows/publish.yml` (`--skip-duplicate`, so unchanged versions are no-ops; requires
the repo secret `NUGET_API_KEY`). Bump the relevant csproj `<Version>` before tagging.

License: MIT.
