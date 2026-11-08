# Weesite work tracker

The single list of what's done, in progress and next. Agents keep it current; humans skim it. Rules are in [AGENTS.md](../../AGENTS.md#work-tracker); scope and decisions are in the [brief](../design/brief.md) and the [ADRs](../adr/README.md).

Legend: [ ] todo · [~] in progress · [x] done · [-] dropped · [!] blocked/needs a decision

## Now

<!-- the next 3–5 items, in order; agents pick work from here -->

1. T-0036 Design tokens and theming
2. T-0037 Tags, reading time, Open Graph cards

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

- [x] T-0011 `infra/bootstrap`: state storage account (versioning, soft delete, Entra ID only) with one container per environment; Key Vault (RBAC, purge protection) with one state-encryption key per environment; `test` and `prod` resource groups; plan identity (read-only, federated on PRs and `main`) and one apply identity per environment (federated on `environment:test`/`environment:prod`, Contributor on its resource group, RBAC Administrator constrained to the Storage Blob/Table/Queue data roles), immutable `repo:owner@id/repo@id` subjects; GitHub environments with protection rules, variables and secrets through the OpenTofu GitHub provider (never created by hand); budget alert; `bootstrap.auto.tfvars.example` — 2026-11-07, [PR #5](https://github.com/gergodrazsdik/weesite/pull/5): azurerm 5.11, github 6.13; also registers the resource providers `infra/app` needs (F-0010); validated, not applied (T-0013)
- [x] T-0012 Recipes `validate`, `bootstrap-plan`, `bootstrap-apply`, `bootstrap-migrate`; add `tofu validate` and TFLint to `just lint` — 2026-11-07, [PR #5](https://github.com/gergodrazsdik/weesite/pull/5): plus `bootstrap-first-run` and `tflint`; the `lint` job installs OpenTofu 1.13.2 and TFLint 0.65.0
- [x] T-0013 First bootstrap apply and state migration — maintainer only — 2026-11-07: applied, state migrated to the encrypted remote backend, local plaintext copies removed; GitHub environments, secrets, variables and the immutable OIDC subject checked
- [x] T-0014 Rego baseline in `policy/` (HTTPS/TLS 1.2, no public blob access, shared keys off, Key Vault purge protection, managed identities, required tags, allowed regions) with `conftest verify` tests and inline waivers; `policy-test` recipe — 2026-11-07, [PR #4](https://github.com/gergodrazsdik/weesite/pull/4): 11 rules written once for the HCL and the plan JSON, 40 tests, `# policy:waive` comments collected by `scripts/policy-waivers.sh`; `policy-test`, `policy-check` and `lint-rego` run in `just lint` and the `lint` job (F-0009)
- [x] T-0015 `infra-static` workflow: `tofu fmt -check`, `tofu validate`, TFLint, Checkov, conftest, `conftest verify`; runs on every PR (forks included) with no credentials; always runs and skips steps when nothing under `infra/` or `policy/` changed — 2026-11-07, branch `ci/infra-static`: runs `just infra` (Checkov 3.3.30, conftest 0.71.1); the `lint` job runs `just lint-core`; ten Checkov waivers on the bootstrap's state storage and Key Vault (F-0012)
- [x] T-0016 `infra-plan` workflow: plan per environment with the read-only identity, plan JSON through conftest and Checkov, change summary (addresses and counts only) to the job summary and a PR comment — 2026-11-07, branch `ci/infra-plan`: one job (`infra-plan`, a stable required-check name) plans both environments with `-lock=false`; skipped steps on fork PRs and PRs without infrastructure changes; a skeleton `infra/app` (backend, encryption, a read of the environment's resource group) so the plan exercises OIDC, state access and the keys; local actions use the `$/` self-repository syntax (actionlint 1.7.12 doesn't know it yet: ignored in `.github/actionlint.yaml`)
- [x] T-0017 `infra-apply` workflow: plan `main`, wait for the environment's approval, apply that saved plan, test then prod — 2026-11-07, branch `ci/infra-apply`: plan jobs use the plan identity and keep the encrypted plan as a one-day artifact; apply jobs run in the environment with its apply identity; an environment without state is planned and applied in the apply job (F-0013); apply output stays off the log; first runs 2026-11-07 created the test and prod states (prod through the no-state path) and then the prod DNS zone
- [x] T-0018 `drift` workflow: weekly plan of both environments, opens an issue on drift — 2026-11-07, branch `ci/drift`: Mondays 05:17 UTC and on demand; one open issue at a time, found by title, labelled `area: infra`; closed by the first run without drift
- [x] T-0019 Make `infra-static` and `infra-plan` required checks in `.github/rulesets/main.json` — 2026-11-07, branch `ci/required-infra-checks`: both always report (steps skipped without infrastructure changes, on fork PRs and in repos without a bootstrap); takes effect when the maintainer runs `just github-settings`
- [x] T-0020 Dependabot `terraform` (check it picks up the OpenTofu lock file) — 2026-11-07, branch `build/dependabot-opentofu`: Dependabot's own `opentofu` ecosystem instead (the lock files record `registry.opentofu.org`), both roots, grouped, 14-day cooldown; confirm the first update PR after the merge
- [~] T-0021 Fill in `docs/deploy.md` — bootstrap sections done 2026-11-07; the app sections follow the workflows; first deploy and promote sections 2026-11-08; the custom domain follows T-0057

### M1.2 Application skeleton

- [x] T-0022 Decide Q-0001 (formatter) before T-0023 — 2026-11-07: `dotnet format`
- [x] T-0023 .NET 11 solution: `global.json` (11.0, `rollForward: latestFeature`, `allowPrerelease: true` until GA on 2026-11-10), `.slnx`, `Directory.Build.props` (artifacts output, `AnalysisMode=All`, warnings as errors), `Directory.Packages.props`; `Weesite.Web`, `Weesite.AppHost`, `Weesite.ServiceDefaults`; TUnit test projects — 2026-11-07, branch `feat/dotnet-solution`: SDK 11.0.100-rc.3 pinned with `latestFeature` roll-forward; hand-written AppHost and ServiceDefaults (Aspire 13.6.4; Aspire templates not installed); five TUnit smoke tests; CA2007 off and ASPIRE010 suppressed, with reasons; also `build`, `test`, `format`, `format-check` recipes (from T-0026) and an `app` workflow with build, format and tests (from T-0027)
- [x] T-0024 `.editorconfig`: .NET naming and style rules — 2026-11-07: `var`, braces, file-scoped namespaces, usings, accessibility, `readonly`, keywords for built-in types, formatting and naming (`_camelCase` private fields, `I`/`T` prefixes) at `warning` with explicit IDE severities, so the build enforces them; IDE0003 and IDE0049 only through `dotnet format --verify-no-changes` (checked with a probe file)
- [x] T-0025 Aspire AppHost with Azurite, seed content and a placeholder identity; `.mcp.json` with the Playwright and Aspire MCP servers (`aspire mcp init`) — 2026-11-07, branch `feat/aspire-apphost`: Azurite through `Aspire.Hosting.Azure.Storage` (Blob, Table, Queue), `Weesite.Seed` loads `seed/` (two posts, an About page, `site.json` with "Example Author"; formats a draft until T-0032), the web app waits for the seed; `.mcp.json` written by hand (`aspire agent mcp`, `@playwright/mcp@0.0.83`); `just aspire`; tried with Podman: web healthy after about 16 s
- [x] T-0026 Recipes `build`, `test`, `aspire`, `screens`; TUnit.Playwright end-to-end tests with axe and screenshot snapshots — 2026-11-07, branch `test/end-to-end`: `Weesite.Web.EndToEnd` (8 tests: structure as accessibility-tree snapshots, axe on every page, skip link, JavaScript off, screenshots at 390/1280 light/dark), `just screens` and `just browsers`; the `app` workflow installs Chromium and keeps reports and screenshots on failure; pixel snapshots dropped (F-0017)
- [x] T-0027 Containerfile (hadolint) and the `app` workflow: build, test, `dotnet list package --vulnerable --include-transitive`, grype and syft, push to GHCR by commit SHA, deploy to test — the `app` workflow's build, format and test job exists since T-0023; `build` is a required check since 2026-11-07; Containerfile (SDK 11.0.100-rc.3 to `aspnet` chiseled-extra on Ubuntu 26.04, both by digest), image job (syft SBOM, grype with high cutoff) and publish job (pushes the scanned image to GHCR by commit SHA) on branch `ci/container-image`; tried locally: healthy, user 1654, 173 MB, no high findings; deploy to test moves to T-0031; NuGetAudit covers `dotnet list package --vulnerable`; deploy to test on branch `ci/deploy-promote` (T-0031)
- [x] T-0028 Dependabot `nuget` and `docker`; dependency review on PRs (14-day cooldown, matching `nuget.config`) — 2026-11-07, branch `build/dependabot-nuget-docker`: `nuget` (grouped: Aspire, OpenTelemetry, tests, other) and `docker` (the Containerfile's digests), both weekly with a 14-day cooldown; `dependency-review` workflow fails on any severity and is a required check
- [x] T-0029 CodeQL: add `csharp` to the default setup (`scripts/github-settings.sh`) — 2026-11-07: `github-settings.sh` configures `actions` and `csharp`; applies with `just github-settings`
- [x] T-0030 `infra/app` modules: storage, Container Apps environment, app and job, Key Vault, Application Insights — 2026-11-08, branch `infra/app-resources`: identity, storage (Blob, Table, Queue), app Key Vault, Log Analytics with a daily cap and Application Insights, Container Apps environment and web app; the Container Apps job moves to T-0045 (no job image yet); test sends `X-Robots-Tag: noindex` (Q-0004)
- [x] T-0031 First deploy to test; `promote` workflow to prod (deploy step in the `app` workflow; the GHCR package must be public so Container Apps pulls it anonymously: check its visibility after the first push to `main`) — 2026-11-08, branch `ci/deploy-promote`: `deploy (test)` in the `app` workflow and the manual `promote` workflow share `scripts/deploy-app.sh` (by digest, checks the new revision's image and `/health`, masks the hostname); `promote` reads test's image with the plan identity. Confirm after the first runs on `main`
- [x] T-0062 Azure DNS zone for the prod domain, ahead of T-0057 so the delegation can settle: `prod_domain` in the bootstrap (repository variable `PROD_DOMAIN`), `azurerm_dns_zone` in `infra/app` prod with `prevent_destroy`, name servers in the apply summary — 2026-11-07, branch `infra/dns-zone`
- [x] T-0063 Set `prod_domain`, run `just bootstrap-apply` and `infra-apply`, then point the domain's name servers at the zone — maintainer — 2026-11-07: zone created by `infra-apply`; the TLD delegation and public resolvers return the zone's four Azure name servers
- [x] T-0066 Move test and prod to their own region: `environment_location` in the bootstrap, region in the `infra/app` name hash, `austriaeast` out of the policy — 2026-11-08, [PR #30](https://github.com/gergodrazsdik/weesite/pull/30): West Europe refused new Key Vaults ("region not accepting new customers"), so both environments are in Poland Central; `infra-apply` applied test and prod there (F-0019, F-0020)

## Phase 2 — Public site

- [x] T-0032 Content model (posts and pages, front matter, `lang`, translation group, authors), localization plumbing (`IStringLocalizer`, `en` only) — 2026-11-08, branch `feat/content-model`: `Weesite.Content` (YAML front matter, slug rules and reserved slugs, catalog with tags and redirects, file and Blob sources, seeder), loaded at startup; site name and tagline from `site.json`; UI strings through `IStringLocalizer` (`en`); the web app seeds Blob itself (`Seed:Path`), replacing `Weesite.Seed`; test seeds from the image, prod stays empty (ADR 0002)
- [x] T-0033 Markdown pipeline (Markdig, sanitized, server-side code highlighting) and media endpoint (`/media/{hash}-{name}`, `nosniff`, non-images as downloads) — 2026-11-08, branch `feat/markdown-media`: Markdig with raw HTML off and an explicit extension list (no media links or generic attributes), unsafe link schemes removed, headings shifted below the page `h1`, TextMateSharp highlighting into `hl-*` classes, rendered once at load; `/media/{hash}-{name}` from Blob (files in tests) with `nosniff`, immutable caching, attachments for everything but raster images, strict name check; sample PNG and SVG in `seed/media/`
- [x] T-0034 Image pipeline: EXIF stripping, WebP variants with `srcset`, required alt text (NetVips, Q-0002) — 2026-11-08, branch `feat/image-pipeline`: `ImageProcessor` (orientation, all metadata stripped, WebP at 480/960/1600 px, no upscaling), run by the seeder now and by uploads later; a media index built at load gives the renderer sizes and variants for `srcset`, `width`, `height` and lazy loading; images without alt text are reported (the editor will refuse them, T-0047); NetVips checked in the chiseled image
- [x] T-0035 Posts, content pages, one-level menu, configurable home page, slug redirects — 2026-11-08, branch `feat/posts-pages`: `/`, `/blog`, `/blog/{slug}`, `/{slug}` with real 404s (`NavigationManager.NotFound`), 301s from `redirect_from`, menu from `site.json` with `aria-current`, footer with contact and links, home per `home.show`; axe passes on every page
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
- [ ] T-0057 Domain registration, Azure DNS zone, custom domains and managed certificates — domain registered by the maintainer 2026-11-07; zone in T-0062
- [ ] T-0058 Privacy policy page; go-live
- [ ] T-0064 After .NET 11 GA (2026-11-10): `global.json` to the GA SDK (`11.0.100`, `allowPrerelease: false`), `Microsoft.AspNetCore.Mvc.Testing` to 11.0.0
- [ ] T-0065 Release 1.0: start `CHANGELOG.md` (Keep a Changelog) and keep it from then on; put the changelog item back into the PR template

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
- F-0010 azurerm 5.0 (July 2026) changed what the policy and the bootstrap assume: no resource providers are registered by default (the bootstrap registers the ones `infra/app` needs, since the apply identities only reach their resource group), `allow_nested_items_to_be_public` defaults to false, `enable_rbac_authorization` is gone. Provider pins use patch ranges (`~> 5.11.0`) so new releases arrive through Dependabot (T-0020), not `tofu init`.
- F-0011 Claude Code sandbox: OpenTofu providers and TFLint plugins need Unix sockets, so `tofu validate`/`plan` and `tflint` fail inside it, and so do `just validate`, `just tflint` and `just lint`. `tofu` is excluded at user level; `tflint` isn't. Add `tflint` to the maintainer's user-level `excludedCommands` (like F-0004), or keep approving it per call. **Resolved 2026-11-07:** the maintainer's user settings now exclude `tflint`, `just infra`, `just validate` and `just tflint`.
- F-0012 For T-0015 (Checkov): the state storage account and Key Vault keep public network access on, because GitHub-hosted runners have no fixed addresses (access is Entra ID only), and the state keys have no expiry or rotation policy (OpenTofu records the key version per state, so rotation would work). Expect Checkov findings for both; waive them with reasons or add a rotation policy. **Resolved 2026-11-07 (T-0015):** waived inline in `infra/bootstrap/state.tf` with reasons: public network access, firewall and private endpoints (storage and Key Vault), key expiry and HSM, customer-managed keys, queue and blob logging.
- F-0013 Before an environment's first apply there is no `app.tfstate`, and `tofu init` wants to write an empty one, which the read-only plan identity can't (403 on the state lock). `scripts/infra-plan.sh` reports such environments as "no state yet" (PR plans pass with that note) and `infra-apply` plans and applies them in the apply job. Found on the first `infra-plan` run, which also proved OIDC, the plan identity and state reads work.
- F-0014 GitHub's default `run` shell is `bash -e` without `pipefail`, so `cmd | tee` hid failures. The infra workflows set `defaults.run.shell: bash` (`-eo pipefail`).
- F-0015 `Weesite.Web` leaves out `blazor.web.js`: no enhanced navigation, streaming rendering or enhanced forms, so pages are plain HTML and forms post normally, as the brief's "works without JavaScript" asks. Revisit if the editor needs enhanced forms; scripts may only enhance.
- F-0016 NuGet supply chain: `nuget.config` allows nuget.org only (source mapping) with a 14-day `minPublishAgeHours` cooldown (.NET 11 SDK), so `dotnet package add`/`update` skip newer versions (checked: `add TUnit` picked 1.76.0); exact pins still restore. NuGetAudit covers transitive packages at every severity and fails the build. Dependabot's `nuget` cooldown (T-0028) should match.
- F-0017 The brief's visual layer (Verify screenshot snapshots) would compare pixels that differ between machines and CI because of font rendering, so it would be flaky. Structure is checked with Playwright accessibility-tree snapshots instead, and `just screens` keeps the screenshots for people and agents to look at. Revisit pixel comparison if a containerised browser makes rendering identical everywhere.
- F-0018 Checkov on the plan JSON doesn't apply the inline `#checkov:skip` comments: `--repo-root-for-plan-enrichment` didn't map plan resources to their code in any variant tried (relative, absolute, `--deep-analysis`). `scripts/infra-plan.sh` therefore skips, in the plan tier, every check waived anywhere in the root; `infra-static` keeps checking each waiver on its own resource. Found on #27's first plan.
- F-0019 The first `infra-apply` of the app resources failed: the subscription allows one Container Apps environment per region in Germany West Central, which another project uses, and none in Austria East. West Europe and Poland Central allow 20. Environments now get their own region (`environment_location`), and each resource group keeps all its resources in its own region, so the app and its storage stay together. A resource group's ID has no region in it, so the name hash now includes the region: the old test Key Vault's name stays reserved for 90 days. The first move, to West Europe, then failed on the Key Vault: `RequestDisallowedByAzure`, the region isn't accepting new customers for this subscription. A probe resource group in Poland Central created a Key Vault, a storage account and a Container Apps environment, and the environments now live there. Probe a region the same way before moving to it.
- F-0020 After the environment resource groups were deleted, refreshing the stale test state failed: azurerm 5.11 errors with 404 on `azurerm_storage_table` when its storage account is gone, instead of dropping it from state. With every resource in that state gone, the fix was to delete the test `app.tfstate` (kept by versioning and soft delete) through a temporary Storage Blob Data Contributor assignment, so the next apply starts from no state. Maintainers have no standing data access to the environment state containers, only to `bootstrap`.
- F-0021 Azure.Storage.Blobs 12.29 uses storage API version 2026-06-06, which Azurite before 3.37 rejects ("API version not supported"), and Aspire 13.6.4 defaults to Azurite 3.35. The AppHost and the Blob source test pin Azurite 3.37.0; keep Azurite in step when the SDK moves.
- F-0022 TextMateSharp's built-in grammars cover C#, TypeScript, shell, YAML, JSON, Dockerfile, PowerShell, Python, Rust, Go, SQL, XML, HTML, CSS and more, but not HCL, Rego, Haskell or TOML: those fences render as plain, escaped code. Grammars can be added later from files (`RegistryOptions.LoadFromLocalDir`).
- F-0023 Once the app resources existed, plans failed for the read-only plan identity: refreshing `azurerm_container_app` calls `listSecrets`, and `azurerm_storage_table` reads the table's stored access policies (Get Table ACL), which with Entra ID needs `Microsoft.Storage/storageAccounts/tableServices/tables/getAcl/action`: no data role has it, only Contributor among built-in roles. The bootstrap gives the plan identity a custom role with exactly those actions on the environment resource groups (a first try with Storage Table Data Reader didn't help). Found on #32's plan.

## Open questions

<!-- questions for the maintainer; ID Q-NNNN. Answered ones keep the answer and date. -->

- Q-0001 Formatter for C#: `dotnet format` (no extra tool) or CSharpier as a local tool with format-on-save? Decide before T-0023. **Answered 2026-11-07:** `dotnet format`; style comes from `.editorconfig` (T-0024).
- Q-0002 Image library: SkiaSharp (MIT) or NetVips (libvips); ImageSharp's licence rules it out (brief, Images and attachments). Decide before T-0034. **Answered 2026-11-08:** NetVips.
- Q-0003 The brief assumes comments and a newsletter are out of scope, "not yet confirmed". Confirm. **Answered 2026-11-08:** confirmed, both out of scope ([ADR 0002](../adr/0002-public-site.md)).
- Q-0004 Restrict test ingress to the maintainer's IP addresses (at the cost of smoke tests from GitHub-hosted runners)? Decide with T-0030. **Answered 2026-11-08:** open, with `noindex` (the `Site:NoIndex` setting adds `X-Robots-Tag`), so CI can smoke-test test deploys.
- Q-0005 The apply identities may grant only the storage data roles (Blob, Queue, Table) to managed identities, as the brief says. The app's Key Vault references (T-0030) also need Key Vault Secrets User for the site's identity: add it to the delegable roles in `infra/bootstrap/identities.tf`, or have the bootstrap grant it? Decide before T-0030. **Answered 2026-11-08:** add it to the delegable roles (`infra/bootstrap/identities.tf`); `infra/app` grants it to the web identity. Needs a `bootstrap-apply`.
- Q-0006 Dependabot's commits carry no DCO sign-off, so its PRs fail the required `dco` check (PR #1 does already). Exempt `dependabot[bot]` in `scripts/check-dco.sh` (its commits are GitHub-signed), or keep signing them off by hand (`git rebase --signoff` on the PR branch, which replaces GitHub's signature with yours)? **Answered 2026-11-07:** exempt them; `scripts/check-dco.sh` skips commits whose author is Dependabot and whose committer is GitHub (a spoofed author fails the ruleset's signature requirement), branch `ci/dco-dependabot`.

## Decisions

<!-- one line per decision with a link to its ADR -->

- 2026-11-07 Baseline from the design brief (stack, hosting, public repo, own bootstrap, two-tier infra CI with a local Rego baseline, Apache-2.0 + DCO, in-app sign-in, in-process hybrid search, generic engine, platform frontend, HTML export): [ADR 0001](../adr/0001-baseline.md)
- 2026-11-08 Public site: brief's URL scheme, Atom and JSON Feed, no raw HTML in Markdown, TextMateSharp highlighting, test seeded and prod empty until the editor, neutral look with a blue accent, NetVips Open Graph cards, content loaded at startup, no comments or newsletter: [ADR 0002](../adr/0002-public-site.md)
