#!/bin/sh
# Scoped headless C# build output; defaults to preview.
set -eu
cd "$(dirname "$0")"
case "${1:-}" in
 "") exec git clean -ndX -- bin obj ;;
 --apply) exec git clean -fdX -- bin obj ;;
 *) echo "Usage: ./clean.sh [--apply]" >&2; exit 2 ;;
esac
