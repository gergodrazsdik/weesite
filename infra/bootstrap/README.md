# infra/bootstrap

A one-off OpenTofu root that prepares a subscription and a GitHub repository for Weesite. A maintainer runs it locally once per deployment, then again only to change what it manages. CI never runs it. The steps are in [docs/install.md](../../docs/install.md) and the details in [docs/deploy.md](../../docs/deploy.md#bootstrap); this file describes what it creates and how.

## What it creates

The names below are those of a first install. With `instance` set (a second install in the same subscription or tenant), `weesite` becomes `weesite-<instance>` in the resource groups, identities, custom roles, budgets and the state's log workspace.

| Area | Resources |
| --- | --- |
| State | `rg-weesite-tfstate` with a storage account (`stweesitestate<suffix>`: GRS, blob versioning, 30-day soft delete, shared keys off, Entra ID only) and one container per state: `bootstrap`, `test`, `prod` |
| Encryption | Key Vault `kv-weesite-state-<suffix>` (RBAC, purge protection, 90-day soft delete) with one RSA key per state, used by OpenTofu's `azure_vault` key provider |
| Audit | Log Analytics `log-weesite-state` (90 days, 0.5 GB daily cap): every read, write and delete of the state blobs and every use of the state keys ([audit.tf](audit.tf)) |
| Environments | `rg-weesite-test` and `rg-weesite-prod` in `environment_location` (default: `location`), each with a monthly budget (alerts at 80% spent and 100% forecast) |
| Identities | User-assigned managed identities with GitHub federated credentials (no client secrets), in the state resource group: `id-weesite-plan`, `id-weesite-plan-main`, `id-weesite-apply-<env>` and `id-weesite-ops-<env>` |
| Resource providers | Registers what the bootstrap and `infra/app` use (azurerm 5.x registers nothing by default) |
| GitHub | Environments `test` and `prod` (deploy from `main` only; `prod` needs a reviewer), `test-ops` and `prod-ops` (the helper workflows, from `main` only; `prod-ops` needs a reviewer) and `plan` (pull request plans, from any branch, each after a reviewer's approval); admins can't bypass any of them. Their secrets, repository secrets and variables, and Dependabot secrets for the plan identity |

The storage account, containers, Key Vault and keys have `prevent_destroy`: losing them loses the states or the means to decrypt them.

## Identities and roles

| Identity | Trusted subject | Roles |
| --- | --- | --- |
| `id-weesite-plan` | `environment:plan` (PR plans, after approval), `ref:refs/heads/main` (test's plans, drift) | For `test` only: Reader and a custom role with only Container Apps `listSecrets` and storage table `getAcl` on its resource group; Storage Blob Data Reader on its state container; Key Vault Crypto User on its state key; Key Vault Reader on its resource group (key properties); Microsoft Graph `Application.Read.All`. `infra/app` adds Key Vault Secrets User on each secret it writes to the app vault (the Microsoft client, the Application Insights connection string), and on no other |
| `id-weesite-plan-main` | `ref:refs/heads/main` only (prod's plan before an apply, drift) | The same roles for `prod` |
| `id-weesite-ops-<env>` | `environment:<env>-ops` (`seed`, `load-test`, `geoip`) | Reader on its resource group. `infra/app` adds Storage Blob Data Contributor on the `geoip` container and, on test, on `content`, `media` and `settings`, with Queue Data Message Sender on the re-index queue (the seed) |
| `id-weesite-apply-<env>` | `environment:<env>` | Contributor on its resource group; Role Based Access Control Administrator there, limited by a condition; Storage Blob Data Contributor on its container; Key Vault Crypto User on its key; Key Vault Crypto Officer on its resource group (the app vault's Data Protection key); Key Vault Secrets Officer on its resource group (the sign-in secrets); Microsoft Graph `Application.ReadWrite.OwnedBy` (the environment's app registration) |

- Subjects use GitHub's immutable format, `repo:<owner>@<owner-id>/<repo>@<repo-id>:...`, the default for repositories created after 15 July 2026. A renamed or re-created repository doesn't inherit the trust. The IDs are looked up through the GitHub provider.
- The plan identities can't write state, so plans run with `-lock=false`.
- Pull requests plan only the environments in `pr_plan_environments` (`test`); the others are planned only from `main`. A workflow on a branch can then read neither prod's state, which holds the sign-in client secret, nor its Key Vault (T-0122). prod's plan shows in **infra-apply** before its approval.
- A pull request signs in as `id-weesite-plan` only in a job of the `plan` environment, which waits for a reviewer: a workflow a branch adds or changes can't use the identity without that approval (T-0177).
- The helper workflows run third-party code (NuGet packages and the app in `seed`, k6 in `load-test`), so they sign in as `id-weesite-ops-<env>`, with only the data roles they need, never as the apply identity (T-0177).
- `plan_secrets_group_scope` keeps the plan identities' former Key Vault Secrets User on the whole resource group, for one bootstrap-apply while an install from 1.0 moves to the secret-scope roles ([docs/deploy.md](../../docs/deploy.md#updating-an-install-from-10)).
- Refreshing `infra/app` needs two actions beyond Reader: azurerm reads Container Apps secrets (`listSecrets`; only Key Vault references live there) and storage table access policies (`tables/getAcl/action`, which no data role includes). Hence the custom role.
- The condition on Role Based Access Control Administrator allows assigning and removing only the roles in `delegable_roles` (Storage Blob Data Contributor and Reader, Storage Queue Data Contributor, Message Processor and Message Sender, Storage Table Data Contributor and Reader, Key Vault Secrets User, Key Vault Crypto User, Monitoring Metrics Publisher) and the custom role `weesite-web-blob-versions-<suffix>`, and only to service principals. That's enough for the site's own managed identity, the plan identity's secrets and the helper workflows' identity, and nothing more.
- The custom role `weesite-web-blob-versions-<suffix>` has one data action, `deleteBlobVersion`: the web app deletes the earlier versions of posts, pages and settings files that its retention lets go (F-0240), which Storage Blob Data Contributor can't. Its ID is derived from the subscription and the install's prefix, so `infra/app` knows it without a lookup by name. `infra/app` assigns it once it exists; until the bootstrap has been applied, its plan warns and the app keeps every version.
- Whoever runs the bootstrap gets Key Vault Crypto Officer on the vault (to create the keys), Storage Blob Data Contributor on the `bootstrap` container, and Key Vault Secrets Officer on each environment's resource group (for `just google-signin`, `just site-admins` and `just smtp`). Subscription Owner has none of these data-plane roles.
- Granting the Microsoft Graph app roles is admin consent, so the runner needs Global Administrator or Privileged Role Administrator in the tenant ([signin.tf](signin.tf)).

## GitHub secrets and variables

| Name | Where | Value |
| --- | --- | --- |
| `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` | environment secrets (`test`, `prod`) | the environment's apply identity |
| `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` | environment secrets (`test-ops`, `prod-ops`) | the environment's ops identity |
| `AZURE_PLAN_CLIENT_ID`, `AZURE_PLAN_MAIN_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` | repository secrets | the plan identities (the plan jobs on `main` have no environment) |
| `AZURE_PLAN_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` | Dependabot secrets | the plan identity, for Dependabot's pull requests (its runs get no Actions secrets) |
| `TF_STATE_RESOURCE_GROUP`, `TF_STATE_STORAGE_ACCOUNT`, `TF_STATE_KEY_VAULT_URI` | repository variables | where the `infra/app` states live; the container and the key are named after the environment |
| `PROD_DOMAIN` | repository variable | `prod_domain`, if set: the domain whose DNS zone `infra/app` creates in prod (a repository variable because plan jobs have no environment) |
| `WEESITE_INSTANCE` | repository variable | `instance`, if set: the workflows and scripts name the resource groups with it |
| anything in `environment_variables` | environment variables | extra variables for your own workflows; Weesite's workflows don't read any |

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
