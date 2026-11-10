#!/bin/sh
# Print the commit the last successful infra-apply run covered, if it's an
# ancestor of <head-sha>; print nothing otherwise (then everything is checked).
# Usage: scripts/last-applied.sh <head-sha>
#
# infra-apply compares against this rather than the push's `before` commit
# (F-0048): `just merge-stack` pushes one commit per PR, GitHub keeps only the
# newest waiting run of a concurrency group and cancels the others, so a run
# for an earlier commit may never happen. Comparing with the last success
# covers every commit since, cancelled runs and failed applies included.
# Needs GH_TOKEN with actions: read and GITHUB_REPOSITORY.
set -eu

if [ "$#" -ne 1 ]; then
  echo "usage: $0 <head-sha>" >&2
  exit 2
fi
head=$1

base=$(gh api "repos/$GITHUB_REPOSITORY/actions/workflows/infra-apply.yml/runs?branch=main&status=success&per_page=1" \
  --jq '.workflow_runs[0].head_sha // empty')

if [ -n "$base" ] && git cat-file -e "$base^{commit}" 2>/dev/null && git merge-base --is-ancestor "$base" "$head"; then
  echo "$base"
fi
