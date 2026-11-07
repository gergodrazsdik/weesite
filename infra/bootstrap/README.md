# infra/bootstrap

A one-off OpenTofu root that prepares a subscription and a GitHub repository for Weesite. A maintainer runs it locally once per deployment, then again only to change what it manages. CI never runs it. The steps are in [docs/deploy.md](../../docs/deploy.md); this file describes what it creates and how.

## What it creates

| Area | Resources |
| --- | --- |
| State | `rg-weesite-tfstate` with a storage account (`stweesitestate<suffix>`: GRS, blob versioning, 30-day soft delete, shared keys off, Entra ID only) and one container per state: `bootstrap`, `test`, `prod` |
| Encryption | Key Vault `kv-weesite-state-<suffix>` (RBAC, purge protection, 90-day soft delete) with one RSA key per state, used by OpenTofu's `azure_vault` key provider |
| Environments | `rg-weesite-test` and `rg-weesite-prod`, each with a monthly budget (alerts at 80% spent and 100% forecast) |
| Identities | User-assigned managed identities with GitHub federated credentials (no client secrets), in the state resource group: `id-weesite-plan` and `id-weesite-apply-<env>` |
| Resource providers | Registers what the bootstrap and `infra/app` use (azurerm 5.x registers nothing by default) |
| GitHub | Environments `test` and `prod` (deploy from `main` only; `prod` needs a reviewer, admins can't bypass), their secrets, and repository secrets and variables |

The storage account, containers, Key Vault and keys have `prevent_destroy`: losing them loses the states or the means to decrypt them.

## Identities and roles

| Identity | Trusted subject | Roles |
| --- | --- | --- |
| `id-weesite-plan` | `pull_request`, `ref:refs/heads/main` (PR plans, drift) | Reader on both environment resource groups; Storage Blob Data Reader on the `test` and `prod` containers; Key Vault Crypto User on the `test` and `prod` keys |
| `id-weesite-apply-<env>` | `environment:<env>` | Contributor on its resource group; Role Based Access Control Administrator there, limited by a condition; Storage Blob Data Contributor on its container; Key Vault Crypto User on its key |

- Subjects use GitHub's immutable format, `repo:<owner>@<owner-id>/<repo>@<repo-id>:...`, the default for repositories created after 15 July 2026. A renamed or re-created repository doesn't inherit the trust. The IDs are looked up through the GitHub provider.
- The plan identity can't write state, so plans run with `-lock=false`.
- The condition on Role Based Access Control Administrator allows assigning and removing only the storage data roles (Blob, Queue and Table) and only to service principals. That's enough for the site's own managed identity, and nothing more.
- Whoever runs the bootstrap gets Key Vault Crypto Officer on the vault (to create the keys) and Storage Blob Data Contributor on the `bootstrap` container. Subscription Owner has neither data-plane role.

## GitHub secrets and variables

| Name | Where | Value |
| --- | --- | --- |
| `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` | environment secrets (`test`, `prod`) | the environment's apply identity |
| `AZURE_PLAN_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` | repository secrets | the plan identity (plan jobs have no environment) |
| `TF_STATE_RESOURCE_GROUP`, `TF_STATE_STORAGE_ACCOUNT`, `TF_STATE_KEY_VAULT_URI` | repository variables | where the `infra/app` states live; the container and the key are named after the environment |
| anything in `environment_variables` | environment variables | for example the site's hostname |

Azure IDs are secrets so that public workflow logs mask them.

## State: first run and migration

The bootstrap keeps its own state in the storage account it creates, encrypted with its own key ([backend.tf](backend.tf)). The backend and encryption settings are derived from `name_suffix` and `subscription_id` (OpenTofu evaluates variables early), so nothing deployment-specific is committed.

The first run can't use resources that don't exist yet:

1. `just bootstrap-first-run` writes a gitignored `first_run_override.tf`: local backend, no encryption.
2. `just bootstrap-apply` creates everything, with the state in `terraform.tfstate` on your disk.
3. `just bootstrap-migrate` removes the override and moves the state into the `bootstrap` container. It reads the local plaintext state once, through an unencrypted fallback, and writes everything encrypted. It then runs a plan, which should show no changes. After that, delete the local `terraform.tfstate` and `terraform.tfstate.backup`.

After the migration, `just bootstrap-plan` and `just bootstrap-apply` use the remote, encrypted state.

## Inputs

Copy [bootstrap.auto.tfvars.example](bootstrap.auto.tfvars.example) to `bootstrap.auto.tfvars` (gitignored) and fill it in. Azure access comes from `az login`; the GitHub token from `GITHUB_TOKEN` (the recipes fall back to `gh auth token`). It needs admin rights on the repository.

## Checks

`just lint` runs `tofu fmt`, `tofu validate`, TFLint and the [policy](../../policy/README.md) on this root.
