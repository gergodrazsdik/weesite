# Changelog

All notable changes to Weesite are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Repository skeleton: community files, agent instructions, lint tooling, git hooks, the `lint` workflow with `zizmor` and `dco` jobs, repository settings script and rulesets, work tracker and baseline ADR.
- Rego policy baseline in `policy/` (storage, Key Vault, Container Apps, tags, locations) for both the HCL and the plan JSON, with `conftest verify` tests, inline waivers (`# policy:waive`) and the `policy-test`, `policy-check` and `lint-rego` recipes, run by `just lint` and CI.
- `infra/bootstrap`: state storage and per-state encryption keys in Key Vault, environment resource groups with budgets, plan and apply identities with GitHub federated credentials (immutable subjects), resource provider registration, and the GitHub environments, secrets and variables. Recipes `bootstrap-first-run`, `bootstrap-plan`, `bootstrap-apply` and `bootstrap-migrate`; `validate` and `tflint` in `just lint` and CI.
- `just merge-stack <top-pr>`: checks every PR of a `gh stack` stack (required checks, signatures, each head containing the one below), then merges it bottom-up by fast-forwarding `main`; `--dry-run` only checks.

### Changed

- Allowed locations: `germanywestcentral`, `westeurope`, `austriaeast`, `polandcentral` (were `westeurope`, `northeurope`, `swedencentral`).
- Policy rules follow azurerm 5.x: `storage-no-public-blob` fails only when public access is turned on (now off by default), and `keyvault-rbac` no longer accepts the removed `enable_rbac_authorization`.

[Unreleased]: https://github.com/gergodrazsdik/weesite/commits/main
