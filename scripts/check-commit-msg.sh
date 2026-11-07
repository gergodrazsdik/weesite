#!/bin/sh
# commit-msg checks (run by lefthook): DCO sign-off, Conventional Commits
# header, and a warning when commit signing is not enabled locally.
# Usage: scripts/check-commit-msg.sh <commit-message-file>
set -eu

msg_file=$1
# Ignore comment lines that git strips later.
msg=$(grep -v '^#' "$msg_file" || true)
header=$(printf '%s\n' "$msg" | sed -n '1p')

case $header in
  "Merge "* | "Revert \""* | "fixup! "* | "squash! "* | "amend! "*)
    # Generated or temporary messages: squashed away before merge.
    exit 0
    ;;
esac

status=0

types='build|chore|ci|docs|feat|fix|perf|refactor|revert|style|test'
if ! printf '%s\n' "$header" | grep -Eq "^($types)(\([a-z0-9._/-]+\))?!?: [^ ].*"; then
  cat >&2 <<EOF
error: the commit header is not a Conventional Commit:
  $header
Use '<type>[(scope)][!]: <summary>', with type one of:
  ${types}
Example: docs(adr): record the search design
EOF
  status=1
fi

if ! printf '%s\n' "$msg" | grep -Eq '^Signed-off-by: .+ <[^>]+@[^>]+>$'; then
  cat >&2 <<'EOF'
error: missing DCO sign-off. Commit with 'git commit -s' to add a
'Signed-off-by: Your Name <you@example.com>' line (see CONTRIBUTING.md).
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
