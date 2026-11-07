#!/bin/sh
# DCO check for CI: every non-merge commit in <base>..<head> must carry a
# 'Signed-off-by:' line whose email matches the commit author's email.
# Usage: scripts/check-dco.sh <base-sha> <head-sha>
set -eu

base=$1
head=$2
failed=0
count=0

for sha in $(git rev-list --no-merges "$base..$head"); do
  count=$((count + 1))
  author_email=$(git log -1 --format='%ae' "$sha" | tr '[:upper:]' '[:lower:]')
  subject=$(git log -1 --format='%s' "$sha")
  signoffs=$(git log -1 --format='%(trailers:key=Signed-off-by,valueonly)' "$sha" |
    sed -n 's/.*<\([^>]*\)>.*/\1/p' | tr '[:upper:]' '[:lower:]')

  if printf '%s\n' "$signoffs" | grep -Fxq "$author_email"; then
    echo "ok      $sha $subject"
  else
    echo "MISSING $sha $subject (no Signed-off-by for $author_email)"
    failed=1
  fi
done

echo "checked $count commit(s)"
if [ "$failed" -ne 0 ]; then
  cat <<'EOF'

Some commits lack a DCO sign-off matching the author. Fix with:
  git rebase --signoff <base>   # adds your sign-off to each commit
  git push --force-with-lease
See CONTRIBUTING.md "Developer Certificate of Origin".
EOF
  exit 1
fi
