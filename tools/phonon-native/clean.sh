#!/bin/sh
# Preview/remove only this bridge's ignored build output.
set -eu
cd "$(dirname "$0")"
case "${1:-}" in
  "") exec git clean -ndX -- target ;;
  --apply) exec git clean -fdX -- target ;;
  *) echo "Usage: ./clean.sh [--apply]" >&2; exit 2 ;;
esac
