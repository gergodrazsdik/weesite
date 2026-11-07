# Changelog

All notable changes to Weesite are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Repository skeleton: community files, agent instructions, lint tooling, git hooks, the `lint` workflow with `zizmor` and `dco` jobs, repository settings script and rulesets, work tracker and baseline ADR.
- Rego policy baseline in `policy/` (storage, Key Vault, Container Apps, tags, locations) for both the HCL and the plan JSON, with `conftest verify` tests, inline waivers (`# policy:waive`) and the `policy-test`, `policy-check` and `lint-rego` recipes, run by `just lint` and CI.

[Unreleased]: https://github.com/gergodrazsdik/weesite/commits/main
