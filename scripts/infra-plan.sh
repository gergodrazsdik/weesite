#!/bin/sh
# Plan infra/app for one environment and check the plan against the policy
# (conftest with the inline waivers) and Checkov.
# Usage: scripts/infra-plan.sh <test|prod> <out-dir> [--lock]
#
# Writes to <out-dir>:
#   <env>.tfplan       the plan, encrypted with the environment's state key
#   <env>.plan.json    the plan as JSON: plaintext, keep it on the runner
#   <env>.summary.md   counts and addresses only (scripts/plan-summary.sh)
# and prints `state=present|missing` and `changes=<n>` (for $GITHUB_OUTPUT).
#
# The plan identity can only read the state, so plans run with -lock=false
# unless --lock is given (the apply identities). The plan text is never
# printed: workflow logs of a public repo are public.
#
# Before the first apply an environment has no state, and `tofu init` wants
# to write an empty one, which the read-only plan identity can't. Without
# --lock that case is reported as state=missing (exit 0, no plan); the
# first apply, which plans with --lock, creates the state.
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
# Only prod has a custom domain (repository variable PROD_DOMAIN); test uses
# the generated Container Apps hostname.
if [ "$env" = prod ]; then
  TF_VAR_domain=${PROD_DOMAIN:-}
else
  TF_VAR_domain=
fi
export TF_VAR_environment TF_VAR_domain

echo "infra-plan: $env: init" >&2
if ! tofu -chdir="$root" init -input=false -no-color -lockfile=readonly -reconfigure >/dev/null 2>"$out/$env.init.err"; then
  if [ "$lock" = false ] &&
    grep -q 'failed to lock azure state' "$out/$env.init.err" &&
    grep -q 'BlobNotFound' "$out/$env.init.err"; then
    echo "infra-plan: $env: no state yet; the first apply creates it" >&2
    # shellcheck disable=SC2016  # the backticks are Markdown code spans
    printf '#### infra/app · %s\n\nNo state yet, so no plan: the first `infra-apply` run on `main` creates it.\n' "$env" >"$out/$env.summary.md"
    echo "state=missing"
    echo "changes=0"
    exit 0
  fi
  cat "$out/$env.init.err" >&2
  exit 1
fi

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
echo "state=present"
echo "changes=$changes"
