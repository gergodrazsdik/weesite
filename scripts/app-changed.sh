#!/bin/sh
# Print `changed=true` or `changed=false` (for $GITHUB_OUTPUT): whether a pull
# request changed anything the app workflow builds, tests or packages.
# Usage: scripts/app-changed.sh <base-sha> <head-sha>
#
# The app workflow's jobs are required checks, so the workflow always runs
# and uses this to skip its jobs instead of using path filters (a workflow
# skipped by a path filter never reports, which blocks the merge); a skipped
# job counts as passed. The list is of what can't change the app: docs,
# infrastructure, policy, the other workflows, lint and agent settings. A file
# it doesn't name runs everything, so a new kind of file errs on the safe side.
set -eu

if [ "$#" -ne 2 ]; then
  echo "usage: $0 <base-sha> <head-sha>" >&2
  exit 2
fi
base=$1
head=$2

# A new branch or a missing base: build everything.
if [ -z "$base" ] || [ "$base" = 0000000000000000000000000000000000000000 ] ||
  ! git cat-file -e "$base^{commit}" 2>/dev/null; then
  echo "changed=true"
  exit 0
fi

# Markdown at the top level only (glob: `*` doesn't cross `/`): seed/ holds the
# sample site's posts, which the tests read.
if git diff --name-only "$base" "$head" -- . \
  ':(exclude)docs/' ':(exclude)infra/' ':(exclude)policy/' ':(exclude)loadtest/' ':(exclude).claude/' \
  ':(exclude,glob)*.md' ':(exclude)LICENSE' ':(exclude)NOTICE' \
  ':(exclude).github/ISSUE_TEMPLATE/' ':(exclude).github/PULL_REQUEST_TEMPLATE.md' ':(exclude).github/CODEOWNERS' \
  ':(exclude).github/rulesets/' ':(exclude).github/dependabot.yml' ':(exclude).github/actionlint.yaml' \
  ':(exclude).github/actions/setup-infra-tools/' \
  ':(exclude).github/workflows/dependency-review.yml' ':(exclude).github/workflows/drift.yml' \
  ':(exclude).github/workflows/geoip.yml' ':(exclude).github/workflows/infra-apply.yml' \
  ':(exclude).github/workflows/infra-plan.yml' ':(exclude).github/workflows/infra-static.yml' \
  ':(exclude).github/workflows/lint.yml' ':(exclude).github/workflows/load-test.yml' \
  ':(exclude).github/workflows/promote.yml' ':(exclude).github/workflows/seed.yml' \
  ':(exclude).github/workflows/tool-updates.yml' ':(exclude)scripts/tool-updates.py' \
  ':(exclude).tflint.hcl' ':(exclude).checkov.yaml' ':(exclude)_typos.toml' ':(exclude).yamllint.yaml' \
  ':(exclude,glob).markdownlint*' ':(exclude)lefthook.yml' ':(exclude)justfile' ':(exclude)local.just.example' \
  ':(exclude).mcp.json' |
  grep -q .; then
  echo "changed=true"
else
  echo "changed=false"
fi
