# Weesite work tracker

The single list of what's done, in progress and next. Agents keep it current; humans skim it. Rules are in [AGENTS.md](../../AGENTS.md#work-tracker); scope and decisions are in the [brief](../design/brief.md) and the [ADRs](../adr/README.md).

Legend: [ ] todo · [~] in progress · [x] done · [-] dropped · [!] blocked/needs a decision

## Now

<!-- the next 3–5 items, in order; agents pick work from here -->

1. T-0011 `infra/bootstrap` (with T-0012's recipes)
2. T-0023 .NET 11 solution skeleton

## Phase 0 — Bootstrap

Done 2026-11-07: public repo with signed, DCO-checked history; lint, zizmor and dco CI as required checks; settings and rulesets as code; community files, agent instructions, tracker and baseline ADR; missing tools installed.

### M0.1 Repository and tooling

- [x] T-0001 Repository skeleton: community files, agent instructions, layout and placeholders, baseline [ADR 0001](../adr/0001-baseline.md), design brief — 2026-11-07, bootstrap commit
- [x] T-0002 Lint tooling: justfile, lefthook hooks (linters, gitleaks, DCO, Conventional Commits), `lint` workflow with `lint`, `zizmor` and `dco` jobs — 2026-11-07, bootstrap commit
- [x] T-0003 Repository settings as code: `scripts/github-settings.sh` with `--check`, rulesets in `.github/rulesets/`, `scripts/ff-merge.sh` behind `just merge` — 2026-11-07, bootstrap commit
- [x] T-0004 Create the public GitHub repository (empty, no template) — 2026-11-07
- [x] T-0005 Apply the settings, security features, Actions policy, labels, topics and rulesets (`just github-settings`), then read them back (`just github-settings-check`): all match, including the SHA pinning policy — 2026-11-07, [PR #2](https://github.com/gergodrazsdik/weesite/pull/2) (F-0007)
- [x] T-0006 Run `lint` and `dco` once, then make them required checks on `main` (GitHub Actions app, id 15368) — 2026-11-07, [PR #2](https://github.com/gergodrazsdik/weesite/pull/2), a real follow-up PR instead of a throwaway smoke test
- [x] T-0007 Code of Conduct enforcement contact: the maintainer's address — 2026-11-07, bootstrap commit

### M0.2 Missing tools (maintainer's machine)

- [x] T-0008 Install zizmor (`just lint` skips it locally until then; CI runs it) — 2026-11-07, zizmor 1.31.1, the version the `lint` job pins
- [x] T-0009 Install the Aspire CLI (before T-0025) — 2026-11-07, Aspire CLI 13.6.4 (.NET global tool)
- [x] T-0010 Install Playwright's Chromium (with the end-to-end tests, T-0026) — 2026-11-07, Chromium 156 for Playwright 1.64.1, the version TUnit.Playwright 1.81.10 uses; T-0026 installs the build matching the version the tests pin

## Phase 1 — Foundation

### M1.1 Infrastructure bootstrap and policy

- [ ] T-0011 `infra/bootstrap`: state storage account (versioning, soft delete, Entra ID only) with one container per environment; Key Vault (RBAC, purge protection) with one state-encryption key per environment; `test` and `prod` resource groups; plan identity (read-only, federated on PRs and `main`) and one apply identity per environment (federated on `environment:test`/`environment:prod`, Contributor on its resource group, RBAC Administrator constrained to the Storage Blob/Table/Queue data roles), immutable `repo:owner@id/repo@id` subjects; GitHub environments with protection rules, variables and secrets through the OpenTofu GitHub provider (never created by hand); budget alert; `bootstrap.auto.tfvars.example`
- [ ] T-0012 Recipes `validate`, `bootstrap-plan`, `bootstrap-apply`, `bootstrap-migrate`; add `tofu validate` and TFLint to `just lint`
- [ ] T-0013 [!] First bootstrap apply and state migration — maintainer only
- [x] T-0014 Rego baseline in `policy/` (HTTPS/TLS 1.2, no public blob access, shared keys off, Key Vault purge protection, managed identities, required tags, allowed regions) with `conftest verify` tests and inline waivers; `policy-test` recipe — 2026-11-07, branch `infra/policy-baseline`: 11 rules written once for the HCL and the plan JSON, 40 tests, `# policy:waive` comments collected by `scripts/policy-waivers.sh`; `policy-test`, `policy-check` and `lint-rego` run in `just lint` and the `lint` job (F-0009)
- [ ] T-0015 `infra-static` workflow: `tofu fmt -check`, `tofu validate`, TFLint, Checkov, conftest, `conftest verify`; runs on every PR (forks included) with no credentials; always runs and skips steps when nothing under `infra/` or `policy/` changed
- [ ] T-0016 `infra-plan` workflow: plan per environment with the read-only identity, plan JSON through conftest and Checkov, change summary (addresses and counts only) to the job summary and a PR comment
- [ ] T-0017 `infra-apply` workflow: plan `main`, wait for the environment's approval, apply that saved plan, test then prod
- [ ] T-0018 `drift` workflow: weekly plan of both environments, opens an issue on drift
- [ ] T-0019 Make `infra-static` and `infra-plan` required checks in `.github/rulesets/main.json`
- [ ] T-0020 Dependabot `terraform` (check it picks up the OpenTofu lock file)
- [ ] T-0021 Fill in `docs/deploy.md`

### M1.2 Application skeleton

- [x] T-0022 Decide Q-0001 (formatter) before T-0023 — 2026-11-07: `dotnet format`
- [ ] T-0023 .NET 11 solution: `global.json` (11.0, `rollForward: latestFeature`, `allowPrerelease: true` until GA on 2026-11-10), `.slnx`, `Directory.Build.props` (artifacts output, `AnalysisMode=All`, warnings as errors), `Directory.Packages.props`; `Weesite.Web`, `Weesite.AppHost`, `Weesite.ServiceDefaults`; TUnit test projects
- [ ] T-0024 `.editorconfig`: .NET naming and style rules
- [ ] T-0025 Aspire AppHost with Azurite, seed content and a placeholder identity; `.mcp.json` with the Playwright and Aspire MCP servers (`aspire mcp init`)
- [ ] T-0026 Recipes `build`, `test`, `aspire`, `screens`; TUnit.Playwright end-to-end tests with axe and screenshot snapshots
- [ ] T-0027 Containerfile (hadolint) and the `app` workflow: build, test, `dotnet list package --vulnerable --include-transitive`, grype and syft, push to GHCR by commit SHA, deploy to test
- [ ] T-0028 Dependabot `nuget` and `docker`; dependency review on PRs
- [ ] T-0029 CodeQL: add `csharp` to the default setup (`scripts/github-settings.sh`)
- [ ] T-0030 `infra/app` modules: storage, Container Apps environment, app and job, Key Vault, Application Insights
- [ ] T-0031 First deploy to test; `promote` workflow to prod

## Phase 2 — Public site

- [ ] T-0032 Content model (posts and pages, front matter, `lang`, translation group, authors), localization plumbing (`IStringLocalizer`, `en` only)
- [ ] T-0033 Markdown pipeline (Markdig, sanitized, server-side code highlighting) and media endpoint (`/media/{hash}-{name}`, `nosniff`, non-images as downloads)
- [ ] T-0034 Image pipeline: EXIF stripping, WebP variants with `srcset`, required alt text (library: Q-0002)
- [ ] T-0035 Posts, content pages, one-level menu, configurable home page, slug redirects
- [ ] T-0036 Design tokens, base design, light/dark/system theming with the cookie switch and `/theme.css`
- [ ] T-0037 Tags, reading time, Open Graph cards
- [ ] T-0038 SEO and feeds: canonical URLs, JSON-LD, sitemap, robots.txt, RSS/Atom per site and tag
- [ ] T-0039 Agent support: `llms.txt`, `llms-full.txt`, Markdown negotiation (`Accept: text/markdown`, `.md` URLs, `Vary: Accept`)

## Phase 3 — Search and agents

- [ ] T-0040 In-memory FTS5 index (BM25, snippets)
- [ ] T-0041 ONNX embedding pipeline (`bge-small-en-v1.5`, int8) and vector storage in Blob
- [ ] T-0042 Hybrid ranking with RRF; search page and search box; cost guard (query cap, cache, concurrency limit)
- [ ] T-0043 Related posts from the embeddings
- [ ] T-0044 MCP endpoint (`search_content`, `get_content`)
- [ ] T-0045 Re-index job: Container Apps job triggered by a Storage queue

## Phase 4 — Editor and admin

- [ ] T-0046 Sign-in (Google, Microsoft), allowlist, roles, audit log
- [ ] T-0047 Web components: `ws-md-editor`, `ws-media-picker`, `ws-menu-editor`, `ws-theme-editor`, `ws-theme-switch`
- [ ] T-0048 Publishing workflow: drafts with autosave, preview, scheduling, revisions, ETag conflicts
- [ ] T-0049 Site settings (identity, contact, social links), menu and theme editors with contrast checks
- [ ] T-0050 HTML export (clipboard and file, change report)
- [ ] T-0051 Export/import zip
- [ ] T-0052 Analytics: page-view logging middleware and the admin dashboard (a launch requirement in the brief)

## Phase 5 — Hardening and launch

- [ ] T-0053 Security headers, strict CSP, upload checks, rate limits
- [ ] T-0054 Monitoring: Application Insights with a daily cap, availability alert; readiness probe after the index and model load
- [ ] T-0055 Backups: soft delete and versioning verified, restore tested
- [ ] T-0056 Output caching, compression, performance targets (Lighthouse ≥ 95, LCP < 1.5 s) checked against test
- [ ] T-0057 Domain registration, Azure DNS zone, custom domains and managed certificates
- [ ] T-0058 Privacy policy page; go-live

## Phase 6 — After launch

- [ ] T-0059 Analytics forwarding (optional, off in test)
- [ ] T-0060 Hungarian localization (`/hu/`, `hreflang`, multilingual embedding model)
- [ ] T-0061 Freelance pages (services, availability, contact) as content pages

## Findings and follow-ups

<!-- problems discovered during work, ideas parked for later; ID F-NNNN -->

- F-0001 `docs/design/brief.md` is committed as exported from the design doc and names the first deployment (its domain and resource names). It is the only committed file that may; it isn't a model for code or docs. An export artefact (an embedded-diagram placeholder) was removed.
- F-0002 The CI `lint` job pins linter versions in `.github/workflows/lint.yml`. Dependabot only updates the actions, so linter versions are bumped by hand.
- F-0003 pinact v5 needs Go 1.27, so the `lint` job sets up Go 1.27 (the other Go tools build with it too).
- F-0004 Project settings can't add sandbox `excludedCommands`. Recipes that call `gh` or `git push` (`just merge`, `just github-settings`, `just github-settings-check`) and the bootstrap recipes must be excluded in each maintainer's user-level Claude Code settings to run from a sandboxed session.
- F-0005 Tag rulesets protect `v*` tags from creation, update and deletion by anyone but admins, but GitHub can't check a tag object's own signature: verify release tags with `git verify-tag`.
- F-0006 zizmor's `adhoc-packages` audit flags the `npm install -g markdownlint-cli2` in the `lint` job (pipx and `go install` aren't flagged). It's pinned to an exact version and suppressed inline; zizmor only reads ignore comments as YAML comments, so the install is its own one-line step. Revisit if more npm tools are added: a `package.json` with a lockfile and `npm ci` would remove the waiver.
- F-0007 Applying the settings: GitHub rejects `allow_forking` (HTTP 422) on public personal repos, which always allow forks, so the script only checks it; and GitHub's API timed out several times, so the script retries like `ff-merge.sh`. GitHub's own `accessibility` label and Dependabot's `github_actions` label are kept.
- F-0008 Dependabot opened [PR #1](https://github.com/gergodrazsdik/weesite/pull/1) (zizmor-action 0.8.0 → 0.8.1) minutes after the first push, for a release only four days old. `dependabot.yml` now has a 14-day cooldown; PR #1 is the maintainer's to merge or close.
- F-0009 Policy baseline: the HCL tier judges only literal values (expressions are left to the plan tier), and the default allowed locations and required tag keys in `policy/data/policy.yaml` are a starting point each deployment edits. A waiver matches by resource type and name across the root's modules, so `policy-check` runs one root at a time. The `lint` job already runs `conftest verify`; T-0015 adds conftest on the HCL and plan.

## Open questions

<!-- questions for the maintainer; ID Q-NNNN. Answered ones keep the answer and date. -->

- Q-0001 Formatter for C#: `dotnet format` (no extra tool) or CSharpier as a local tool with format-on-save? Decide before T-0023. **Answered 2026-11-07:** `dotnet format`; style comes from `.editorconfig` (T-0024).
- Q-0002 Image library: SkiaSharp (MIT) or NetVips (libvips); ImageSharp's licence rules it out (brief, Images and attachments). Decide before T-0034.
- Q-0003 The brief assumes comments and a newsletter are out of scope, "not yet confirmed". Confirm.
- Q-0004 Restrict test ingress to the maintainer's IP addresses (at the cost of smoke tests from GitHub-hosted runners)? Decide with T-0030.

## Decisions

<!-- one line per decision with a link to its ADR -->

- 2026-11-07 Baseline from the design brief (stack, hosting, public repo, own bootstrap, two-tier infra CI with a local Rego baseline, Apache-2.0 + DCO, in-app sign-in, in-process hybrid search, generic engine, platform frontend, HTML export): [ADR 0001](../adr/0001-baseline.md)
