# Changelog

All notable changes to Weesite are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Repository skeleton: community files, agent instructions, lint tooling, git hooks, the `lint` workflow with `zizmor` and `dco` jobs, repository settings script and rulesets, work tracker and baseline ADR.
- Rego policy baseline in `policy/` (storage, Key Vault, Container Apps, tags, locations) for both the HCL and the plan JSON, with `conftest verify` tests, inline waivers (`# policy:waive`) and the `policy-test`, `policy-check` and `lint-rego` recipes, run by `just lint` and CI.
- `infra/bootstrap`: state storage and per-state encryption keys in Key Vault, environment resource groups with budgets, plan and apply identities with GitHub federated credentials (immutable subjects), resource provider registration, and the GitHub environments, secrets and variables. Recipes `bootstrap-first-run`, `bootstrap-plan`, `bootstrap-apply` and `bootstrap-migrate`; `validate` and `tflint` in `just lint` and CI.
- `just merge-stack <top-pr>`: checks every PR of a `gh stack` stack (required checks, signatures, each head containing the one below), then merges it bottom-up by fast-forwarding `main`; `--dry-run` only checks.
- `infra-static` workflow: `tofu fmt` and `validate`, TFLint, Checkov and conftest on every PR, forks included, with no credentials (`just infra`); Checkov settings in `.checkov.yaml`, with inline waivers for the bootstrap's state storage and Key Vault.
- `infra-plan` workflow: plans `infra/app` for test and prod on same-repo PRs with the read-only plan identity, runs conftest and Checkov on the plan JSON, and posts counts and addresses to the job summary and a PR comment. A skeleton `infra/app` root reads each environment's resource group. Shared tool setup in `.github/actions/setup-infra-tools`.

### Changed

- `just lint` is now `lint-core` (the `lint` workflow) plus `infra` (the `infra-static` workflow).
- Allowed locations: `germanywestcentral`, `westeurope`, `austriaeast`, `polandcentral` (were `westeurope`, `northeurope`, `swedencentral`).
- Policy rules follow azurerm 5.x: `storage-no-public-blob` fails only when public access is turned on (now off by default), and `keyvault-rbac` no longer accepts the removed `enable_rbac_authorization`.

[Unreleased]: https://github.com/gergodrazsdik/weesite/commits/main
