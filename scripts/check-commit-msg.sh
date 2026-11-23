#!/bin/sh
# Commit message checks.
# Usage:
#   scripts/check-commit-msg.sh <commit-message-file>
#     The commit-msg hook (lefthook): a DCO sign-off whose email is the author's
#     (as CI's scripts/check-dco.sh wants it), the Conventional Commits header,
#     and a warning when commit signing is not enabled locally.
#   scripts/check-commit-msg.sh --range <base-sha> <head-sha>
#     CI (the dco job): the Conventional Commits header of every non-merge
#     commit in <base>..<head>, which land on main as they are (F-0244).
set -eu

types='build|chore|ci|docs|feat|fix|perf|refactor|revert|style|test'

# Prints an error and fails when <header> isn't a Conventional Commits header.
check_header() {
  if printf '%s\n' "$1" | grep -Eq "^($types)(\([a-z0-9._/-]+\))?!?: [^ ].*"; then
    return 0
  fi
  cat >&2 <<EOF
error: the commit header is not a Conventional Commit:
  $1
Use '<type>[(scope)][!]: <summary>', with type one of:
  ${types}
Example: docs(adr): record the search design
EOF
  return 1
}

if [ "${1:-}" = --range ]; then
  if [ "$#" -ne 3 ]; then
    echo "usage: $0 --range <base-sha> <head-sha>" >&2
    exit 2
  fi
  failed=0
  count=0
  # Listed first, so that a bad revision fails the check (set -e) instead of checking nothing.
  shas=$(git rev-list --no-merges "$2..$3")
  for sha in $shas; do
    count=$((count + 1))
    header=$(git log -1 --format='%s' "$sha")
    case $header in
      "fixup! "* | "squash! "* | "amend! "*)
        echo "SQUASH  $sha $header (squash it before the merge: git rebase --autosquash)"
        failed=1
        ;;
      *)
        if check_header "$header" 2>/dev/null; then
          echo "ok      $sha $header"
        else
          echo "HEADER  $sha $header (not a Conventional Commit)"
          failed=1
        fi
        ;;
    esac
  done
  echo "checked $count commit(s)"
  if [ "$failed" -ne 0 ]; then
    cat <<EOF

Some commit headers are not Conventional Commits: '<type>[(scope)][!]: <summary>',
with type one of ${types}. Reword them with
  git rebase -i <base>   # 'reword' the commits above
  git push --force-with-lease
See CONTRIBUTING.md "Commit messages".
EOF
    exit 1
  fi
  exit 0
fi

msg_file=$1
# Ignore comment lines that git strips later.
msg=$(grep -v '^#' "$msg_file" || true)
header=$(printf '%s\n' "$msg" | sed -n '1p')

case $header in
  "Merge "* | "fixup! "* | "squash! "* | "amend! "*)
    # Generated or temporary messages: squashed away before merge.
    exit 0
    ;;
  "Revert \""*)
    # git revert's own header lands on main as it is, and CI's header check refuses it (F-0253).
    cat >&2 <<EOF
error: git revert's header isn't a Conventional Commit, and CI refuses it:
  $header
Write it as 'revert: <the reverted commit's header>', and sign off (git commit -s).
EOF
    exit 1
    ;;
esac

status=0

check_header "$header" || status=1

# The author of the commit being made: `git var` follows GIT_AUTHOR_EMAIL, which git sets for its
# hooks on --author and --amend, and falls back to user.email.
author_email=$(git var GIT_AUTHOR_IDENT | sed -n 's/.*<\([^>]*\)>.*/\1/p' | tr '[:upper:]' '[:lower:]')
signoffs=$(printf '%s\n' "$msg" | sed -n 's/^Signed-off-by: .* <\([^>]*@[^>]*\)>$/\1/p' | tr '[:upper:]' '[:lower:]')
if [ -z "$signoffs" ]; then
  cat >&2 <<'EOF'
error: missing DCO sign-off. Commit with 'git commit -s' to add a
'Signed-off-by: Your Name <you@example.com>' line (see CONTRIBUTING.md).
EOF
  status=1
elif ! printf '%s\n' "$signoffs" | grep -Fxq "$author_email"; then
  cat >&2 <<EOF
error: no sign-off for the commit's author, <$author_email>; CI's dco check
wants the sign-off email to be the author's. Commit with 'git commit -s', or
set user.email to the address you sign off with (see CONTRIBUTING.md).
EOF
  status=1
fi

if [ "$(git config --bool commit.gpgSign 2>/dev/null || echo false)" != true ]; then
  cat >&2 <<'EOF'
warning: commit.gpgSign is not enabled. main only accepts signed commits,
so an unsigned commit will be rejected when the PR merges. Enable it with
'git config commit.gpgSign true' (see CONTRIBUTING.md "Signed commits").
EOF
fi

exit "$status"
