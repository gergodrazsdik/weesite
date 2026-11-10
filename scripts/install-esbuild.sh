#!/bin/sh
# Install the esbuild the Containerfile pins (T-0104): its linux-x64 npm package, checked by
# SHA-256. The Containerfile's ADD line is the only place the version and checksum live, so CI and
# the image build use the same esbuild.
# Usage: scripts/install-esbuild.sh <directory on PATH>
set -eu

if [ $# -ne 1 ]; then
  echo "usage: scripts/install-esbuild.sh <directory>" >&2
  exit 2
fi
dir=$1
cd "$(dirname "$0")/.."

# The checksum is on the ADD line, the URL on the line after it.
pin=$(awk '/--checksum=sha256:/ { line = $0 }
  /registry\.npmjs\.org\/@esbuild\/linux-x64\// {
    sub(/.*--checksum=sha256:/, "", line); sub(/ .*/, "", line); print line, $1; exit
  }' Containerfile)
sha256=${pin%% *}
url=${pin#* }
if [ -z "$pin" ] || [ "$sha256" = "$url" ]; then
  echo "install-esbuild: no esbuild ADD line with a checksum in the Containerfile" >&2
  exit 1
fi

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
curl -fsSL -o "$tmp/esbuild.tgz" "$url"
echo "$sha256  $tmp/esbuild.tgz" | sha256sum --check --quiet
tar -xzf "$tmp/esbuild.tgz" -C "$tmp" package/bin/esbuild
mkdir -p "$dir"
install -m 0755 "$tmp/package/bin/esbuild" "$dir/esbuild"
"$dir/esbuild" --version
