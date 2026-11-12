#!/bin/sh
# Download the embedding models into models/<name>/, pinned to a Hugging Face commit and checked by
# SHA-256 (ADR 0003, ADR 0008):
#   bge-small-en-v1.5      English, the default: int8 ONNX and its WordPiece vocabulary
#   multilingual-e5-small  about a hundred languages (Search:EmbeddingModel): int8 ONNX and its
#                          SentencePiece model
# Files that are already there with the right hash are kept. The Containerfile pins the same
# URLs and hashes; change both together.
# Usage: scripts/fetch-model.sh [dir]   (default: models)
set -eu

root=${1:-models}

fetch() {
  dir=$1
  base=$2
  name=$3
  path=$4
  sha=$5
  mkdir -p "$dir"
  if [ -f "$dir/$name" ] && echo "$sha  $dir/$name" | sha256sum -c --status; then
    echo "fetch-model: $dir/$name is up to date"
    return
  fi
  echo "fetch-model: downloading $dir/$name"
  curl -fsSL --retry 3 -o "$dir/$name.part" "$base/$path"
  if ! echo "$sha  $dir/$name.part" | sha256sum -c --status; then
    rm -f "$dir/$name.part"
    echo "fetch-model: $dir/$name doesn't match its SHA-256; not using it" >&2
    exit 1
  fi
  mv "$dir/$name.part" "$dir/$name"
}

bge=https://huggingface.co/Xenova/bge-small-en-v1.5/resolve/ea104dacec62c0de699686887e3f920caeb4f3e3
fetch "$root/bge-small-en-v1.5" "$bge" model.onnx onnx/model_quantized.onnx 6c9c6101a956d62dfb5e7190c538226c0c5bb9cb27b651234b6df063ee7dbfe4
fetch "$root/bge-small-en-v1.5" "$bge" vocab.txt vocab.txt 07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3

e5=https://huggingface.co/Xenova/multilingual-e5-small/resolve/761b726dd34fb83930e26aab4e9ac3899aa1fa78
fetch "$root/multilingual-e5-small" "$e5" model.onnx onnx/model_quantized.onnx f80102d3f2a1229f387d3c81909990d8945513e347b0eab049f7de3c6f98c193
fetch "$root/multilingual-e5-small" "$e5" sentencepiece.bpe.model sentencepiece.bpe.model cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865
