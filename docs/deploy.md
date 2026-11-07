# Deploy your own Weesite

> **In progress.** The bootstrap sections are complete. The sections from "First deploy" on are an outline until the app infrastructure and workflows exist.

Weesite deploys to your own Azure subscription from your own fork. Nothing in the repo names a deployment: your domain, hostnames and IDs live in your fork's GitHub environment variables and secrets.

## Prerequisites

- An Azure subscription where you are Owner (or Contributor plus Role Based Access Control Administrator). The bootstrap assigns roles and registers resource providers.
- A GitHub account and a domain you control (the domain is optional).
- The infrastructure tools from [dev-setup.md](dev-setup.md#infrastructure-from-phase-1): OpenTofu, the Azure CLI, and `gh` (or a GitHub token).
- A fork created after 15 July 2026, or a repository opted in to GitHub's immutable OIDC subject claims. The federated credentials trust that subject format.

## Fork the repository

Fork (or copy) the repo, then apply the repository settings and rulesets to it with `just github-settings <owner>/<repo>`.

## Bootstrap inputs: `bootstrap.auto.tfvars`

Copy `infra/bootstrap/bootstrap.auto.tfvars.example` to `infra/bootstrap/bootstrap.auto.tfvars` (gitignored) and fill it in:

- `subscription_id` and `location`. The location must be in `policy/data/policy.yaml`; edit that list for your deployment first.
- `name_suffix`: 3–6 lowercase letters or digits that make the storage account and Key Vault names globally unique. Pick it once.
- `github_owner`, `github_repository`, and `prod_reviewers` (the GitHub logins that approve prod deployments).
- `budget_amount` (per environment, default 30) and `budget_contact_emails`.
- `environment_variables` (optional): extra variables per environment, such as your hostnames.

## Run the bootstrap: `just bootstrap-plan`, `just bootstrap-apply`

Sign in with the Azure CLI on the right subscription (`az login`, then `az account set --subscription <id>`). For GitHub, either export `GITHUB_TOKEN` with admin rights on your fork, or sign in to `gh`; the recipes then use `gh auth token`.

On the very first run, the state can't live in the storage account that doesn't exist yet:

```sh
just bootstrap-first-run   # local, unencrypted state for this run only
just bootstrap-plan        # review: about 55 resources to add
just bootstrap-apply       # shows the plan again and asks for confirmation
```

The apply waits two minutes after granting you Key Vault Crypto Officer, because new role assignments take a while to reach the Key Vault data plane. What it creates is described in [infra/bootstrap/README.md](../infra/bootstrap/README.md).

## Move the bootstrap state: `just bootstrap-migrate`

```sh
just bootstrap-migrate
```

This moves the bootstrap's local state into the `bootstrap` container of the storage account it created, encrypted with its own key. OpenTofu asks before copying. A plan then runs against the remote state and should show no changes. After that, delete the local plaintext copies:

```sh
rm infra/bootstrap/terraform.tfstate infra/bootstrap/terraform.tfstate.backup
```

Later changes to the bootstrap use `just bootstrap-plan` and `just bootstrap-apply`, with the encrypted remote state.

## GitHub environment variables and secrets

The bootstrap sets these; don't create or edit them by hand:

- Environment secrets on `test` and `prod`: `AZURE_CLIENT_ID` (that environment's apply identity), `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`.
- Repository secrets for plans: `AZURE_PLAN_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`.
- Repository variables: `TF_STATE_RESOURCE_GROUP`, `TF_STATE_STORAGE_ACCOUNT`, `TF_STATE_KEY_VAULT_URI`.
- Whatever you put in `environment_variables`.

To add or change a variable, edit `environment_variables` and run `just bootstrap-apply`. The sign-in client IDs come with the editor (Phase 4).

## First deploy

Merge to `main`: the workflows plan and apply the app infrastructure, build and scan the image, and deploy it to test; promote to prod after your approval.

The infrastructure part works already. Until an environment has been applied once, it has no state, and PR plans say so instead of planning. The first `infra-apply` run creates the state: it applies test, then waits for your approval in the `prod` environment. You can also start it by hand from the Actions tab (`workflow_dispatch`).

## Custom domain

DNS records, managed certificates and the redirect from `www` to the bare domain.
