# Agent instructions: Weesite

These instructions are for coding agents (Claude Code, Codex and others) working in this repo. Humans: see [CONTRIBUTING.md](CONTRIBUTING.md).

## What Weesite is

A small, open-source personal-site engine: one ASP.NET Core 11 app (Blazor static SSR) on Azure Container Apps, with hybrid search, feeds, agent support (`llms.txt`, Markdown negotiation, MCP), a private editor and OpenTofu infrastructure with its own bootstrap.

Where the truth lives:

- [docs/design/brief.md](docs/design/brief.md): scope, stack and decisions. Read the relevant sections before any non-trivial work. It names the first deployment; that's the one exception to the rules below, not a model.
- [docs/adr/](docs/adr/README.md): decisions that refine or supersede the brief.
- [docs/plan/TRACKER.md](docs/plan/TRACKER.md): what's done and what's next.

**Current phase: 0 → 1.** The repo skeleton exists; there is no application or infrastructure code yet.

## Layout

```text
AGENTS.md CLAUDE.md README.md CONTRIBUTING.md SECURITY.md SUPPORT.md CHANGELOG.md LICENSE NOTICE
justfile  local.just.example  lefthook.yml  .editorconfig  .tflint.hcl  lint configs
.claude/settings.json      committed Claude Code sandbox and deny rules
.github/                   workflows/, rulesets/, issue and PR templates, CODEOWNERS, dependabot.yml
docs/design/brief.md       the design brief (exported; not edited by agents)
docs/adr/                  architecture decision records
docs/plan/TRACKER.md       work tracker
docs/dev-setup.md          prerequisites and local setup
docs/deploy.md             "Deploy your own Weesite"
infra/bootstrap/           one-off OpenTofu root: state, keys, identities, GitHub environments
infra/app/                 the site's resources, one state per environment
policy/                    Rego baseline and its conftest tests
scripts/                   lint helpers, DCO/commit checks, ff-merge.sh, github-settings.sh
src/  tests/               the .NET solution and its tests (Phase 1)
```

Keep this tree current when you add top-level folders.

## Working agreement

- Work on feature branches (`feat/…`, `fix/…`, `docs/…`, `ci/…`, `infra/…`). **Never commit to `main`.**
- Push branches and open PRs with `gh`. The description follows the PR template: summary, tracker IDs, tests, screenshots for UI changes. **Never merge PRs**; the maintainer merges with `just merge <pr>` (fast-forward, so signatures survive).
- Conventional Commits (`feat:`, `fix:`, `docs:`, `chore:`, `ci:`, `build:`, `test:`…); small, logical commits.
- **DCO and signatures:** always `git commit -s`, with commit signing enabled; never disable signing or pass `--no-verify`. Write the message to a file outside the repo and commit with `git commit -s -F <file>`. Tags are annotated and signed, and release tags are the maintainer's only: agents never create, move or delete tags.
- After a PR merges, rebase remaining branches onto `main`.
- Ask before any GitHub write other than pushing your branch and creating or editing your PRs (comments, reviews, closing, labels, settings, force pushes outside your own stack).

## Stacked PRs

When one change builds on another that isn't merged yet, open the PRs as a native GitHub stack with the `gh stack` extension ([about stacked PRs](https://docs.github.com/en/pull-requests/get-started/about-stacked-prs), [commands](https://docs.github.com/en/pull-requests/reference/stacked-prs-cli-commands)), not as PRs based on each other's branches. GitHub then retargets the rest of the stack when a PR merges.

- Create the branches with `gh stack init <branch>…` (or `gh stack add <branch>` on the top branch), and commit to each as usual.
- Publish with `gh stack submit --auto --open` (without `--auto` it opens an interactive editor), then give each PR its real title and body with `gh pr edit --title … --body-file <file>`.
- The maintainer merges bottom-up. Afterwards, `gh stack sync --prune` updates trunk and the remaining branches locally and deletes the merged ones.
- `sync` and `rebase` may force-push the stack's own branches (with lease). `link`, `merge` and `unstack` write to GitHub: ask first. Never run `gh stack merge`.

## Commands

Use `just` (`just --list`):

| Recipe | What it does |
|---|---|
| `just doctor` | Check tools and git settings; prints install hints. |
| `just hooks` | Install the lefthook git hooks. |
| `just lint` | All linters: Markdown, YAML, actionlint, zizmor, pinact, shellcheck, typos, editorconfig, gitleaks; `tofu fmt` and hadolint once there are files for them. |
| `just pin` | Pin workflow actions to full SHAs. |
| `just claude` | Start Claude Code in this repo. |
| `just merge <pr>` | Maintainer only. Fast-forward `main` to a PR's head. |
| `just github-settings <repo>` / `just github-settings-check <repo>` | Maintainer only. Apply or check the repo settings and rulesets. |

Run `just lint` before every commit. The hooks run the same checks on staged files plus gitleaks, the DCO sign-off and the Conventional Commits header. `build`, `test`, `screens` and `aspire` arrive with the code; `validate`, `policy-test` and the `bootstrap-*` recipes with the infrastructure.

## Code principles

- **Generic and configurable.** No personal or deployment data in code, config, tests, docs or commit messages: no names, domains, hostnames, email addresses, Azure IDs or resource names of a deployment. Site identity is site settings; deployment values are GitHub environment variables and secrets. Seed content uses a placeholder identity ("Example Author"). The project name `Weesite` (`Weesite.*` namespaces, `weesite` image and resource names) is fine.
- **Modern platform, no frameworks.** HTML, CSS and JavaScript platform features (Baseline "widely available"), web components with the `ws-` prefix in the admin, ES modules through an import map, no bundler, no frontend framework.
- **The public site works without JavaScript.** Scripts only enhance.
- **Strict CSP:** no inline scripts or styles, ever, including in Razor components.
- **Security and accessibility by default:** sanitized Markdown, antiforgery on forms, upload checks, semantic HTML, visible focus, WCAG AA contrast.

## Infrastructure

- CI enforces the repo's own Rego baseline in `policy/`, plus TFLint and Checkov. New rules come with `conftest verify` tests. An exception needs an inline waiver with a reason.
- Design guidance (not enforced in CI): the Cloud Adoption Framework's [naming](https://learn.microsoft.com/azure/cloud-adoption-framework/ready/azure-best-practices/resource-naming), [resource abbreviations](https://learn.microsoft.com/azure/cloud-adoption-framework/ready/azure-best-practices/resource-abbreviations) and [tagging](https://learn.microsoft.com/azure/cloud-adoption-framework/ready/azure-best-practices/resource-tagging); the [Microsoft cloud security benchmark](https://learn.microsoft.com/security/benchmark/azure/overview) and the [Container Apps security baseline](https://learn.microsoft.com/security/benchmark/azure/baselines/azure-container-apps-security-baseline); the [OpenTofu style conventions](https://opentofu.org/docs/language/syntax/style/); the [Azure Verified Modules specifications](https://azure.github.io/Azure-Verified-Modules/specs/module-specs/) as guidance.
- Deployment-specific values go in GitHub environment variables (`TF_VAR_*`), Azure IDs in environment secrets, app secrets in the environment's Key Vault.
- Never post a full plan in a PR, issue or comment: the repo is public. Resource addresses and change counts only.
- Agents never run `tofu apply`, `just bootstrap-apply` or `just bootstrap-migrate`, and never sign in to Azure on their own. `tofu plan` only when the maintainer asks.

## Testing

- All tests use TUnit; browser tests use TUnit.Playwright with role- and label-based locators and no sleeps.
- Add or update tests with every behaviour change.
- After any UI change, run `just screens` and look at the screenshots before calling the change done (once the recipe exists).

## Work tracker

[docs/plan/TRACKER.md](docs/plan/TRACKER.md) is the single tracker. Rules:

- IDs are never reused: `T-NNNN` tasks, `F-NNNN` findings, `Q-NNNN` questions.
- Update the tracker **in the same PR** as the work it tracks.
- Finished items stay in place, ticked, with date and commit or PR link. Dropped items (`[-]`) keep a one-line reason. When a phase completes, add a one-line summary under its heading.
- Plan from **Now**, in order. Propose before starting anything larger than one PR. Ask the maintainer about `[!]` items.
- New findings and questions go into the tracker immediately, not only into chat.
- Don't add a "last updated" line: git has that.

## Local overrides

`local.just`, `lefthook-local.yml`, `CLAUDE.local.md`, `.claude/settings.local.json`, `.env.local` and `*.auto.tfvars` are personal and gitignored. Never commit them, and never put machine-specific paths, tools or secret-manager commands into committed files.

## Writing style

Precise, concise, plain English. No marketing language. Docs say what something does and when to use it.
