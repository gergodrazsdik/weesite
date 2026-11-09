#!/bin/sh
# Check the syntax of the site's browser scripts (F-0038). Pages load them as ES modules
# (<script type="module">), so each file is parsed as a module: `node --check file.js` alone
# would parse it as CommonJS and miss module-only errors, such as a top-level return.
# Usage: scripts/check-js.sh <file>...
#
# Like scripts/with-tool.sh, a missing node is skipped locally and fails in CI
# (WEESITE_REQUIRE_TOOLS=1).
set -eu

if ! command -v node >/dev/null 2>&1; then
  if [ "${WEESITE_REQUIRE_TOOLS:-0}" = 1 ]; then
    echo "error: node is not installed (required). Run 'just doctor' for install hints." >&2
    exit 127
  fi
  echo "SKIPPED: node is not installed, so this check did not run. Run 'just doctor' for install hints." >&2
  exit 0
fi

status=0
for file in "$@"; do
  if ! node --input-type=module --check <"$file"; then
    echo "check-js: syntax error in $file (the line above counts from its start)" >&2
    status=1
  fi
done
exit "$status"
