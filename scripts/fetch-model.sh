#!/bin/sh
# Download the embedding models into models/<name>/, pinned to a Hugging Face commit and checked by
# SHA-256 (ADR 0003, ADR 0008):
#   bge-small-en-v1.5      English, the default: int8 ONNX and its WordPiece vocabulary
#   multilingual-e5-small  about a hundred languages (Search:EmbeddingModel): int8 ONNX and its
#                          SentencePiece model
# Files that are already there with the right hash are kept. The Containerfile's ADD lines are the
# only place the URLs and hashes live (F-0251), as for esbuild (install-esbuild.sh), so the tests
# and the image use the same models.
# Usage: scripts/fetch-model.sh [dir]   (default: models)
set -eu

root=${1:-models}
containerfile="$(dirname "$0")/../Containerfile"

fetch() {
  sha=$1
  url=$2
  file=$3
  dir=$(dirname "$file")
  mkdir -p "$dir"
  if [ -f "$file" ] && echo "$sha  $file" | sha256sum -c --status; then
    echo "fetch-model: $file is up to date"
    return
  fi
  echo "fetch-model: downloading $file"
  curl -fsSL --retry 3 -o "$file.part" "$url"
  if ! echo "$sha  $file.part" | sha256sum -c --status; then
    rm -f "$file.part"
    echo "fetch-model: $file doesn't match its SHA-256; not using it" >&2
    exit 1
  fi
  mv "$file.part" "$file"
}

# Each model file is an ADD with --checksum on one line, the URL on the next and the destination,
# ./Models/<model>/<file>, after it.
pins=$(awk '
  /--checksum=sha256:/ { sha = $0; sub(/.*--checksum=sha256:/, "", sha); sub(/ .*/, "", sha); next }
  sha != "" && url == "" && /huggingface\.co\// { url = $1; next }
  url != "" && /\.\/Models\// { print sha, url, $1; sha = ""; url = ""; next }
  { sha = ""; url = "" }
' "$containerfile")
if [ "$(echo "$pins" | grep -c 'Models/')" -lt 4 ]; then
  echo "fetch-model: expected the model ADD lines in the Containerfile, found: $pins" >&2
  exit 1
fi

echo "$pins" | while read -r sha url dest; do
  fetch "$sha" "$url" "$root/${dest#./Models/}"
done
