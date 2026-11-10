# Deploy your own Weesite

> **In progress.** Everything below works; the guide is finished with the site's launch (T-0021).

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
2. **`app`** builds, tests and scans the image, pushes it to `ghcr.io/<owner>/weesite:<commit>`, and deploys it to test by digest (`deploy (test)`). It checks `/health` on the new revision, then points the re-index job at the same image. If `infra-apply` hasn't created the Container App or the job yet, the deploy job warns and skips; re-run it from the Actions tab once `infra-apply` has finished.

Before the first deploy:

- Make the `weesite` package public (your GitHub profile, Packages, `weesite`, Package settings). The first push may create it as private, and Container Apps pulls it without credentials.
- Run `just github-settings <owner>/<repo>` after merging changes to the settings or rulesets.

### Promote to prod

Start **`promote`** from the Actions tab, on `main`. It reads the image digest test runs, waits for your approval in the `prod` environment, deploys that same digest to prod, and checks `/health` there.

### Where things run

- test: the hostname Container Apps generates (Azure portal, or the `ca-weesite-web` app in `rg-weesite-test`). It sends `X-Robots-Tag: noindex`, so it stays out of search engines.
- prod: your own domain, once it's set up ([Custom domain](#custom-domain)); until then, the hostname Container Apps generates.

## Sign-in

The editor at `/admin` signs in with Microsoft and Google accounts ([ADR 0004](adr/0004-sign-in.md)). Each environment has its own app registration with each provider. The secrets live in the environment's Key Vault, and the app reads them at startup; a provider without its secrets stays off.

### Microsoft: OpenTofu

`infra/app` creates the Entra app registration, its client secret (rotated every 180 days) and the Key Vault secrets. The apply identities need permission to manage app registrations, which the bootstrap grants:

1. Run `just bootstrap-apply` signed in as a Global Administrator or Privileged Role Administrator of the tenant (the account that owns a personal Azure subscription is Global Administrator of its directory). It grants Microsoft Graph `Application.ReadWrite.OwnedBy` to the apply identities and `Application.Read.All` to the plan identity, and Key Vault secret roles to them and to you.
2. Run `infra-apply`. It registers `https://<hostname>/signin-microsoft` for the environment (and `https://<domain>/signin-microsoft` with a custom domain); test also gets `http://localhost:5080/signin-microsoft` for local development. Find the hostname with the command at the end of this section; the workflow doesn't print it, because its logs are public.

### Google: by hand, once per environment

Google has no API for OAuth clients, so you create them in the console:

1. In the [Google Cloud console](https://console.cloud.google.com/), create a project (for example `weesite`) or pick one.
2. Open **Google Auth Platform** and choose **Get started**: app name (your site's name), user support email, audience **External**, contact email. Create.
3. Under **Data access**, add the scopes `openid`, `.../auth/userinfo.email` and `.../auth/userinfo.profile`. These are non-sensitive, so the app needs no verification.
4. Under **Audience**, choose **Publish app**. While it's in testing, only listed test users can sign in. The editor's allowlist decides who gets in either way.
5. Under **Clients**, choose **Create client**, type **Web application**, named after the environment (`weesite-test`). Add the authorized redirect URIs, the same as Microsoft's with `/signin-google`. For test that's `https://<test hostname>/signin-google` and `http://localhost:5080/signin-google`; for prod `https://<prod hostname>/signin-google` and, with a custom domain, `https://<domain>/signin-google`.
6. Copy the client ID and secret, and store them: `just google-signin test` (or `prod`). It first prints the exact redirect URIs the client must list (step 5), then asks for the ID and secret and writes them to the environment's Key Vault.
7. Restart the app (deploy, or restart the revision in the portal) so it reads them.

The test hostname: `az containerapp show --resource-group rg-weesite-test --name ca-weesite-web --query properties.configuration.ingress.fqdn --output tsv`.

### Admins and editors

Only allowlisted email addresses get in. Admins add further editors, as admin or author, on `/admin/editors`, and see every sign-in and change on `/admin/audit`. The admins themselves come from the environment's Key Vault, so you can never lock yourself out and the addresses stay out of the repository and the workflow logs: run `just site-admins test` (and `prod`), enter the addresses, comma-separated, and restart the app. Microsoft work and school accounts get in only from tenants listed in `Authentication__Microsoft__TrustedTenants`, because any tenant can claim any email address; personal Microsoft accounts and Google accounts with a verified address always can.

## Privacy policy and terms

The sample site comes with a privacy policy (`/privacy`) and terms of use (`/terms`), written for the EU's GDPR and for what Weesite actually records: page views without IP addresses or cookies, a theme cookie only when a visitor picks a mode, short-lived logs. Before going live, edit both under Pages in the admin: your name and contact address, the Azure region, your country and the date, which are in brackets. The terms license your content under CC BY 4.0 and code samples under MIT No Attribution; change that if you want other terms. Site settings choose which pages the footer links as Privacy and Terms; choose none to leave a link out.

## App settings

The site's own choices (its name, menu, theme, footer, privacy and terms pages) are site settings, edited in the admin. App settings tune the software: rate limits, search, telemetry, sign-in. [`src/Weesite.Web/appsettings.example.json`](../src/Weesite.Web/appsettings.example.json) lists every one, with its default and what it does; set only what you change.

Where to set them:

- **Per environment, not secret:** the repository variables `APP_SETTINGS_TEST` and `APP_SETTINGS_PROD`, each a JSON object of environment variable names (`:` becomes `__`) and string values. `infra-apply` passes them to the Container App, and a change to them takes effect with the next apply (start `infra-apply` from the Actions tab):

  ```sh
  gh variable set APP_SETTINGS_PROD --body '{"RateLimits__SearchPerMinute": "60", "Telemetry__SamplingRatio": "0.1"}'
  ```

  The settings `infra/app` sets itself (storage, Key Vault, telemetry and Data Protection connections, `Seed__Path`, `Site__NoIndex`) can't be overridden this way; the plan says so.
- **Secrets:** the environment's Key Vault, as a secret named with `--` for `:` (`Authentication--Google--ClientSecret`). The app reads its Key Vault at startup, so restart it after a change (Azure portal, the Container App, Revisions, Restart). `just site-admins` and `just google-signin` do this for their settings.
- **Locally:** `dotnet user-secrets` for the web project, or environment variables; Aspire sets the storage connections itself.

## Custom domain

Prod can serve your own domain from an Azure DNS zone; test always uses the hostname Container Apps generates.

1. Set `prod_domain` in `bootstrap.auto.tfvars` and run `just bootstrap-apply`. This sets the repository variable `PROD_DOMAIN`.
2. Run `infra-apply` (it runs on the next infrastructure change on `main`, or start it from the Actions tab). The prod apply creates the zone and lists its four name servers in the job summary.
3. At your registrar, replace the domain's name servers with those four. Check with `dig NS <domain> +short`; propagation can take up to 48 hours.

4. Run `infra-apply` again once `dig NS` shows them. The prod apply then:
   - adds the records: an A record for the domain (the Container Apps environment's IP address), a CNAME for `www` (the app's hostname) and the two `asuid` TXT records that prove the domain is yours;
   - adds both hostnames to the app and has Container Apps issue a free certificate for each, which takes a few minutes; it renews them itself;
   - binds each certificate to its hostname (`infra/app/domain.tf`);
   - sets `Site__BaseUrl` to `https://<domain>`, so canonical links, Open Graph, feeds and the sitemap use it, and `www` redirects to the bare domain with a 301 (308 for other methods);
   - points the availability test at the domain, so it checks the certificate too.

   The apply in step 2 already tries this and fails at adding the hostnames while the name servers still point elsewhere: Container Apps checks the `asuid` records over public DNS. Running it again after the delegation is enough.
5. Check: `curl -sI https://<domain>/` answers 200, and `curl -sI https://www.<domain>/` answers 301 with `location: https://<domain>/`. The redirect is the app's: if `www` answers 200 instead, prod still runs an image from before it, so run `promote`.

The zone has `prevent_destroy`: removing `prod_domain` later makes the prod plan fail rather than delete the zone, because a new zone gets new name servers.

The certificates renew only while their records stay as they are: an A record for the domain, and a CNAME for `www` that points straight at the app (not through a CDN or another CNAME). If you add a CAA record to the zone, allow DigiCert (`0 issue "digicert.com"`), the certificates' issuer. The Container Apps hostname keeps working beside the domain; its pages name the domain as canonical.

## Backups and restore

What keeps your content safe, from the quickest way back to the most thorough:

- **Revisions:** every save of a post or page keeps the earlier text. The editor lists them under Versions; open one and save to restore it. Earlier versions are kept for 90 days.
- **Blob versioning and soft delete:** the storage account keeps every earlier version of every file (content, media, settings) for 90 days, including the last version of a deleted file, and deleted containers for 30 days.
- **Backup zip:** Backup in the admin downloads everything as one zip (content, settings, media), and imports one, reviewed item by item. Keep one before large changes; it's also how you move a site.
- **Factory reset:** the danger zone at the bottom of Site settings deletes all content, media and settings (after you type the site's name), for clearing test content or a clean start before importing a backup. Its files can be restored like any deleted file, below.

### Restore a file by hand

For a file the editor can't bring back, such as one deleted outside the editor. You need a data role on the environment's storage account, which subscription Owner doesn't include. Give yourself one for the job and remove it afterwards:

```sh
env="test"   # or "prod"
acct=$(az storage account list -g "rg-weesite-$env" --query "[0].name" -o tsv)
scope=$(az storage account show -n "$acct" --query id -o tsv)
me=$(az ad signed-in-user show --query id -o tsv)
az role assignment create --assignee "$me" --role "Storage Blob Data Contributor" --scope "$scope"
# The role can take a few minutes to apply.
```

Posts are `posts/<name>.md` and pages `pages/<name>.md` in the `content` container; media files are in `media`, site settings (`site.json`, `authors.json`) in `settings`. Say which file, once; the commands below use these:

```sh
container="content"
file="posts/restore-test.md"
```

1. List the file's versions, newest last:

   ```sh
   az storage blob list --account-name "$acct" --container-name "$container" --prefix "$file" \
     --include v --auth-mode login \
     --query "[].{version: versionId, current: isCurrentVersion, modified: properties.lastModified}" -o table
   ```

   A file that exists has one version marked `True` under Current. A deleted file has none, and the table then leaves the Current column out; its last content is the newest version.

2. Copy the version you want back over the file:

   ```sh
   version="2026-11-10T15:44:08.7486328Z"   # a version from the list
   az storage blob copy start --account-name "$acct" --auth-mode login \
     --destination-container "$container" --destination-blob "$file" \
     --source-uri "https://$acct.blob.core.windows.net/$container/$file?versionid=$version"
   ```

3. Tell the site: the replicas reload when the version marker changes (they check every 30 seconds), and a change made outside the app doesn't touch it.

   ```sh
   az storage blob upload --account-name "$acct" --auth-mode login --container-name settings --name version \
     --data "$(date +%s)" --overwrite
   ```

4. Remove the role: `az role assignment delete --assignee "$me" --role "Storage Blob Data Contributor" --scope "$scope"`.

A whole deleted container comes back with `az storage container list --account-name "$acct" --include-deleted --auth-mode login` (note its `version`) and `az storage container restore --account-name "$acct" --name <container> --deleted-version <version> --auth-mode login`, followed by step 3.

### Test the restore

Once per environment, and after changes to storage: on test, write a post in the editor (`restore-test`), save it twice with different text, and restore the first text from Versions. Then delete `posts/restore-test.md` with `az storage blob delete --account-name "$acct" --auth-mode login --container-name content --name posts/restore-test.md`, bump the marker (step 3) and check the post is gone, restore it with steps 1 to 3 and check it's back. Delete it again when done.
