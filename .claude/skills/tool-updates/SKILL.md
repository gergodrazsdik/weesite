---
name: tool-updates
description: Check and take updates of the tool versions Weesite pins outside Dependabot's reach (the lint, infra and load-test tools in the workflows, Checkov's lock, esbuild in the Containerfile), with the 7-day cooldown (14 for a major version). Use when asked what needs updating or when, to take tool updates (T-0182), or after the weekly tool-updates run or its issue reports something due.
---

# Tool updates

Weesite takes a new version once it's **7 days old**, a new major version once it's **14 days old**, as Dependabot's cooldown and `nuget.config`'s `minPublishAgeHours` do (since 2026-11-23; 14 days for everything before). A newer release that's still waiting doesn't hold back an older one that's due: take the newest one past its wait. Taking one sooner is the maintainer's call: say how many days short it is and let them decide.

## Who covers what

- **Dependabot** (`.github/dependabot.yml`): the actions in the workflows and in `.github/actions/*`, the base images in the `Containerfile` (by digest), NuGet packages, and the OpenTofu providers. Don't update these by hand: its grouped PRs come weekly, past the cooldown.
- **This skill**: the versions written as plain strings, which no bot reads. `scripts/tool-updates.py` lists them in `PINS`, with where each is pinned and where its releases are.
- **The maintainer's machine** (the tools the local hooks and `just` run): the home-directory session's `update-sources` skill. Never install or update machine-wide tools from a project session; say which ones differ from the pins.

## 1. Check

```sh
just tool-updates            # scripts/tool-updates.py --local
```

Run it **outside the sandbox**: it uses the GitHub CLI's sign-in for GitHub's API (without one, 60 requests an hour, which a couple of runs use up). Each pin gets:

- `current`: nothing newer.
- `update`: a newer release is 7 days old or more (a major 14). The detail names the newest such one, and what else changes with it.
- `wait`: a newer release isn't old enough yet. The detail says when it's due: the one due first, and the newest when that's another.
- `error`: its source couldn't be read. Say so; don't guess.

`--local` adds this machine's versions. A machine ahead of the pin is normal (it updates on its own schedule); behind, the local hooks lint with an older tool than CI.

The weekly `tool-updates` workflow runs the same check into its summary, and with the repository variable `TOOL_UPDATES_ISSUE=true` keeps one "Tool updates due" issue.

## 2. Update

One PR per tool, or a few related ones together, stacked (`gh stack`), with a `ci:` or `build:` header. For each:

| Tool | Where | Also |
|---|---|---|
| just, yamllint, typos, markdownlint-cli2, actionlint, editorconfig-checker, gitleaks, pinact, hadolint | `.github/workflows/lint.yml`, `*_VERSION` | just: also `rust-just` in the Checkov lock |
| zizmor | `lint.yml`: `ZIZMOR_VERSION` **and** zizmor-action's `version:` | zizmor-action runs only the versions it knows: take a release that knows the new one (its notes say so), and verify through the action, not only the binary (F-0178) |
| OpenTofu, TFLint, conftest | `.github/actions/setup-infra-tools/action.yml` | run `just infra` (its own call, outside the sandbox) |
| Checkov | `.github/actions/setup-infra-tools/requirements.txt` | regenerate the whole lock with the commands in its header (hashes for everything); check whether Checkov still pins asteval 1.0.6 and whether ecdsa has a fix (T-0132); `grype dir:` an environment installed from it |
| k6 | `.github/workflows/load-test.yml`, `k6-version` | |
| esbuild | `Containerfile`: the URL **and** its `ADD --checksum` | npm publishes SHA-512, not the SHA-256 `ADD` takes: download the tarball, `vscan` it, and take its `sha256sum`; `scripts/install-esbuild.sh` reads both from the Containerfile; run `just minify` |

Then:

- `just lint-core` (and `just infra` for the infra tools); the PR's CI runs the workflows with the new pins.
- The release notes: say in the PR what changed that matters here, and anything breaking.
- `docs/plan/TRACKER.md`: the open tool-updates task (T-0182 from 2026-11-23; T-0135 before) has a subtask per update due: tick it with the date and branch, and add the script's `wait` rows as new subtasks, each with the date it's due.

## 3. A new pin

A tool pinned somewhere new gets a `Pin` in `scripts/tool-updates.py` (name, file, a regex with the version as group `v`, the source: `github:owner/repo`, `pypi:package` or `npm:package`, the command that prints its local version, and a note for anything that changes with it), and a row in the table above. Run the script to see that it reads.
