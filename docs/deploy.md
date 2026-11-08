# Deploy your own Weesite

> **In progress.** Everything up to "Custom domain" works. The custom domain's records and certificate come with the site's launch (T-0057).

Weesite deploys to your own Azure subscription from your own fork. Nothing in the repo names a deployment: your domain, hostnames and IDs live in your fork's GitHub environment variables and secrets.

## Prerequisites

- An Azure subscription where you are Owner (or Contributor plus Role Based Access Control Administrator). The bootstrap assigns roles and registers resource providers.
- A GitHub account and a domain you control (the domain is optional).
- The infrastructure tools from [dev-setup.md](dev-setup.md#infrastructure-from-phase-1): OpenTofu, the Azure CLI, and `gh` (or a GitHub token).
- A fork created after 15 July 2026, or a repository opted in to GitHub's immutable OIDC subject claims. The federated credentials trust that subject format.

## Fork the repository

Fork (or copy) the repo, then apply the repository settings and rulesets to it with `just github-settings <owner>/<repo>`. On a brand-new repository, add `--no-required-checks` until `lint`, `dco`, `infra-static`, `infra-plan`, `build` and `dependency-review` have run once, then run it again without. Until the bootstrap has run, `infra-plan` and `infra-apply` skip their work with a note.

## Bootstrap inputs: `bootstrap.auto.tfvars`

Copy `infra/bootstrap/bootstrap.auto.tfvars.example` to `infra/bootstrap/bootstrap.auto.tfvars` (gitignored) and fill it in:

- `subscription_id` and `location` (the state storage, its Key Vault and the identities). The location must be in `policy/data/policy.yaml`; edit that list for your deployment first.
- `environment_location` (optional, defaults to `location`): the region of the test and prod resource groups and everything in them. Each environment runs one Container Apps environment, and subscriptions have a per-region limit on those (Azure portal, Quotas, Container Apps, Managed Environments): pick a region with room for two. Some regions also turn away new subscriptions for some services (`RequestDisallowedByAzure`); before choosing one, create a Key Vault, a storage account and a Container Apps environment in a throwaway resource group there.
- `name_suffix`: 3–6 lowercase letters or digits that make the storage account and Key Vault names globally unique. Pick it once.
- `github_owner`, `github_repository`, and `prod_reviewers` (the GitHub logins that approve prod deployments).
- `budget_amount` (per environment, default 30) and `budget_contact_emails`.
- `prod_domain` (optional): your site's domain, for example `example.com`. `infra/app` creates its Azure DNS zone in prod; see [Custom domain](#custom-domain).
- `environment_variables` (optional): extra variables per environment.

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

Merge to `main`. Two workflows take it from there:

1. **`infra-apply`** plans and applies `infra/app`: test first, then prod after you approve the `prod` environment in the Actions tab. The first run per environment also creates its state. The Container App starts on a placeholder image (Microsoft's ASP.NET sample).
2. **`app`** builds, tests and scans the image, pushes it to `ghcr.io/<owner>/weesite:<commit>`, and deploys it to test by digest (`deploy (test)`). It checks `/health` on the new revision. If `infra-apply` hasn't created the Container App yet, the deploy job warns and skips; re-run it from the Actions tab once `infra-apply` has finished.

Before the first deploy:

- Make the `weesite` package public (your GitHub profile, Packages, `weesite`, Package settings). The first push may create it as private, and Container Apps pulls it without credentials.
- Run `just github-settings <owner>/<repo>` after merging changes to the settings or rulesets.

### Promote to prod

Start **`promote`** from the Actions tab, on `main`. It reads the image digest test runs, waits for your approval in the `prod` environment, deploys that same digest to prod, and checks `/health` there.

### Where things run

- test: the hostname Container Apps generates (Azure portal, or the `ca-weesite-web` app in `rg-weesite-test`). It sends `X-Robots-Tag: noindex`, so it stays out of search engines.
- prod: the same, until the custom domain below.

## Custom domain

Prod can serve your own domain from an Azure DNS zone; test always uses the hostname Container Apps generates.

1. Set `prod_domain` in `bootstrap.auto.tfvars` and run `just bootstrap-apply`. This sets the repository variable `PROD_DOMAIN`.
2. Run `infra-apply` (it runs on the next infrastructure change on `main`, or start it from the Actions tab). The prod apply creates the zone and lists its four name servers in the job summary.
3. At your registrar, replace the domain's name servers with those four. Check with `dig NS <domain> +short`; propagation can take up to 48 hours.

The zone has `prevent_destroy`: removing `prod_domain` later makes the prod plan fail rather than delete the zone, because a new zone gets new name servers. The records for the site (apex and `www`), the managed certificate and the redirect from `www` to the bare domain come with the app.
