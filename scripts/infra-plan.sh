#!/bin/sh
# Plan infra/app for one environment and check the plan against the policy
# (conftest with the inline waivers) and Checkov.
# Usage: scripts/infra-plan.sh <test|prod> <out-dir> [--lock]
#
# Writes to <out-dir>:
#   <env>.tfplan       the plan, encrypted with the environment's state key
#   <env>.plan.json    the plan as JSON: plaintext, keep it on the runner
#   <env>.summary.md   counts and addresses only (scripts/plan-summary.sh)
# and prints `changes=<n>` (for $GITHUB_OUTPUT) as its last line.
#
# The plan identity can only read the state, so plans run with -lock=false
# unless --lock is given (the apply identities). The plan text is never
# printed: workflow logs of a public repo are public.
set -eu

if [ "$#" -lt 2 ] || [ "$#" -gt 3 ]; then
  echo "usage: $0 <test|prod> <out-dir> [--lock]" >&2
  exit 2
fi
env=$1
mkdir -p "$2"
out=$(cd "$2" && pwd)
lock=false
if [ "${3:-}" = --lock ]; then
  lock=true
fi
root=infra/app

TF_VAR_environment=$env
export TF_VAR_environment

echo "infra-plan: $env: init" >&2
tofu -chdir="$root" init -input=false -no-color -lockfile=readonly -reconfigure >/dev/null

echo "infra-plan: $env: plan (lock=$lock)" >&2
if ! tofu -chdir="$root" plan -input=false -no-color -lock="$lock" -out="$out/$env.tfplan" >"$out/$env.plan.log"; then
  # Errors went to stderr; the plan log may hold values, so it stays unprinted.
  echo "infra-plan: $env: plan failed" >&2
  exit 1
fi
tofu -chdir="$root" show -json "$out/$env.tfplan" >"$out/$env.plan.json"

echo "infra-plan: $env: policy" >&2
scripts/policy-waivers.sh "$root" >"$out/waivers.json"
conftest test --no-color --policy policy --data policy/data --data "$out/waivers.json" "$out/$env.plan.json" >&2

echo "infra-plan: $env: checkov" >&2
checkov --file "$out/$env.plan.json" --framework terraform_plan --repo-root-for-plan-enrichment . \
  --quiet --compact --skip-download >&2

scripts/plan-summary.sh "$out/$env.plan.json" "infra/app · $env" >"$out/$env.summary.md"

changes=$(jq '[.resource_changes[]? | select(.change.actions != ["no-op"] and .change.actions != ["read"])] | length' "$out/$env.plan.json")
echo "changes=$changes"
