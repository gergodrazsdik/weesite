#!/bin/sh
# Download the embedding model (bge-small-en-v1.5, int8 ONNX, and its WordPiece vocabulary) into
# models/bge-small-en-v1.5/, pinned to a Hugging Face commit and checked by SHA-256 (ADR 0003).
# Files that are already there with the right hash are kept. The Containerfile pins the same
# URLs and hashes; change both together.
# Usage: scripts/fetch-model.sh [dir]
set -eu

dir=${1:-models/bge-small-en-v1.5}
base=https://huggingface.co/Xenova/bge-small-en-v1.5/resolve/ea104dacec62c0de699686887e3f920caeb4f3e3

mkdir -p "$dir"

fetch() {
  name=$1
  path=$2
  sha=$3
  if [ -f "$dir/$name" ] && echo "$sha  $dir/$name" | sha256sum -c --status; then
    echo "fetch-model: $name is up to date"
    return
  fi
  echo "fetch-model: downloading $name"
  curl -fsSL --retry 3 -o "$dir/$name.part" "$base/$path"
  if ! echo "$sha  $dir/$name.part" | sha256sum -c --status; then
    rm -f "$dir/$name.part"
    echo "fetch-model: $name doesn't match its SHA-256; not using it" >&2
    exit 1
  fi
  mv "$dir/$name.part" "$dir/$name"
}

fetch model.onnx onnx/model_quantized.onnx 6c9c6101a956d62dfb5e7190c538226c0c5bb9cb27b651234b6df063ee7dbfe4
fetch vocab.txt vocab.txt 07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3
