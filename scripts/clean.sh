#!/usr/bin/env bash
# Deletes build output: Cargo's target/ folder. The next build recreates it.
# Usage: scripts/clean.sh [--all]   (--all also deletes build/, the packaged .app and Linux bundles)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"

targets=()
[ -d "$ROOT/target" ] && targets+=("$ROOT/target")
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
