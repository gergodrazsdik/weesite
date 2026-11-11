# infra/app

The site's own resources, one OpenTofu state per environment (`test`, `prod`). PRs plan both environments with the read-only plan identity (`infra-plan`); merges to `main` apply them with each environment's apply identity after approval (`infra-apply`).

Per environment, in the resource group the bootstrap created:

| File | Resources |
| --- | --- |
| `identity.tf` | `id-weesite-web`: the app's managed identity, with Storage Blob, Table and Queue Data Contributor on the storage account and Key Vault Secrets User on the app Key Vault, Key Vault Crypto User on its `dataprotection` key (the only roles the apply identity may grant) |
| `storage.tf` | `stweesite<env><hash>`: Entra ID only, versioning, 30-day soft delete, LRS; containers `content`, `media`, `settings`, `index`; tables `pageviews`, `audit`; queue `reindex` |
| `keyvault.tf` | `kv-weesite-<env>-<hash>`: app secrets (RBAC, purge protection) and the `dataprotection` key (RSA 3072, wrap and unwrap only) that wraps the app's Data Protection keys; the app gets its address as `DataProtection__KeyId` |
| `monitoring.tf` | Log Analytics (30 days, daily cap 0.1 GB on test, 0.5 GB on prod) and workspace-based Application Insights; its connection string in the app's Key Vault, which the Container App passes to the app as `APPLICATIONINSIGHTS_CONNECTION_STRING` (OpenTelemetry export, a quarter of the traces) |
| `availability.tf` | prod only: a standard availability test of the home page from two European locations every 15 minutes (about $3.70 a month), and an alert that emails the subscription's Owners when both fail |
| `containerapp.tf` | Container Apps environment and `ca-weesite-web` (port 8080; prod keeps one replica warm, test scales to zero; storage endpoints, the identity's client ID and `Site__NoIndex` as environment variables) |
| `reindex.tf` | `caj-weesite-reindex`: the re-index job, the web image started with `reindex` (1 vCPU, 2 GiB, 30-minute timeout), run once per batch of messages on the `reindex` queue; it uses the web identity, also for the queue trigger, so no storage key is stored |
| `dns.tf` | prod with a domain: the Azure DNS zone |
| `dns-records.tf` | prod with a domain: extra records from `var.dns_records` (repository variable `PROD_DNS_RECORDS`): TXT, CNAME and MX, such as the search engines' verification and email |
| `domain.tf` | prod with a domain: the apex A record, the `www` CNAME and the `asuid` TXT records; both hostnames on `ca-weesite-web` with a free managed certificate each, bound through the `azapi` provider (azurerm can't bind managed certificates); the app gets `Site__BaseUrl`, and redirects `www` to the apex |

- `<hash>` is the first six hex digits of the SHA-256 of the resource group's ID and region: stable and unique per deployment, with no extra input. Moving an environment to another region gives new names, because a deleted Key Vault's name stays reserved for 90 days under purge protection.
- Data resources (storage, its containers, tables and queue, the Key Vault, the DNS zone) have `prevent_destroy`.
- OpenTofu ignores the Container App's and the re-index job's image: the `app` and `promote` workflows deploy images (`scripts/deploy-app.sh`, the job once the app is healthy). Until the first deploy both use a pinned placeholder (Microsoft's ASP.NET sample, also on port 8080); don't use Re-embed all before then.

## State and inputs

| Variable | CI source |
| --- | --- |
| `environment` | the job (`test` or `prod`) |
| `state_resource_group`, `state_storage_account`, `state_key_vault_uri` | repository variables `TF_STATE_*`, set by the bootstrap |
| `use_oidc` | `true` in CI, so the state encryption key provider uses GitHub's OIDC token |
| `domain` | prod only: repository variable `PROD_DOMAIN` (set by the bootstrap's `prod_domain`), passed by `scripts/infra-plan.sh`; empty for test |

The state of an environment is `app.tfstate` in the container named after it, encrypted with the Key Vault key named after it. Azure authentication comes from the `ARM_*` environment (`ARM_USE_OIDC=true` and the client, tenant and subscription IDs from the GitHub secrets) or, locally, the Azure CLI.

Deployment-specific values (domain, hostnames) come from GitHub environment variables as `TF_VAR_*`, never from committed files. Resource names follow the Cloud Adoption Framework's naming and abbreviations.

## Scripts

- [`scripts/infra-plan.sh`](../../scripts/infra-plan.sh) `<env> <out-dir> [--lock]`: init, plan, `tofu show -json`, conftest with the inline waivers, Checkov on the plan, and a summary. The plan text is never printed.
- [`scripts/plan-summary.sh`](../../scripts/plan-summary.sh): change counts and resource addresses as Markdown, the only form a plan is shown in on GitHub.
- [`scripts/infra-apply.sh`](../../scripts/infra-apply.sh) `<env> <plan-dir>`: applies the saved plan from the plan job (OpenTofu rejects it if the state changed since), or, when the environment had no state yet, plans with the lock and applies that. Only the "Apply complete" line reaches the log.

## Applying

`infra-apply` runs on merges to `main` that touch the infrastructure, and on demand (`workflow_dispatch`): plan test, apply test, plan prod, then apply prod after the `prod` reviewer approves. The reviewer finds the prod plan's counts and addresses in the plan job's summary. The first run per environment creates its state.

## DNS

With a domain, prod gets an Azure DNS zone for it in `rg-weesite-prod` (`prevent_destroy`: re-creating it would change its name servers). The apply job's summary lists the zone's name servers; set them at the domain's registrar. The site's records, its hostnames and their certificates are in `domain.tf`; adding the hostnames fails until the name servers point at the zone, so the first prod apply after setting a domain fails there, and the next one, after the delegation, completes it ([Custom domain](../../docs/deploy.md#custom-domain)). Test has no zone and uses the generated Container Apps hostname.

## Drift

`drift` plans both environments every Monday (and on demand) with the plan identity. If either plan has changes, it opens an issue titled "Infrastructure drift" (label `area: infra`) with counts and addresses, or comments on the open one; a later run without drift closes it.
