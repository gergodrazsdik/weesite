# Contributing to Weesite

Thanks for your interest. Weesite is a small personal-site engine with one maintainer, so the process is light, but a few rules are strict: every commit is signed off (DCO) and signed, and nothing personal or deployment-specific goes into the repo.

> **Weesite is pre-alpha.** The design is in [docs/design/brief.md](docs/design/brief.md) and the plan in [docs/plan/TRACKER.md](docs/plan/TRACKER.md). Everything can still change.

## Proposing changes

- **Small fixes** (typos, broken links, an obvious bug): open a pull request directly.
- **Anything non-trivial** (a feature, a dependency, a change to the infrastructure or the design): open an issue first and agree on the approach, so nobody's work is wasted. Check the tracker and the brief: it may already be planned or decided.
- **Security problems:** never in a public issue. See [SECURITY.md](SECURITY.md).

## Branches and pull requests

- Never commit to `main`. Work on a feature branch (`feat/<topic>`, `fix/<topic>`, `docs/<topic>`, `ci/<topic>`…) in your fork or, for maintainers, in the repo.
- Open a pull request using the template: summary, tracker IDs, tests, and screenshots for UI changes.
- `main` advances by **fast-forward**: the maintainer merges with `just merge <pr>`, which moves `main` to your branch's head, so your commits land exactly as you signed them. Keep the branch rebased on `main` (`git rebase main`, then `git push --force-with-lease` to your branch).
- Keep commits small and logical; each one should pass `just lint`. Squash fixups before review is done.
- **Stacked changes:** maintainers and agents use the [`gh stack`](https://docs.github.com/en/pull-requests/get-started/about-stacked-prs) extension for PRs that build on each other. Without it, open ordinary PRs and say in the description which PR yours builds on.

## Developer Certificate of Origin (DCO)

Every commit must carry a sign-off line:

```text
Signed-off-by: Your Name <you@example.com>
```

`git commit -s` adds it, using your `user.name` and `user.email`. By signing off you certify the [Developer Certificate of Origin 1.1](https://developercertificate.org/): in short, that you wrote the change or otherwise have the right to submit it under the project's licence (Apache-2.0). There is no CLA.

The sign-off email must match the commit author's email; the `dco` check runs on every commit of a pull request, and the `commit-msg` hook checks it locally. To add missing sign-offs to a branch, run `git rebase --signoff main` and force-push the branch (`git push --force-with-lease`). Web edits on GitHub need a sign-off too; GitHub adds it when you commit through the web editor. Dependabot's commits are the one exception: authored by `dependabot[bot]` and committed and signed by GitHub, they pass without a sign-off.

## Signed commits and tags

**`main` only accepts signed commits.** GitHub must show each commit in your pull request as "Verified". GPG, SSH and S/MIME signing keys all work:

- [About commit signature verification](https://docs.github.com/en/authentication/managing-commit-signature-verification/about-commit-signature-verification)
- [Generating a new GPG key](https://docs.github.com/en/authentication/managing-commit-signature-verification/generating-a-new-gpg-key) and [telling Git about your signing key](https://docs.github.com/en/authentication/managing-commit-signature-verification/telling-git-about-your-signing-key)
- Upload the public key to your GitHub account as a **signing** key.

Then enable signing for this repo (or globally with `--global`):

```sh
git config commit.gpgSign true
# For SSH keys additionally:
# git config gpg.format ssh
# git config user.signingKey ~/.ssh/id_ed25519.pub
```

Release tags are annotated and signed (`git tag -s vX.Y.Z -m "vX.Y.Z"`) and are created only by the maintainer; a ruleset protects `v*` tags. GitHub can't check a tag object's own signature, so verify a release tag with `git verify-tag`.

## Commit messages

We use [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/): `<type>[(scope)][!]: <summary>`, with type one of `build`, `chore`, `ci`, `docs`, `feat`, `fix`, `perf`, `refactor`, `revert`, `style`, `test`. Examples:

```text
feat(search): merge keyword and vector results with RRF
fix(admin): reject stale saves with the blob's ETag
docs: describe the bootstrap inputs
```

## Local setup

See [docs/dev-setup.md](docs/dev-setup.md) for prerequisites. Then:

```sh
just doctor   # check tools and git settings, with install hints
just hooks    # install the git hooks (pre-commit and commit-msg)
just lint     # run every linter, as CI does
```

The hooks skip a check whose tool is missing and print a clear message; CI runs every check.

### Local overrides

These files are personal and gitignored. Put anything machine-specific or secret-related there, never in committed files:

| File | Purpose |
|---|---|
| `local.just` | Extra recipes, imported by the justfile if present (see `local.just.example`). |
| `lefthook-local.yml` | Personal hook overrides ([lefthook docs](https://lefthook.dev/configuration/)). |
| `CLAUDE.local.md` | Personal notes for Claude Code. |
| `.claude/settings.local.json` | Personal Claude Code settings. |
| `.env.local` | Local environment variables. |
| `*.auto.tfvars` | OpenTofu inputs of your deployment (a commented `.example` is committed). |

## What goes in the repo

Weesite is a generic engine. Committed files never contain a deployment's domain names, hostnames, Azure subscription, tenant or client IDs, resource names, email addresses or secrets. Site identity lives in the site settings, deployment values in GitHub environment variables and secrets, app secrets in Key Vault. Local development uses seed content with a placeholder identity.

## Tests and UI changes

- Add or update tests with every behaviour change (TUnit; browser tests with TUnit.Playwright, once the solution exists).
- UI changes include screenshots at phone and desktop width, in light and dark mode (`just screens` once it exists), and keep the public site working without JavaScript.

## Infrastructure changes

- Changes under `infra/` and `policy/` run the static checks (`tofu fmt`, `tofu validate`, TFLint, Checkov, conftest) on every PR, including PRs from forks. `infra-static`, `infra-plan`, the .NET `build` and `dependency-review` are required checks; without infrastructure changes they pass with their steps skipped.
- Forks get no secrets or cloud credentials, so `tofu plan` doesn't run on fork PRs. Once a maintainer has reviewed the change, they push your branch to the repo, where the plan runs.
- Never paste a full plan into an issue or PR: the repo is public. Resource addresses and change counts are enough.
- `just infra` runs the same static checks locally.
- New policy rules come with `conftest verify` tests; an exception needs an inline waiver with a reason ([policy/README.md](policy/README.md#waivers)).

## Workflow approvals

Workflows from outside contributors need the maintainer's approval before they run. That is a GitHub setting for every external contributor, not a judgement on you.

## AI-assisted contributions

Contributions written with AI coding agents are welcome; much of this project is built that way. You are responsible for every line you submit: review it, understand it, test it. Your DCO sign-off covers AI-assisted changes exactly like any other change. Agent instructions for this repo are in [AGENTS.md](AGENTS.md).

## Code of Conduct

This project follows the [Contributor Covenant](CODE_OF_CONDUCT.md). By participating you agree to uphold it.
