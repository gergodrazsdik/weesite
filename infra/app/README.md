# infra/app

The site's own resources, one OpenTofu state per environment (`test`, `prod`). PRs plan both environments with the read-only plan identity (`infra-plan`); merges to `main` apply them with each environment's apply identity after approval (`infra-apply`).

**Status:** a skeleton. It reads the environment's resource group and creates nothing; the modules come with T-0030 (see the [tracker](../../docs/plan/TRACKER.md)). Planned, from the [brief](../../docs/design/brief.md#hosting-decision):

- Storage (Blob for content, vectors and media; Table for analytics and the audit log; a Queue for the re-index job), with soft delete and versioning
- Container Apps environment, the web app and the re-index job; OpenTofu ignores the image field so app deploys and applies don't undo each other
- Azure DNS zone, custom domains and managed certificates (prod only)
- Key Vault for app secrets, Application Insights with a daily cap, an availability test

## State and inputs

| Variable | CI source |
| --- | --- |
| `environment` | the job (`test` or `prod`) |
| `state_resource_group`, `state_storage_account`, `state_key_vault_uri` | repository variables `TF_STATE_*`, set by the bootstrap |
| `use_oidc` | `true` in CI, so the state encryption key provider uses GitHub's OIDC token |

The state of an environment is `app.tfstate` in the container named after it, encrypted with the Key Vault key named after it. Azure authentication comes from the `ARM_*` environment (`ARM_USE_OIDC=true` and the client, tenant and subscription IDs from the GitHub secrets) or, locally, the Azure CLI.

Deployment-specific values (domain, hostnames) come from GitHub environment variables as `TF_VAR_*`, never from committed files. Resource names follow the Cloud Adoption Framework's naming and abbreviations.

## Scripts

- [`scripts/infra-plan.sh`](../../scripts/infra-plan.sh) `<env> <out-dir> [--lock]`: init, plan, `tofu show -json`, conftest with the inline waivers, Checkov on the plan, and a summary. The plan text is never printed.
- [`scripts/plan-summary.sh`](../../scripts/plan-summary.sh): change counts and resource addresses as Markdown, the only form a plan is shown in on GitHub.
- [`scripts/infra-apply.sh`](../../scripts/infra-apply.sh) `<env> <plan-dir>`: applies the saved plan from the plan job (OpenTofu rejects it if the state changed since), or, when the environment had no state yet, plans with the lock and applies that. Only the "Apply complete" line reaches the log.

## Applying

`infra-apply` runs on merges to `main` that touch the infrastructure, and on demand (`workflow_dispatch`): plan test, apply test, plan prod, then apply prod after the `prod` reviewer approves. The reviewer finds the prod plan's counts and addresses in the plan job's summary. The first run per environment creates its state.
