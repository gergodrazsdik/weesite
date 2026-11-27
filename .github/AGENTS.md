# Agent instructions: .github/

Workflows, composite actions, rulesets and Dependabot. The rules in the root [AGENTS.md](../AGENTS.md) apply; these add to them.

- Actions are pinned to full SHAs (`just pin`); refer to in-repo actions with the self-repository syntax, `uses: $/.github/actions/<name>`, not `./`. `defaults.run.shell: bash` keeps `pipefail`.
- `just lint-core` runs actionlint, zizmor and pinact. zizmor reads waivers only as YAML comments; zizmor and zizmor-action's `version:` move together (F-0178).
- Tool versions written as plain strings (`lint.yml`, `setup-infra-tools`, `load-test.yml`) aren't Dependabot's: the [tool-updates skill](../.claude/skills/tool-updates/SKILL.md) and `scripts/tool-updates.py` own them. Every new release waits 7 days.
- The `app` workflow skips its jobs on a PR that changes nothing the app depends on (`scripts/app-changed.sh`); pushes to `main` always build. `infra-apply` compares with the last applied commit.
- Repo settings and rulesets are applied by the maintainer (`just github-settings`); the required checks are in `rulesets/main.json`.
- More: [engineering notes](../docs/engineering-notes.md#github-ci-and-dependencies).
