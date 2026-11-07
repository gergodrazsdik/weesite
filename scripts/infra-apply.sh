#!/bin/sh
# Apply infra/app for one environment with its apply identity.
# Usage: scripts/infra-apply.sh <test|prod> <plan-dir>
#
# Applies <plan-dir>/<env>.tfplan, the saved plan from the plan job, if
# there is one. OpenTofu refuses a plan whose state has changed since, so
# what was reviewed is what gets applied. Without a saved plan (the
# environment had no state yet, so the read-only plan identity couldn't
# plan), it plans here with the state lock (scripts/infra-plan.sh --lock,
# policy and Checkov included) and applies that.
#
# The apply output names resources with their IDs and values, so it goes
# to a file; only the "Apply complete" line is printed. Errors still reach
# the log through stderr.
set -eu

if [ "$#" -ne 2 ]; then
  echo "usage: $0 <test|prod> <plan-dir>" >&2
  exit 2
fi
env=$1
mkdir -p "$2"
dir=$(cd "$2" && pwd)
root=infra/app

TF_VAR_environment=$env
export TF_VAR_environment

if [ -f "$dir/$env.tfplan" ]; then
  echo "infra-apply: $env: init" >&2
  tofu -chdir="$root" init -input=false -no-color -lockfile=readonly -reconfigure >/dev/null
  plan="$dir/$env.tfplan"
else
  echo "infra-apply: $env: no saved plan (no state yet); planning with the lock" >&2
  scripts/infra-plan.sh "$env" "$dir" --lock >/dev/null
  plan="$dir/$env.tfplan"
fi

echo "infra-apply: $env: apply" >&2
if ! tofu -chdir="$root" apply -input=false -no-color "$plan" >"$dir/$env.apply.log"; then
  echo "infra-apply: $env: apply failed" >&2
  exit 1
fi
grep -E '^Apply complete!' "$dir/$env.apply.log" || echo "Apply complete."
