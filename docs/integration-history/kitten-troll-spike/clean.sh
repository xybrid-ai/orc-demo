#!/bin/sh
# Preview/remove only this archived spike's ignored local output.
set -eu
cd "$(dirname "$0")"
case "${1:-}" in
  "") exec git clean -ndX -- Tests/bin Tests/obj build tools/__pycache__ ;;
  --apply) exec git clean -fdX -- Tests/bin Tests/obj build tools/__pycache__ ;;
  *) echo "Usage: ./clean.sh [--apply]" >&2; exit 2 ;;
esac
