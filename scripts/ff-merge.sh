#!/usr/bin/env bash
# Merges a PR by fast-forwarding main to its head, so its commits land as they were reviewed and signed.
# GitHub marks the PR merged when its head reaches main. The admin bypass in .github/rulesets/main.json allows the
# push, and it also skips the ruleset's checks, so this checks them first, and more: every check on the PR passes
# (not only the required ones: F-0241), main hasn't moved past the PR's base, and every commit has a good signature
# from an allowed key (T-0177). A good signature alone isn't enough: git accepts one from any key in your keyring.
# The allowed keys are the full fingerprints in WEESITE_MERGE_SIGNERS (space- or comma-separated; set it in your
# environment or local.just), or else the key git signs your own commits with (user.signingkey). Commits by
# Dependabot may also be signed by GitHub's web-flow key, and only those.
#
# With --stack, <pr> is the top of a stack of PRs (each based on the branch of the one below, as `gh stack` makes
# them). It checks every PR of the stack first, then merges them bottom-up, one fast-forward each, retargeting each
# PR to main before its merge unless GitHub already did. (`gh stack merge` can't be used: it merges through GitHub,
# which rewrites the commits without your signatures.)
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

# GitHub's web-flow key, which signs the commits GitHub makes, Dependabot's among them.
web_flow=968479A1AFF927E37D1A566BB5690EEEBB952194
dependabot_email='49699333+dependabot[bot]@users.noreply.github.com'

# The fingerprints allowed to sign, upper case, one per line.
allowed_signers() {
  local signers=${WEESITE_MERGE_SIGNERS:-} key
  if [[ -z $signers ]]; then
    key=$(git config user.signingkey || true)
    if [[ -z $key ]]; then
      echo "Set WEESITE_MERGE_SIGNERS to the fingerprints of the keys allowed to sign (or git's user.signingkey)." >&2
      exit 1
    fi
    # The primary key's fingerprint: the first fingerprint record of the key's listing.
    signers=$(gpg --batch --with-colons --fingerprint "$key" 2>/dev/null | awk -F: '$1 == "fpr" { print $10; exit }')
    if [[ -z $signers ]]; then
      echo "Couldn't find the fingerprint of user.signingkey ($key); set WEESITE_MERGE_SIGNERS." >&2
      exit 1
    fi
  fi
  tr ',' ' ' <<<"$signers" | tr -s ' ' '\n' | grep -v '^$' | tr '[:lower:]' '[:upper:]'
}

# Succeeds when <commit> has a good signature from an allowed key: one in <signers>, or GitHub's web-flow key for a
# commit Dependabot authored.
signed_by_allowed_key() {
  local commit=$1 signers=$2 status signer primary email
  IFS=' ' read -r status signer primary email < <(git log -1 --format='%G? %GF %GP %ae' "$commit")
  # G: good and trusted; U: good, from a key of unknown trust. Anything else (bad, expired, revoked, none) fails.
  [[ $status == G || $status == U ]] || return 1
  signer=${signer^^}
  primary=${primary^^}
  if grep -qxF -e "$signer" -e "${primary:-$signer}" <<<"$signers"; then
    return 0
  fi
  [[ $email == "$dependabot_email" && ($signer == "$web_flow" || $primary == "$web_flow") ]]
}

# Checks that every check on a PR passed (or was skipped) and every commit from main to <head> has a good signature
# from an allowed key.
check_pr() {
  local number=$1 head=$2 commit out status=0
  # All checks, not only the required ones: a check that isn't required (zizmor's code scanning results, CodeQL)
  # still blocks the merge when it fails. gh exits 8 while checks are still running.
  out=$(gh_retry pr checks "$number") || status=$?
  if [[ $status -ne 0 ]]; then
    printf '%s\n' "$out" | grep -v -P '\t(pass|skipping)\t' >&2 || true
    echo "PR #$number: not every check has passed (gh pr checks $number)." >&2
    exit "$status"
  fi
  git fetch --quiet origin main "$head"
  local signers
  signers=$(allowed_signers)
  for commit in $(git rev-list origin/main.."$head"); do
    if ! signed_by_allowed_key "$commit" "$signers"; then
      echo "PR #$number: commit $commit has no good signature from an allowed key (WEESITE_MERGE_SIGNERS; GitHub's web-flow key only for Dependabot): $(git log -1 --format='%G? %GF, author %ae' "$commit")" >&2
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

# Merge bottom-up, one push per PR. GitHub retargets a PR to main only once the PR below it has merged, and it refuses
# to change the base of a PR that's part of a stack, so a single push to the top can't merge the whole stack. Each push
# starts the workflows on main and cancels the run before; infra-apply compares with its last successful run, so it
# still sees every change (F-0048). After a merge GitHub usually retargets the next PR within seconds; if it hasn't
# within ~30 s, try it here (outside a stack it works).
for i in "${!chain[@]}"; do
  number=${chain[$i]}
  for _ in 1 2 3 4 5 6; do
    read -r head base state _ < <(pr_info "$number")
    [[ $base == main ]] && break
    sleep 5
  done
  if [[ $base != main ]]; then
    echo "Retargeting #$number from $base to main."
    gh_retry pr edit "$number" --base main >/dev/null
  fi
  if [[ $head != "${heads[$i]}" ]]; then
    echo "PR #$number changed while merging (head is now ${head:0:7}); stopping. Run it again." >&2
    exit 1
  fi
  push_main "$number" "$head"
done
echo "Merged the stack: ${chain[*]/#/#}."
