#!/bin/sh
# Print `changed=true` or `changed=false` (for $GITHUB_OUTPUT): whether any
# file that the infrastructure checks depend on changed between two commits.
# Usage: scripts/infra-changed.sh <base-sha> <head-sha>
#
# The infra workflows are required checks, so they always run and use this
# to skip their steps instead of using path filters (a workflow skipped by a
# path filter never reports, which blocks the merge).
set -eu

if [ "$#" -ne 2 ]; then
  echo "usage: $0 <base-sha> <head-sha>" >&2
  exit 2
fi
base=$1
head=$2

# A new branch or a missing base: check everything.
if [ -z "$base" ] || [ "$base" = 0000000000000000000000000000000000000000 ] ||
  ! git cat-file -e "$base^{commit}" 2>/dev/null; then
  echo "changed=true"
  exit 0
fi

if git diff --name-only "$base" "$head" -- \
  infra policy .tflint.hcl .checkov.yaml justfile \
  scripts/policy-waivers.sh scripts/infra-changed.sh scripts/infra-plan.sh scripts/plan-summary.sh \
  .github/actions '.github/workflows/infra-*.yml' '.github/workflows/drift.yml' | grep -q .; then
  echo "changed=true"
else
  echo "changed=false"
fi
