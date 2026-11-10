#!/usr/bin/env bash
# Merges a PR by fast-forwarding main to its head, so its commits land as they were reviewed and signed.
# GitHub marks the PR merged when its head reaches main. The admin bypass in .github/rulesets/main.json allows the
# push, and it also skips the ruleset's checks, so this checks the same things first: the required checks pass,
# every commit is signed by a key you trust, and main hasn't moved past the PR's base.
#
# With --stack, <pr> is the top of a stack of PRs (each based on the branch of the one below, as `gh stack` makes
# them). It checks every PR of the stack first, retargets them all to main, then fast-forwards main to the top of the
# stack in one push, which merges them all. (`gh stack merge` can't be used: it merges through GitHub, which rewrites
# the commits without your signatures.)
#
# --dry-run runs every check and stops before retargeting or pushing anything.
#
# Run it from a clone of the repo the PR is in (`just merge <pr>`, `just merge-stack <top-pr>`):
#   ff-merge.sh [--stack] [--dry-run] <pr-number>
set -euo pipefail

stack=false
dry_run=false
while [[ ${1:-} == --* ]]; do
  case $1 in
    --stack) stack=true ;;
    --dry-run) dry_run=true ;;
    *)
      echo "unknown option: $1" >&2
      exit 2
      ;;
  esac
  shift
done
pr=${1:?usage: ff-merge.sh [--stack] [--dry-run] <pr-number>}

# Runs gh, retrying when GitHub's API times out or drops the connection, which happens from here now and then. Other
# failures, such as a failed or pending check, come back at once with gh's exit status.
gh_retry() {
  local attempt err status
  err=$(mktemp)
  for attempt in 1 2 3 4 5; do
    status=0
    gh "$@" 2>"$err" || status=$?
    if [[ $status -eq 0 ]] || ! grep -qiE 'timeout|connection (reset|refused)|EOF' "$err"; then
      cat "$err" >&2
      rm -f "$err"
      return "$status"
    fi
    echo "GitHub didn't answer (try $attempt of 5); retrying." >&2
    sleep 5
  done
  cat "$err" >&2
  rm -f "$err"
  return "$status"
}

# Prints "<head-sha> <base-branch> <state> <head-branch>" for a PR.
pr_info() {
  gh_retry pr view "$1" --json headRefOid,baseRefName,state,headRefName \
    --jq '"\(.headRefOid) \(.baseRefName) \(.state) \(.headRefName)"'
}

# Checks that a PR's required checks passed and every commit from main to <head> is signed by a key you trust.
check_pr() {
  local number=$1 head=$2 commit
  # Exit 8 means checks are still running.
  gh_retry pr checks "$number" --required >/dev/null
  git fetch --quiet origin main "$head"
  for commit in $(git rev-list origin/main.."$head"); do
    if ! git verify-commit "$commit" 2>/dev/null; then
      echo "PR #$number: commit $commit has no valid signature from a key you trust." >&2
      exit 1
    fi
  done
}

# Fast-forwards main to a PR's head.
push_main() {
  local number=$1 head=$2
  git fetch --quiet origin main
  if ! git merge-base --is-ancestor origin/main "$head"; then
    echo "PR #$number: main has moved past the PR's base. Rebase the branch onto main and push it again." >&2
    exit 1
  fi
  git push origin "$head:refs/heads/main"
  if [[ $(git branch --show-current) == main ]]; then
    git merge --quiet --ff-only "$head"
  fi
  echo "Merged #$number: main is now ${head:0:7}."
}

if [[ $stack == false ]]; then
  read -r head base state _ < <(pr_info "$pr")
  if [[ $state != OPEN || $base != main ]]; then
    echo "PR #$pr is $state against $base; only open PRs against main can be merged here (a stack: just merge-stack)." >&2
    exit 1
  fi
  check_pr "$pr" "$head"
  if [[ $dry_run == true ]]; then
    echo "Dry run: #$pr can be merged (main would become ${head:0:7})."
    exit 0
  fi
  push_main "$pr" "$head"
  exit 0
fi

# --stack: collect the chain from the top PR down to the one based on main.
chain=()
heads=()
number=$pr
while :; do
  read -r head base state _ < <(pr_info "$number")
  if [[ $state != OPEN ]]; then
    echo "PR #$number is $state; every PR of the stack must be open." >&2
    exit 1
  fi
  chain=("$number" "${chain[@]}")
  heads=("$head" "${heads[@]}")
  [[ $base == main ]] && break
  below=$(gh_retry pr list --state open --head "$base" --json number --jq '.[0].number // empty')
  if [[ -z $below ]]; then
    echo "PR #$number is based on $base, but no open PR has that branch as its head." >&2
    exit 1
  fi
  if ((${#chain[@]} >= 30)); then
    echo "The stack is deeper than 30 PRs; stopping." >&2
    exit 1
  fi
  number=$below
done
echo "Stack, bottom to top: ${chain[*]/#/#}"

# Check everything before merging anything: checks, signatures, and that each head contains the one below.
for i in "${!chain[@]}"; do
  check_pr "${chain[$i]}" "${heads[$i]}"
  if ((i > 0)) && ! git merge-base --is-ancestor "${heads[$((i - 1))]}" "${heads[$i]}"; then
    echo "PR #${chain[$i]} doesn't contain #${chain[$((i - 1))]}'s head: rebase the stack (gh stack rebase) and push it." >&2
    exit 1
  fi
  echo "ok      #${chain[$i]} ${heads[$i]:0:7}"
done
if [[ $dry_run == true ]]; then
  echo "Dry run: the stack can be merged (main would become ${heads[-1]:0:7})."
  exit 0
fi

# Retarget every PR to main first, then fast-forward main to the top of the stack in one push. GitHub marks each PR
# merged whose head that push brings to main, and the workflows on main run once for the whole stack. One push per PR
# started every workflow once per PR, each run cancelling the one before, and infra-apply could miss a change (F-0048).
for i in "${!chain[@]}"; do
  number=${chain[$i]}
  read -r head base state _ < <(pr_info "$number")
  if [[ $head != "${heads[$i]}" ]]; then
    echo "PR #$number changed while merging (head is now ${head:0:7}); stopping. Run it again." >&2
    exit 1
  fi
  if [[ $base != main ]]; then
    echo "Retargeting #$number from $base to main."
    gh_retry pr edit "$number" --base main >/dev/null
  fi
done
push_main "${chain[-1]}" "${heads[-1]}"

# GitHub marks the PRs merged within seconds of the push; report any it hasn't after a minute.
for number in "${chain[@]}"; do
  for _ in $(seq 1 12); do
    read -r _ _ state _ < <(pr_info "$number")
    [[ $state == MERGED ]] && break
    sleep 5
  done
  if [[ $state != MERGED ]]; then
    echo "warning: #$number isn't marked merged yet ($state); its commits are on main. Check it on GitHub." >&2
  fi
done
echo "Merged the stack: ${chain[*]/#/#}."
