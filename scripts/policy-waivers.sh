#!/bin/sh
# Collect the inline policy waivers from OpenTofu files and print them as
# conftest data ({"waivers": [...]}) for `conftest test --data`.
# Usage: scripts/policy-waivers.sh <dir>... > waivers.json
#
# A waiver is a comment directly above a resource block (other comment
# lines may sit between them), naming one rule and giving a reason:
#
#   # policy:waive storage-no-public-blob The site's media container is public by design.
#   resource "azurerm_storage_container" "media" {
#
# It covers that rule for every resource with that type and name in the
# directories given, so check one root (and its modules) at a time.
# A waiver without a reason, or not followed by a resource, is an error.
set -eu

if [ "$#" -eq 0 ]; then
  echo "usage: $0 <dir>..." >&2
  exit 2
fi
for dir in "$@"; do
  [ -d "$dir" ] || { echo "$0: not a directory: $dir" >&2; exit 2; }
done

find "$@" -type d -name .terraform -prune -o -type f \( -name '*.tf' -o -name '*.tofu' \) -print |
  LC_ALL=C sort |
  awk '
    function fail(msg) {
      printf "%s\n", msg > "/dev/stderr"
      failed = 1
      exit 1
    }
    function json(s) {
      gsub(/\\/, "\\\\", s)
      gsub(/"/, "\\\"", s)
      gsub(/\t/, " ", s)
      return "\"" s "\""
    }
    function flush_pending(file, lineno) {
      if (npending > 0) {
        fail(file ":" pending_at[1] ": policy:waive is not directly above a resource block")
      }
    }
    BEGIN { n = 0 }
    {
      file = $0
      npending = 0
      lineno = 0
      while ((getline line < file) > 0) {
        lineno++
        if (line ~ /^[ \t]*#[ \t]*policy:waive([ \t]|$)/) {
          rest = line
          sub(/^[ \t]*#[ \t]*policy:waive[ \t]*/, "", rest)
          rule = rest
          sub(/[ \t].*$/, "", rule)
          reason = rest
          sub(/^[^ \t]*[ \t]*/, "", reason)
          sub(/[ \t]+$/, "", reason)
          if (rule == "" || reason == "") {
            fail(file ":" lineno ": policy:waive needs a rule ID and a reason")
          }
          npending++
          pending_rule[npending] = rule
          pending_reason[npending] = reason
          pending_at[npending] = lineno
        } else if (line ~ /^[ \t]*(#|\/\/)/) {
          continue
        } else if (line ~ /^[ \t]*resource[ \t]+"[^"]+"[ \t]+"[^"]+"/) {
          split(line, parts, "\"")
          for (i = 1; i <= npending; i++) {
            entry[++n] = sprintf("{\"rule\":%s,\"resource\":%s,\"reason\":%s,\"at\":%s}",
              json(pending_rule[i]), json(parts[2] "." parts[4]),
              json(pending_reason[i]), json(file ":" pending_at[i]))
          }
          npending = 0
        } else {
          flush_pending(file, lineno)
        }
      }
      close(file)
      flush_pending(file, lineno)
    }
    END {
      if (failed) exit 1
      printf "{\"waivers\":["
      for (i = 1; i <= n; i++) printf "%s%s", (i > 1 ? "," : ""), entry[i]
      printf "]}\n"
    }
  '
