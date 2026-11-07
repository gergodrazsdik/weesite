# policy

Weesite's own Rego baseline for the OpenTofu code, run with [Conftest](https://www.conftest.dev/). There is no external standards library: the rules live here, each covered by `conftest verify` tests.

The same rules run on two inputs:

- **The HCL** of every `.tf` file (`conftest test --parser hcl2`): runs on every PR, forks included, with no credentials (`infra-static`) and locally (`just policy-check`). It judges only what is written as a literal; an expression such as `var.location` is left to the plan.
- **The plan JSON** (`tofu show -json <plan>`): runs in `infra-plan`, where variables are resolved. Values computed during apply aren't judged.

Rules apply to resources being created, updated or kept. Resources being deleted are skipped.

## Rules

| ID | Resource types | Requires |
| --- | --- | --- |
| `storage-https-only` | `azurerm_storage_account` | `https_traffic_only_enabled` not turned off |
| `storage-min-tls` | `azurerm_storage_account` | `min_tls_version` unset (TLS 1.2) or `TLS1_2`/`TLS1_3` |
| `storage-no-public-blob` | `azurerm_storage_account`, `azurerm_storage_container` | `allow_nested_items_to_be_public` not turned on; `container_access_type` unset or `private` |
| `storage-shared-key-off` | `azurerm_storage_account` | `shared_access_key_enabled = false` (Entra ID auth only) |
| `keyvault-purge-protection` | `azurerm_key_vault` | `purge_protection_enabled = true` |
| `keyvault-rbac` | `azurerm_key_vault` | `rbac_authorization_enabled = true` |
| `container-app-https-only` | `azurerm_container_app` | no ingress with `allow_insecure_connections = true` |
| `managed-identity` | `azurerm_container_app`, `azurerm_container_app_job` | an `identity` block; registries pulled with `identity`, not `password_secret_name` |
| `no-stored-secrets` | `azurerm_container_app`, `azurerm_container_app_job` | secrets as `key_vault_secret_id`, never `value` |
| `required-tags` | every taggable `azurerm_*` resource | the tag keys in `data/policy.yaml` |
| `allowed-locations` | every `azurerm_*` resource with a `location` | a location listed in `data/policy.yaml` |

Defaults are azurerm 5.x's. Where the default is insecure (`shared_access_key_enabled`, `purge_protection_enabled`), the setting must be written out; where it is secure (HTTPS only, TLS 1.2, no public blob access), only turning it off fails.

`data/policy.yaml` holds the required tag keys and the allowed locations. Edit both for your deployment.

## Waivers

An exception is a comment directly above the resource block, naming one rule and giving a reason:

```hcl
# policy:waive storage-no-public-blob Public media is served straight from Blob by design.
resource "azurerm_storage_container" "media" {
```

- [`scripts/policy-waivers.sh`](../scripts/policy-waivers.sh) collects the waivers of one OpenTofu root and its modules, and conftest gets them with `--data`.
- A waiver covers its rule for every resource with that type and name in the root, in any module.
- A waived violation is reported as a warning with its reason, so it stays visible.
- A waiver without a reason, one that isn't directly above a resource, or one naming an unknown rule fails the check.

## Layout

```text
main.rego            conftest entry point (namespace main): deny, warn, waivers
lib/resources.rego   both inputs as one resource list, value helpers, rule IDs
lib/fixtures.rego    test helpers: one resource in both input shapes
rules/*.rego         the rules, one package per resource family
rules/*_test.rego    their tests (packages weesite.tests.*)
data/policy.yaml     settings (data.policy)
```

## Commands

| Recipe | What it does |
| --- | --- |
| `just policy-test` | Run the tests (`conftest verify`). Part of `just lint`. |
| `just policy-check` | Check each root under `infra/` against the rules, with its waivers. Part of `just lint`. |
| `just lint-rego` | Check the Rego formatting (`conftest fmt --check`; fix it with `conftest fmt policy`). |

## Adding a rule

1. Add a `violation contains lib.violation("<id>", r, "<message>") if { ... }` rule to the matching file in `rules/`, or a new package `weesite.rules.<family>`.
2. Add the ID to `rule_ids` in `lib/resources.rego` and to the table above.
3. Add tests to `rules/<family>_test.rego` that check both input shapes (`fixtures.rules_both`): a passing and a failing case, plus a case where the value isn't known yet if the rule reads an attribute.
