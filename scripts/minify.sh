#!/bin/sh
# Minify the web app's browser scripts and stylesheets with esbuild (T-0104). The repository keeps
# the readable sources; the container image build minifies them before `dotnet publish`, which then
# fingerprints and compresses the minified files.
#
# Usage:
#   scripts/minify.sh [file...]             check: minify into a temporary directory, fail on errors
#   scripts/minify.sh --in-place [file...]  replace each file with its minified form (image build, CI)
# Without files: every .js and .css file under src/Weesite.Web/wwwroot.
#
# Scripts are ES modules (every page loads them with type="module"), so top-level names can be
# shortened. No target: esbuild keeps modern syntax (CSS nesting, light-dark()) as it is.
# Like scripts/with-tool.sh, a missing esbuild is skipped locally and fails in CI
# (WEESITE_REQUIRE_TOOLS=1).
set -eu

cd "$(dirname "$0")/.."

in_place=0
if [ "${1:-}" = --in-place ]; then
  in_place=1
  shift
fi

if ! command -v esbuild >/dev/null 2>&1; then
  if [ "${WEESITE_REQUIRE_TOOLS:-0}" = 1 ] || [ "$in_place" = 1 ]; then
    echo "error: esbuild is not installed (required). Run 'just doctor' for install hints." >&2
    exit 127
  fi
  echo "SKIPPED: esbuild is not installed, so this check did not run. Run 'just doctor' for install hints." >&2
  exit 0
fi

if [ $# -eq 0 ]; then
  if [ "$in_place" = 1 ]; then
    exec find src/Weesite.Web/wwwroot -type f \( -name '*.js' -o -name '*.css' \) -exec "$0" --in-place {} +
  fi
  exec find src/Weesite.Web/wwwroot -type f \( -name '*.js' -o -name '*.css' \) -exec "$0" {} +
fi

if [ "$in_place" = 1 ]; then
  out=.
else
  out=$(mktemp -d)
  trap 'rm -rf "$out"' EXIT
fi

before=$(cat "$@" | wc -c)
# A CSS syntax error is only a warning to esbuild; here it fails like a script syntax error.
# --outbase=. keeps each file's path under the output directory, so in place it overwrites itself.
esbuild "$@" --minify --format=esm --charset=utf8 --log-level=warning --log-override:css-syntax-error=error \
  --outbase=. --outdir="$out" --allow-overwrite

if [ "$in_place" = 0 ]; then
  after=$(cd "$out" && cat "$@" | wc -c)
  echo "minify: $# files, $before bytes -> $after bytes (checked only; the sources are unchanged)"
fi
