#!/bin/sh
# Scoped ignored Unity output only; defaults to preview.
set -eu
cd "$(dirname "$0")"
case "${1:-}" in
  "") exec git clean -ndX -- Library Temp Obj Logs Build Builds ;;
  --apply) exec git clean -fdX -- Library Temp Obj Logs Build Builds ;;
  *) echo "Usage: ./clean.sh [--apply]" >&2; exit 2 ;;
esac
