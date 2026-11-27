#!/bin/sh
# Check that the Containerfile's rust image is the Rust version
# native/highlight/rust-toolchain.toml pins (ADR 0012): Dependabot moves the
# image on its own, and the two must move together. Run from anywhere.
set -eu

root=$(cd "$(dirname "$0")/.." && pwd)
pinned=$(sed -n 's/^channel = "\(.*\)"$/\1/p' "$root/native/highlight/rust-toolchain.toml")
image=$(sed -n 's#^FROM .*docker.io/library/rust:\([0-9][0-9.]*\)-.* AS highlighter$#\1#p' "$root/Containerfile")

if [ -z "$pinned" ] || [ -z "$image" ]; then
  echo "check-rust-version: can't read the Rust version from rust-toolchain.toml ('$pinned') or the Containerfile ('$image')" >&2
  exit 1
fi

if [ "$pinned" != "$image" ]; then
  echo "check-rust-version: the Containerfile builds with Rust $image, but native/highlight/rust-toolchain.toml pins $pinned; move both together" >&2
  exit 1
fi

echo "check-rust-version: Rust $pinned in both"
