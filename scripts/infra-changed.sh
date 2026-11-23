#!/bin/sh
# Print `changed=true` or `changed=false` (for $GITHUB_OUTPUT): whether any
# file that the infrastructure checks depend on changed between two commits.
# Usage: scripts/infra-changed.sh [--plan] <base-sha> <head-sha>
#
# Without --plan: what the static checks read (infra-static runs `just infra`,
# so the justfile, the lint configs and the policy tests count). With --plan:
# only what a plan or apply of infra/app reads (infra-plan, infra-apply), so a
# recipe or policy test change doesn't ask for prod's approval of an empty
# plan (F-0244).
#
# The infra workflows are required checks, so they always run and use this
# to skip their steps instead of using path filters (a workflow skipped by a
# path filter never reports, which blocks the merge).
set -eu

plan=false
if [ "${1:-}" = --plan ]; then
  plan=true
  shift
fi
if [ "$#" -ne 2 ]; then
  echo "usage: $0 [--plan] <base-sha> <head-sha>" >&2
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

# The scripts and the tools action that both a plan and the static checks use.
shared='scripts/policy-waivers.sh scripts/infra-changed.sh scripts/last-applied.sh scripts/infra-plan.sh scripts/infra-apply.sh scripts/plan-summary.sh .github/actions'
if [ "$plan" = true ]; then
  # infra-plan.sh plans infra/app and checks the plan with conftest (policy/, not its tests) and
  # Checkov (.checkov.yaml); bootstrap is its own root, applied by hand.
  # shellcheck disable=SC2086  # $shared is a list of paths without spaces
  set -- infra ':(exclude)infra/bootstrap' policy ':(exclude,glob)policy/**/*_test.rego' .checkov.yaml \
    $shared '.github/workflows/infra-plan.yml' '.github/workflows/infra-apply.yml'
else
  # shellcheck disable=SC2086  # $shared is a list of paths without spaces
  set -- infra policy .tflint.hcl .checkov.yaml justfile \
    $shared '.github/workflows/infra-*.yml' '.github/workflows/drift.yml'
fi

if git diff --name-only "$base" "$head" -- "$@" | grep -q .; then
  echo "changed=true"
else
  echo "changed=false"
fi
