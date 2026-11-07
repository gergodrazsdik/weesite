# policy

**Placeholder.** Weesite's own Rego baseline for the OpenTofu code, run with [Conftest](https://www.conftest.dev/) on the HCL in every PR (`infra-static`) and on the plan JSON (`infra-plan`). There is no external standards library; the rules live here, each with `conftest verify` tests.

Planned rules (about ten, from the [brief](../docs/design/brief.md#repo-cicd-and-environments)):

- HTTPS only, minimum TLS 1.2
- No public blob access
- Storage shared keys off (Entra ID auth only)
- Key Vault purge protection on
- Managed identities instead of stored credentials
- Required tags present
- Resources only in the allowed regions

A failing rule fails the check. An exception needs an inline waiver next to the resource, with a reason.
