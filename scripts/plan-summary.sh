#!/bin/sh
# Summarise a plan JSON (`tofu show -json`) as Markdown: change counts and
# resource addresses only, never attribute values. The repo is public, so
# this is all a PR comment, job summary or issue may show of a plan.
# Usage: scripts/plan-summary.sh <plan.json> <title>
set -eu

if [ "$#" -ne 2 ]; then
  echo "usage: $0 <plan.json> <title>" >&2
  exit 2
fi

jq -r --arg title "$2" '
  def kind:
    if . == ["create"] then "add"
    elif . == ["update"] then "change"
    elif . == ["delete"] then "destroy"
    elif (. == ["delete", "create"] or . == ["create", "delete"]) then "replace"
    else "other" end;
  def symbol:
    {"add": "+", "change": "~", "destroy": "-", "replace": "-/+", "other": "?"}[.];
  [ .resource_changes[]?
    | select(.change.actions != ["no-op"] and .change.actions != ["read"])
    | {address, kind: (.change.actions | kind)} ] as $changes
  | ($changes | map(select(.kind == "add")) | length) as $add
  | ($changes | map(select(.kind == "change")) | length) as $change
  | ($changes | map(select(.kind == "replace")) | length) as $replace
  | ($changes | map(select(.kind == "destroy")) | length) as $destroy
  | "#### \($title)\n",
    if ($changes | length) == 0 then
      "No changes."
    else
      "**\($add) to add, \($change) to change, \($replace) to replace, \($destroy) to destroy**\n",
      "<details><summary>Resources (\($changes | length))</summary>\n",
      "| | Address |",
      "| --- | --- |",
      ($changes[] | "| `\(.kind | symbol)` | `\(.address)` |"),
      "\n</details>"
    end
' "$1"
