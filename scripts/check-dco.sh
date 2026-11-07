#!/bin/sh
# DCO check for CI: every non-merge commit in <base>..<head> must carry a
# 'Signed-off-by:' line whose email matches the commit author's email.
# Usage: scripts/check-dco.sh <base-sha> <head-sha>
#
# Dependabot's commits are exempt: Dependabot can't sign off, and its
# commits are authored by dependabot[bot] and committed (and signed) by
# GitHub. Both emails must match, and the main ruleset requires verified
# signatures, which only GitHub's key gives a noreply@github.com committer,
# so a commit that merely claims Dependabot as its author can't merge.
set -eu

dependabot_author='49699333+dependabot[bot]@users.noreply.github.com'
github_committer='noreply@github.com'

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

  committer_email=$(git log -1 --format='%ce' "$sha" | tr '[:upper:]' '[:lower:]')

  if [ "$author_email" = "$dependabot_author" ] && [ "$committer_email" = "$github_committer" ]; then
    echo "exempt  $sha $subject (Dependabot)"
  elif printf '%s\n' "$signoffs" | grep -Fxq "$author_email"; then
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
