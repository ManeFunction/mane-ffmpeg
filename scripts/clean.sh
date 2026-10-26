#!/usr/bin/env bash
# Deletes build output: every bin/ and obj/ under src/ and tests/. The next build recreates them.
# Usage: scripts/clean.sh [--all]   (--all also deletes build/, the packaged .app and Linux bundles)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"

targets=()
while IFS= read -r dir; do
  targets+=("$dir")
done < <(find "$ROOT/src" "$ROOT/tests" -type d \( -name bin -o -name obj \) -prune 2>/dev/null)

if [ "${1:-}" = "--all" ] && [ -d "$ROOT/build" ]; then
  targets+=("$ROOT/build")
fi

if [ "${#targets[@]}" -eq 0 ]; then
  echo "Nothing to clean."
  exit 0
fi

du -sh "${targets[@]}"
rm -rf "${targets[@]}"
echo "Removed ${#targets[@]} folder(s)."
