#!/bin/sh
# Check that a deployed site ignores a client-sent X-Forwarded-For (T-0122): the rate limits
# count per client address, and an app that took the client's own entry could be passed by
# sending a new address with every request.
# Usage: scripts/check-forwarded-for.sh <host>
#
# Posts to /admin/sign-in (limited to 10 a minute per address; without a form, each answer is
# 400) with a different made-up address each time. The ingress adds the real one, which the app
# reads, so a 429 has to come within the limit. Each replica counts on its own: 40 tries cover
# up to three. This runner's address then waits out the minute; nobody signs in from it.
set -eu

if [ "$#" -ne 1 ]; then
  echo "usage: $0 <host>" >&2
  exit 2
fi
host=$1
tries=40

for i in $(seq 1 "$tries"); do
  # A timeout or a dropped connection counts as one more try, not the end of the deploy.
  status=$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 -X POST \
    -H "X-Forwarded-For: 203.0.113.$i" "https://$host/admin/sign-in") || status=000
  if [ "$status" = 429 ]; then
    echo "check-forwarded-for: limited after $i posts with made-up addresses: X-Forwarded-For from the client is ignored"
    exit 0
  fi
done

echo "check-forwarded-for: no 429 in $tries sign-in posts with made-up X-Forwarded-For addresses: the app may be counting the client's own entry, or RateLimits__SignInPerMinute is raised" >&2
exit 1
