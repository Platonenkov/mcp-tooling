#!/usr/bin/env bash
# Unified bilingual-docs gate (identical across all StaticBit MCP repos).
#
# Layout convention (English is canonical / default):
#   - docs/<path>.md         -> docs/ru/<path>.md          (mirror subtree)
#   - <dir>/X.md             -> <dir>/X.ru.md              (suffix sibling) for:
#       repo root, plugins/*, examples/** , servers/*, infra/
#
# Every Russian counterpart must exist AND be non-stub (>= MIN_BYTES bytes).
# English-only / generated docs are exempted via a repo-root .i18nignore file
# (one repo-relative path per line; blank lines and #-comments allowed).
#
# Exit: 0 = all good; 1 = missing/stub/orphan translations.

set -euo pipefail

MIN_BYTES=200
repo_root="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
cd "$repo_root"

ignore_file=".i18nignore"
is_ignored() {
  [ -f "$ignore_file" ] || return 1
  grep -vE '^\s*(#|$)' "$ignore_file" | grep -qxF "$1"
}

errors=0
err() { echo "::error::$1" >&2; errors=$((errors + 1)); }

check_nonstub() {
  local ru="$1"
  if [ -f "$ru" ]; then
    local sz
    sz=$(wc -c < "$ru")
    if [ "$sz" -lt "$MIN_BYTES" ]; then
      err "RU translation looks like a stub (${sz}b < ${MIN_BYTES}b): $ru"
    fi
  fi
}

# 1) docs/ mirror — docs/**/*.md (excluding docs/ru/) must have docs/ru/<rel>.
if [ -d docs ]; then
  while IFS= read -r en; do
    rel="${en#docs/}"
    case "$rel" in ru/*) continue ;; esac
    is_ignored "$en" && continue
    ru="docs/ru/$rel"
    [ -f "$ru" ] || err "missing RU mirror: $en -> $ru"
    check_nonstub "$ru"
  done < <(find docs -name '*.md' -type f 2>/dev/null)

  # reverse — every docs/ru/**/*.md must have its English source.
  while IFS= read -r ru; do
    en="docs/${ru#docs/ru/}"
    is_ignored "$en" && continue
    [ -f "$en" ] || err "orphan RU mirror (no English source): $ru -> $en"
  done < <(find docs/ru -name '*.md' -type f 2>/dev/null)
fi

# 2) suffix pairs outside docs/ — <dir>/X.md must have <dir>/X.ru.md.
suffix_globs=( "*.md" "plugins/*/*.md" "examples/*/*.md" "examples/*/*/*.md" "servers/*/*.md" "infra/*.md" )
for g in "${suffix_globs[@]}"; do
  for en in $g; do
    [ -f "$en" ] || continue
    case "$en" in *.ru.md) continue ;; esac
    is_ignored "$en" && continue
    ru="${en%.md}.ru.md"
    [ -f "$ru" ] || err "missing RU sibling: $en -> $ru"
    check_nonstub "$ru"
  done
done

# 3) explicit extra pairs for docs outside the conventional locations
#    (repo-root .i18npairs, one `en-path:ru-path` per line; #-comments allowed).
pairs_file=".i18npairs"
if [ -f "$pairs_file" ]; then
  while IFS= read -r line; do
    case "$line" in '' | \#*) continue ;; esac
    en="${line%%:*}"
    ru="${line##*:}"
    [ -f "$en" ] || { err "explicit pair references missing English file: $en"; continue; }
    [ -f "$ru" ] || err "missing RU (explicit pair): $en -> $ru"
    check_nonstub "$ru"
  done < "$pairs_file"
fi

if [ "$errors" -gt 0 ]; then
  echo "::error::$errors bilingual-docs issue(s). English is canonical; add the Russian counterpart (docs/ru/<path>.md or <name>.ru.md), or list English-only docs in .i18nignore." >&2
  exit 1
fi

echo "OK: bilingual docs check passed."
exit 0
