#!/bin/sh
set -eu
cd "$(dirname "$0")"
case "${1:-}" in
  "") exec git clean -ndX -- .zig-cache zig-out ;;
  --apply) exec git clean -fdX -- .zig-cache zig-out ;;
  *) echo "Usage: ./clean.sh [--apply]" >&2; exit 2 ;;
esac
