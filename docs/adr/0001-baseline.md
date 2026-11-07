# 0001: Baseline decisions from the design brief

- Status: accepted
- Date: 2026-11-07
- Tracker: T-0001

## Context and problem statement

The [design brief](../design/brief.md) (Draft v0.1) settles Weesite's scope, stack, hosting and process before any code is written. Later ADRs refine or supersede these decisions one by one, and they need a recorded starting point.

## Considered options

1. One grouped baseline ADR that summarizes the brief's decisions and links to their reasoning.
2. One ADR per decision.
3. No ADR; treat the brief as the record.

## Decision outcome

Chosen option: "one grouped baseline ADR", because the brief already holds the reasoning, a dozen near-empty files would add nothing, and later ADRs need something they can supersede piece by piece.

The baseline:

- **Stack** ([Application stack](../design/brief.md#application-stack)): one ASP.NET Core 11 app with Blazor static server rendering and no interactive render modes; Markdown with Markdig; no off-the-shelf CMS. Localization-ready from day one with English as the only culture.
- **Hosting** ([Hosting decision](../design/brief.md#hosting-decision)): Azure Container Apps (Consumption), prod at minimum one 0.25 vCPU / 0.5 GiB replica so there's no cold start, test scaled to zero; Blob and Table Storage; one region; no CDN or Front Door; images on GHCR, not ACR.
- **Repository** ([Repo, CI/CD and environments](../design/brief.md#repo-cicd-and-environments)): a public repo on GitHub Free, for free environments with required reviewers, CodeQL, secret scanning and unlimited Actions minutes, and so others can deploy their own Weesite. `main` advances by fast-forward only, through `just merge`.
- **Infrastructure bootstrap**: Weesite's own OpenTofu root (`infra/bootstrap`) creates state storage, client-side state encryption keys in Key Vault, environment resource groups, plan and apply identities with federated credentials, the GitHub environments and a budget alert. It depends on no other project.
- **Infrastructure CI**: two tiers. `infra-static` runs on every PR, forks included, with no credentials; `infra-plan` runs on same-repo PRs with a read-only identity. Policy is a repo-local Rego baseline in `policy/` with `conftest verify` tests, plus TFLint and Checkov. No external standards library, no Infracost. Design standards (the Cloud Adoption Framework, the Microsoft cloud security benchmark, the Azure Verified Modules specs) guide agents but aren't enforced in CI.
- **Licence and contributions** ([Repo, CI/CD and environments](../design/brief.md#repo-cicd-and-environments)): Apache-2.0 with DCO sign-off and no CLA; signed commits required on `main`; release tags signed by the maintainer only.
- **Sign-in** ([Editor and authentication](../design/brief.md#editor-and-authentication)): in-app OpenID Connect (Google and Microsoft) with cookie auth, an allowlist and roles, rather than Container Apps' built-in authentication (Easy Auth), so sign-in works and is testable locally and the engine isn't tied to Azure.
- **Search** ([Hybrid search](../design/brief.md#hybrid-search)): in-process hybrid search, SQLite FTS5 in memory plus local ONNX embeddings (`bge-small-en-v1.5`), merged with reciprocal rank fusion; no search service or vector database. Bulk re-embedding runs in a Container Apps job.
- **Generic engine** ([Application stack](../design/brief.md#application-stack)): no personal names in code, namespaces, images or resource names; site identity is site settings, deployment values are GitHub environment variables and secrets.
- **Frontend** ([Design and theming](../design/brief.md#design-and-theming)): modern HTML/CSS/JS platform features, web components in the admin, no framework or bundler; the public site works without JavaScript under a strict CSP with no inline scripts or styles.
- **HTML export instead of cross-posting** ([HTML export](../design/brief.md#html-export)): posts export as clean HTML for publishing elsewhere by hand; the app never posts to social networks itself.

### Consequences

- Changing a baseline decision needs the maintainer's approval and a superseding ADR.
- The brief stays the place for detail and reasoning; ADRs link to it rather than copying it.

## More information

- Brief: [Options considered and rejected](../design/brief.md#options-considered-and-rejected)
