# Deploy reference

How a Weesite install is put together and run, by topic. For a first install, follow [Install Weesite](install.md): it goes step by step and links here for the details.

Nothing in the repository names a deployment: your domain, hostnames and IDs live in your fork's GitHub variables and secrets. The names below are a first install's, such as `rg-weesite-test`; a [second install](#second-install) has its instance in them.

## Requirements

- An Azure subscription where you are Owner (or Contributor plus User Access Administrator). The bootstrap assigns roles, creates two custom role definitions and registers resource providers.
- Global Administrator or Privileged Role Administrator in the subscription's Microsoft Entra tenant: the first `just bootstrap-apply` grants Microsoft Graph app roles to the identities ([Microsoft: OpenTofu](#microsoft-opentofu)). The account that owns a personal subscription has it.
- A GitHub account or organisation that doesn't host another Weesite, and a domain you control (the domain is optional).
- The tools the recipes use ([dev-setup.md](dev-setup.md#prerequisites)): OpenTofu, the Azure CLI, `just`, Conftest, `jq` and `gh` (or a GitHub token).
- A fork created after 15 July 2026, or a repository opted in to GitHub's immutable OIDC subject claims. The federated credentials trust that subject format.

## Repository

### Fork

Fork (or copy) the repository into an account or organisation that doesn't host another Weesite: the image goes to `ghcr.io/<owner>/weesite`, so two installs under one owner would share it. GitHub doesn't let you fork into your own account, so a second install of your own goes in a free organisation. A fork's workflows are off until you enable them on its **Actions** tab.

### Repository settings

`just github-settings <owner>/<repo>` applies the repository settings, security features, Actions permissions, labels, topics and rulesets (`scripts/github-settings.sh`, `.github/rulesets/`), then reads them back. `just github-settings-check <owner>/<repo>` only reads them. Run it again after merging changes to the settings or rulesets.

- On a new repository, add `--no-required-checks` until `lint`, `dco`, `infra-static`, `infra-plan`, `build`, `format`, `browser (minified)`, `image` and `dependency-review` have run once, on its first pull request; then run it again without.
- CodeQL is set up for the languages GitHub has detected. Until the fork's workflows are on, that leaves out `actions`, and the script says so.
- Until the bootstrap has run, `infra-plan` and `infra-apply` skip their work with a note.

## Bootstrap

`infra/bootstrap` prepares the subscription and the fork: the environments' resource groups and budgets, the state storage and its Key Vault, the identities the workflows sign in as, their roles, and the fork's GitHub environments, variables and secrets. You run it on your machine; CI never does. [infra/bootstrap/README.md](../infra/bootstrap/README.md) describes what it creates.

### Bootstrap inputs

`infra/bootstrap/bootstrap.auto.tfvars` (gitignored; copy it from `bootstrap.auto.tfvars.example`) holds them:

- `subscription_id` and `location` (the state storage, its Key Vault and the identities). The location must be in `policy/data/policy.yaml`; edit that list for your deployment first.
- `environment_location` (optional, defaults to `location`): the region of the test and prod resource groups and everything in them. Each environment runs one Container Apps environment, and subscriptions have a per-region limit on those (Azure portal, Quotas, Container Apps, Managed Environments): pick a region with room for two. Some regions also turn away new subscriptions for some services (`RequestDisallowedByAzure`); before choosing one, create a Key Vault, a storage account and a Container Apps environment in a throwaway resource group there.
- `name_suffix`: 3–6 lowercase letters or digits that make the storage account and Key Vault names globally unique. Pick it once.
- `instance` (optional, empty by default): only for a [second install](#second-install) in a subscription or tenant that already has one.
- `github_owner`, `github_repository`, and `prod_reviewers` (the GitHub logins that approve prod deployments).
- `budget_amount` (per environment, default 30) and `budget_contact_emails`.
- `prod_domain` (optional): your site's domain, for example `example.com`. `infra/app` creates its Azure DNS zone in prod; see [Custom domain](#custom-domain).
- `environment_variables` (optional): extra GitHub environment variables per environment, for your own workflows; Weesite's workflows don't read any.

### Second install

For a second Weesite in a subscription, or in a tenant (directory), that already has one, set `instance`, such as `second`: 1 to 10 lowercase letters or digits, starting with a letter. Give it its own `name_suffix` as well. The tenant counts too because some names are the tenant's, not the subscription's: `infra/app` finds the environment's apply identity in Entra by its name (`id-weesite-apply-<env>`), and two installs with the same name in one tenant make that lookup fail for both; the sign-in app registrations (`weesite-<env>`) would share a name too.

- The resource groups become `rg-weesite-second-test`, `rg-weesite-second-prod` and `rg-weesite-second-tfstate`, and the identities, custom roles, budgets, log workspace and Entra app registrations get it too, so nothing clashes. Names inside a resource group can't clash, and the global names (storage account, Key Vault) carry a hash of their group's ID.
- The bootstrap sets the repository variable `WEESITE_INSTANCE`, which the workflows use. The maintainer recipes (`just site-admins`, `restart`, `url`, `google-signin`, `smtp`) read it too, through `gh`, from the repository your clone points at, and say which resource group they use before they do anything; `WEESITE_INSTANCE` in your shell overrides it.
- Pick it once: changing it re-creates everything.

### Running the bootstrap

Sign in with the Azure CLI on the right subscription (`az login`, then `az account set --subscription <id>`). For GitHub, either export `GITHUB_TOKEN` with admin rights on your fork, or sign in to `gh`; the recipes then use `gh auth token`.

The first run can't keep its state in the storage account it's about to create, so it starts with local, unencrypted state and moves it afterwards:

```sh
just bootstrap-first-run   # local, unencrypted state for this run only
just bootstrap-plan        # about 77 resources to add; then the policy check on the plan
just bootstrap-apply       # shows the plan again and asks for confirmation
just bootstrap-migrate     # moves the state into the storage account, encrypted
rm infra/bootstrap/terraform.tfstate infra/bootstrap/terraform.tfstate.backup
```

- The apply waits two minutes after granting you Key Vault Crypto Officer, because new role assignments take a while to reach the Key Vault data plane.
- `just bootstrap-migrate` moves the state into the `bootstrap` container of the storage account, encrypted with its own key; OpenTofu asks before copying. A plan then runs against the remote state and should show no changes. Then delete the local plaintext copies (the `rm` above).
- A plan before the first run stops and says to run `just bootstrap-first-run`.

Later changes use `just bootstrap-plan` and `just bootstrap-apply`, with the encrypted remote state.

### Updating an install from 1.0

Release 1.1 changes the bootstrap: pull request plans wait for approval in a `plan` environment, the helper workflows sign in as identities of their own, the plan identities read only the Key Vault secrets `infra/app` writes, and the web app sends its telemetry with its identity. In this order:

1. Set `plan_secrets_group_scope = true` in `bootstrap.auto.tfvars`, then `just bootstrap-plan` and `just bootstrap-apply`. It keeps the plan identities' old access to the Key Vault secrets until step 3 has replaced it.
2. Take the release onto `main`. `infra-apply` grants the plan identities the secrets one by one, and the helper workflows their data roles; `app` deploys the image that sends telemetry with the app's identity. Promote that image to prod before you approve `infra-apply`'s prod step: from then on prod's Application Insights takes telemetry only through Entra ID.
3. Remove `plan_secrets_group_scope` and run `just bootstrap-plan` and `just bootstrap-apply` again: it removes the old access.

Until step 1, pull request plans can't sign in: they now run in the `plan` environment, which the old bootstrap's identities don't trust.

### What it sets in GitHub

The bootstrap sets these; don't create or edit them by hand:

- Environments: `test` and `prod` (deploy from `main` only; `prod` waits for a reviewer), `test-ops` and `prod-ops` for the helper workflows (`seed`, `load-test`, `geoip`; from `main` only; `prod-ops` waits for a reviewer), and `plan`, where pull request plans wait for a reviewer ([Pull request plans](#pull-request-plans-infra-plan)). The reviewers are `prod_reviewers`; admins can't bypass any of them.
- Environment secrets on `test` and `prod`: `AZURE_CLIENT_ID` (that environment's apply identity), `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`; the same on `test-ops` and `prod-ops`, with that environment's ops identity.
- Repository secrets for plans: `AZURE_PLAN_CLIENT_ID` (test: from `main`, and from pull requests in the `plan` environment), `AZURE_PLAN_MAIN_CLIENT_ID` (prod, from `main` only), `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`. Dependabot secrets with the first and the last two, so Dependabot's pull requests can be planned too.
- Repository variables: `TF_STATE_RESOURCE_GROUP`, `TF_STATE_STORAGE_ACCOUNT`, `TF_STATE_KEY_VAULT_URI`; `PROD_DOMAIN` when `prod_domain` is set, and `WEESITE_INSTANCE` when `instance` is (a [second install](#second-install)).
- Whatever you put in `environment_variables`.

To add or change a variable, edit `environment_variables` and run `just bootstrap-apply`.

### Hand the bootstrap over to another account

The bootstrap gives a few roles to whoever runs it. Subscription Owner doesn't include them, because they're data-plane roles:

- Key Vault Crypto Officer on the state Key Vault;
- Storage Blob Data Contributor on the `bootstrap` state container;
- Key Vault Secrets Officer on each environment's resource group, for `just google-signin`, `just site-admins` and `just smtp`.

To move them to another account in the same tenant, such as a new admin account:

1. Give the new account Owner on the subscription.
2. Give it two roles for the handover only:
   - Key Vault Crypto Officer on the state resource group, `rg-weesite-tfstate` (`rg-weesite-<instance>-tfstate` with an `instance`);
   - Storage Blob Data Contributor on the same resource group, or on the state storage account.

   Wait a few minutes for them to take effect. These broader scopes let it read and decrypt the bootstrap state. The bootstrap's own assignments sit on the Key Vault and the container, so the two don't clash.
3. Sign in as the new account: `az logout`, then `az login`.
4. Run `just bootstrap-plan`. It should replace the runner's role assignments, four with test and prod, and change nothing else apart from inputs you changed in `bootstrap.auto.tfvars`.
5. Run `just bootstrap-apply`.
6. Remove the two roles from step 2, then run `just bootstrap-plan` again. "No changes" shows that the new account works through its own roles.

The old account loses those roles and keeps everything else, such as Owner. Keeping it as a second Global Administrator for emergencies is a good idea.

The site's admins are separate from this. Update them with `just site-admins` ([Admins and editors](#admins-and-editors)). A Microsoft work account also needs its tenant in `Authentication__Microsoft__TrustedTenants`.

## Deploys

Two workflows deploy, and a third promotes. On a new fork, start `infra-apply`, then `app`, by hand from the Actions tab on `main`; after that they run by themselves.

### Infrastructure: `infra-apply`

It plans and applies `infra/app`: test first, then prod after you approve the `prod` environment in the Actions tab. It runs by itself when a push to `main` changes infrastructure files since its last successful run; started by hand, it applies regardless of what changed. The first run per environment also creates its state. The Container App starts on a placeholder image (Microsoft's ASP.NET sample). With `prod_domain` set, the prod apply fails at the hostnames until you finish [Custom domain](#custom-domain).

The apply jobs apply the plan their plan job saved, the one whose summary you approved; if it can't be downloaded, the job fails rather than plan again. Only an environment without state yet is planned in the apply job.

### Pull request plans: `infra-plan`

A pull request that changes what a plan reads (`infra/app`, the policy, the plan scripts and tools) is planned against test, and the change counts and resource addresses go to a comment on it. The plan waits in the `plan` environment until a reviewer approves it on the run's page (**Review deployments**). Approve it only after reading the pull request, including any change to workflows and scripts: the job runs that code signed in as the plan identity, which reads test's state and the secrets `infra/app` writes to test's Key Vault. A workflow on a branch can't sign in as the plan identity any other way (T-0177). Pull requests that change nothing a plan reads, forks' pull requests and repositories without a bootstrap skip the plan and pass without waiting. Dependabot's pull requests wait for approval too.

`infra-plan`, the required check, passes once the plan has passed or wasn't needed; it stays pending while the plan waits for approval, and fails if the approval is rejected or the plan fails.

### The site: `app`

It runs on every push to `main`, and can be started by hand on `main` (on another branch it only builds and tests). It builds, tests and scans the image, pushes it to `ghcr.io/<owner>/weesite:<commit>`, and deploys it to test by digest (`deploy (test)`). It waits until the new revision has all the traffic and `/health` answers Healthy (until then the previous revision keeps serving, so its answers don't count), and fails if the revision doesn't get there in 5 minutes or fails to start; only then does it point the re-index job at the same image. If `infra-apply` hasn't created the Container App or the job yet, the deploy job warns and skips; re-run it from the Actions tab once `infra-apply` has finished.

The first push may create the package as private, and Container Apps pulls it without credentials: **deploy (test)** then stops and says so. Make the `weesite` package public (the Packages tab of your GitHub profile, or of the organisation that owns the fork, then `weesite`, Package settings, Change visibility), then re-run the failed job.

### Sample content on test

Test starts empty. To try it with the sample site from `seed/` (about 100 posts with photos, a few pages and settings, under a placeholder identity), start **`seed`** from the Actions tab once `infra-apply` and `app` have run. It runs in the `test-ops` environment and uploads the files as test's ops identity, not the apply identity: `infra/app` gives it Storage Blob Data Contributor on the `content`, `media` and `settings` containers and Queue Data Message Sender on the re-index queue, on test only. Then the site reloads and the re-index job embeds the posts. It can run again after `seed/` changes; files changed in the editor on test are kept. Prod never gets the seed.

### Visitor countries: `geoip`

The analytics count visitors' countries (people only, not bots or agents) with DB-IP's [IP to Country Lite](https://db-ip.com/db/download/ip-to-country-lite) database, CC BY 4.0; the analytics page credits it. **`geoip`** keeps it current: on the 3rd of each month, and when started by hand on `main`, it downloads the month's file, checks it (`scripts/geoip-fetch.sh`: the gzip file is whole, the size is plausible, the MaxMind DB metadata is there and names a country database), and uploads it to each environment's storage as `geoip/country.mmdb`, with the month as blob metadata. The download runs without Azure access; each upload runs in the environment's `<env>-ops` environment and signs in as its ops identity, which `infra/app` gives Storage Blob Data Contributor on the `geoip` container (`geoip.tf`) and the bootstrap Reader on the environment's resource group, to find the storage account. The prod upload waits for your approval in the `prod-ops` environment, like a deploy. It needs no variables or secrets beyond the ones the bootstrap sets.

On a new fork, start it by hand once `infra-apply` has run. The app loads the file in the background after it starts and checks for a new one once a day (hourly while there's none): until then, views record no country. It only reads the file, so every replica can do the same.

To stop recording countries, turn off **Record visitors' countries** in the admin's Site settings: no address is looked up and no country stored, and the analytics keep the countries recorded before. To stop the downloads too, disable the `geoip` workflow in the Actions tab. Locally, `Analytics:CountryDatabase` points the app at a database file instead.

### Promote to prod

Start **`promote`** from the Actions tab, on `main`. It reads the image digest test runs, accepts only this repository's own package (`ghcr.io/<owner>/weesite@sha256:…`), waits for your approval in the `prod` environment, deploys that same digest to prod, and checks the new revision as for test.

### Where things run

- test: the hostname Container Apps generates (`just url test`). It sends `X-Robots-Tag: noindex`, so it stays out of search engines.
- prod: your own domain, once it's set up ([Custom domain](#custom-domain)); until then, the hostname Container Apps generates.

### Logs and audit trails

- `log-weesite-<env>`: the Container Apps logs and Application Insights telemetry, 30 days, with a daily cap (0.1 GB on test, 0.5 GB on prod) so a runaway log can't run up a bill.
- `log-weesite-<env>-audit`: every access to the app Key Vault's secrets and keys, and every write and delete in the app's storage (posts, pages, settings, media, the key ring; the editor, connected app and audit log tables; re-index requests), 90 days, with a cap of its own (0.2 GB, 1 GB on prod) so a busy day of app logs can't stop it. Table write logs name each entity, editors' email addresses among them. What anonymous requests cause isn't logged there: the table writes of page views, reactions, comments, contact messages, the spam protection counters and OAuth client registrations, batches included, are dropped by a workspace transformation before they're stored, so the volume doesn't follow traffic and a busy day can't fill the cap. Should it fill anyway, an alert emails the subscription's Owners: until the next UTC day the workspace stores nothing more.
- Application Insights takes telemetry only from the web app's managed identity (Entra ID), not from anyone holding its connection string.
- `log-weesite-state` in `rg-weesite-tfstate`: every read, write and delete of the OpenTofu states and every use of their keys, 90 days. The states hold the sign-in client secret, so this is the trail of who read them.

## Sign-in

The editor at `/admin` signs in with Microsoft and Google accounts ([ADR 0004](adr/0004-sign-in.md)). Each environment has its own app registration with each provider. The secrets live in the environment's Key Vault, and the app reads them at startup; a provider without its secrets stays off.

### Microsoft: OpenTofu

`infra/app` creates the Entra app registration, its client secret (rotated every 180 days) and the Key Vault secrets. The secret is also in the environment's OpenTofu state: the azuread provider keeps a generated password there, and has no write-only form. The state is encrypted with a Key Vault key. Prod's is readable only by the maintainer and the workflows on `main`; test's also by the plan in pull requests from branches of this repository (not forks), once a reviewer approves it in the `plan` environment (T-0122, T-0177). That plan reads, of test's Key Vault, only the secrets `infra/app` writes there (the Microsoft client and the Application Insights connection string), not the ones you store by hand. The apply identities need permission to manage app registrations, which the bootstrap grants:

1. Run `just bootstrap-apply` signed in as a Global Administrator or Privileged Role Administrator of the tenant (the account that owns a personal Azure subscription is Global Administrator of its directory). It grants Microsoft Graph `Application.ReadWrite.OwnedBy` to the apply identities and `Application.Read.All` to the plan identities, and Key Vault secret roles to them and to you.
2. Run `infra-apply`. It registers `https://<hostname>/signin-microsoft` for the environment (and `https://<domain>/signin-microsoft` with a custom domain); test also gets `http://localhost:5080/signin-microsoft` for local development. Find the hostname with `just url <env>`; the workflow doesn't print it, because its logs are public.

### Google: by hand, once per environment

Google has no API for OAuth clients, so you create them in the console:

1. In the [Google Cloud console](https://console.cloud.google.com/), create a project (for example `weesite`) or pick one.
2. Open **Google Auth Platform** and choose **Get started**: app name (your site's name), user support email, audience **External**, contact email. Create.
3. Under **Data access**, add the scopes `openid`, `.../auth/userinfo.email` and `.../auth/userinfo.profile`. These are non-sensitive, so the app needs no verification.
4. Under **Audience**, choose **Publish app**. While it's in testing, only listed test users can sign in. The editor's allowlist decides who gets in either way.
5. Under **Clients**, choose **Create client**, type **Web application**, named after the environment (`weesite-test`). Add the authorized redirect URIs, the same as Microsoft's with `/signin-google`. For test that's `https://<test hostname>/signin-google` and `http://localhost:5080/signin-google`; for prod `https://<prod hostname>/signin-google` and, with a custom domain, `https://<domain>/signin-google`.
6. Copy the client ID and secret, and store them: `just google-signin test` (or `prod`). It first prints the exact redirect URIs the client must list (step 5), then asks for the ID and secret and writes them to the environment's Key Vault.
7. Restart the app so it reads them: `just restart test` (or `prod`).

The test hostname: `just url test`.

### What the consent screens show

Both providers show the site's name, logo and links when someone signs in for the first time. The links are `https://<domain>/` (or the Container Apps hostname without a domain), `/legal/privacy` and `/legal/terms`: the site redirects those two to the pages chosen under [Privacy policy and terms](#privacy-policy-and-terms), so choose them first.

The logo is the site's own once **Site settings → Logo** has one (an image from Media, SVG or PNG; **Favicon** takes a separate image for the browser tab if the logo doesn't work that small); under the field are the two sizes the sign-in screens take, to download: 120 px for Google and 215 px for Microsoft. Both providers reject a logo that doesn't identify you, so set your own before submitting Google's branding for review.

**Microsoft.** `infra/app` names the app after the domain (`weesite-<environment>` without one), sets the three links, and Weesite's logo when it creates the app. To show your own, upload the 215 px PNG under **Branding & properties → Upload new logo**; later applies leave it. Until a publisher domain is verified, the consent screen says the app is unverified; Graph doesn't let OpenTofu set it, so it's one step in the portal, once per app, after the domain works:

1. In the [Microsoft Entra admin center](https://entra.microsoft.com/), open **App registrations**, then the prod app (named after the domain), then **Branding & properties**.
2. Under **Publisher domain**, choose **Update domain**, then **Verify a new domain**, and enter the site's domain. The site already serves the file Microsoft asks for, `https://<domain>/.well-known/microsoft-identity-association.json`, with this app's client ID; choose **Verify and save domain**.

**Google.** Under **Google Auth Platform**, then **Branding**:

1. App logo: the 120 px PNG from Site settings.
2. App home page `https://<domain>/`, privacy policy `https://<domain>/legal/privacy`, terms of service `https://<domain>/legal/terms`.
3. Authorized domains: the site's domain. Google accepts it once it's verified in Search Console under the same Google account ([Search engines](#search-engines)).
4. Save. A logo makes Google review the branding before it shows it (**Verification Center**, usually a few days); sign-in keeps working meanwhile, without the logo.

Test has no domain of its own, so it keeps the defaults: Google can't verify a Container Apps hostname, and Microsoft's publisher domain needs a domain too.

### Admins and editors

Only allowlisted email addresses get in. Admins add further editors, as admin or author, on `/admin/editors`, and see every sign-in and change on `/admin/audit`. The admins themselves come from the environment's Key Vault, so you can never lock yourself out and the addresses stay out of the repository and the workflow logs: run `just site-admins test` (and `prod`), enter the addresses, comma-separated, then `just restart test` (and `prod`) so the app reads them.

Microsoft work and school accounts get in only from tenants listed in `Authentication__Microsoft__TrustedTenants`, because any tenant can claim any email address; personal Microsoft accounts and Google accounts with a verified address always can. So before an admin with a work account signs in for the first time, add its tenant ID (a GUID; several separated by commas) to the [app settings](#app-settings) of both environments, then run `infra-apply`:

```sh
gh variable set APP_SETTINGS_TEST --body '{"Authentication__Microsoft__TrustedTenants": "00000000-0000-0000-0000-000000000000"}'
```

Without it, the sign-in ends on "Not allowed in" like any address that isn't on the list. The page doesn't say which check failed; the log does, without the address. It takes a few minutes to arrive:

```sh
az monitor app-insights query --app appi-weesite-test --resource-group rg-weesite-test --offset 1d \
  --analytics-query "traces | where message startswith 'Sign-in with' | top 5 by timestamp | project timestamp, message" \
  --query "tables[0].rows" --output tsv
```

`--offset` sets how far back it looks; without it, `az` looks at the last hour only, whatever the query says.

### Invitations by email

When an admin adds an editor or changes their role, Weesite sends them an invitation with the sign-in link ([ADR 0010](adr/0010-email-over-smtp.md)). Any mail service that accepts SMTP works: Microsoft 365's High Volume Email, Postmark, Mailgun, Brevo, Amazon SES or another. Without settings nothing is sent. Either way, `/admin/editors` has each editor's invitation, under the list, to copy and send yourself.

1. Pick a provider and a sender address on a domain it lets you send from, such as `noreply@mail.<domain>`. Add the SPF and DKIM records it asks for: on prod, in `PROD_DNS_RECORDS` like the other records under [Email](#email). Use a subdomain if the domain already sends mail through another service.
2. Run `just smtp test` (and `prod`). It asks for the server, the port (587, or 465 with `SslOnConnect`), the user name, the password and the sender address, and stores them in the environment's Key Vault as `Email--Smtp--*` and `Email--From`.
3. Restart the app (`just restart <env>`). The Editors page then says that invitations are sent.

Rotate the password with the provider, then run `just smtp <env>` again and `just restart <env>`. Name the provider in your privacy policy, where the sample says `[your mail provider]`. Each invitation is in the audit log (`editor-invited`, or `editor-invite-failed` with the reason).

### Contact form

`/contact` can take messages from visitors ([ADR 0011](adr/0011-visitor-input.md)): a name, an email address and up to 1,000 characters of plain text. Turn it on under **Site settings → Contact form**:

- **Page above the form** (optional): a [special page](#privacy-policy-and-terms) whose title and text go above the form; without one, the form has its own heading. With the form off, `/contact` shows that page alone.
- **Messages go to:** the address the hourly digest goes to. It's never shown on the site, unlike the contact address in the footer.

To link to the form, add a menu item that goes to **Contact form** (**Menu**, offered while the form is on).

Messages wait under **Messages** in the admin (admins only), newest first with the unread count in the menu, until you delete them; they're deleted automatically after 12 months. With [email set up](#invitations-by-email), one mail an hour lists what came in: the first message after a quiet hour goes out within a few minutes, later ones wait for the hour. When the mail server refuses an address for good, the log says so and that mail isn't tried again; a mail that failed for a moment is tried again each minute until the next digest is due. Without email, there's no digest, only the Messages page. On test, which stops when idle, a waiting digest goes out when the site next starts.

Spam is held back without a third party: a field people never see, a minimum time between showing the form and sending it, an invisible proof of work the browser solves while the visitor types, plain-text rules (no HTML or BBCode links, images or scripts, at most 3 links) and 5 messages an hour per address. Each page's form takes one message, and a post over 32 KB is refused. The address is only ever kept as a hash that changes daily. A message sent without JavaScript, or whose proof failed, still arrives, marked **unverified**. The limits are app settings under `Visitor` and `RateLimits:VisitorPerMinute` ([App settings](#app-settings)).

The sample privacy policy has a section on contact messages; check it before turning the form on, and name your mail provider there.

### Comments

Posts can take comments from visitors ([ADR 0011](adr/0011-visitor-input.md)): a name and up to 1,000 characters of plain text, in which addresses become links marked `rel="ugc nofollow"` and lines starting with `>` or `|` show as quotes (each comment's **Reply** quotes it into the form). A post shows ten comments, oldest first, and **Show more** opens ten more. Turn them on under **Site settings → Comments** (off by default). A post closes its own comments with **Close comments on this post** in the editor (`comments: false` in its front matter); the comments it has already stay.

- **Moderation:** every comment, and every edit of a published one, waits under **Comments** in the admin until an editor approves or rejects it: admins moderate every post's comments, authors their own posts'. A published comment stays as it was while its edit waits. Rejected comments are deleted after 30 days, and so are comments that waited 30 days without a decision; an edit that waited that long goes, and the published text stays.
- **On the post:** signed in, a moderator sees the comments waiting on the post itself, with **Approve** and **Reject**; an admin can also **Remove** a published comment there, which is the way to take one down, as the Comments page lists only the waiting and the rejected ones. A removed comment leaves a marker where a later comment follows it.
- **What you approve is what you saw:** approving publishes the text the page showed you. If its writer changed or deleted the comment in the meantime, or another moderator decided first, nothing happens and the page says so: look at it again.
- **Editors comment under their own name:** signed in, an editor's comment carries their name and an **Author** mark on their own post or **Editor** elsewhere; an admin's, and the post's author's, are public at once.
- **Digest:** with [email set up](#invitations-by-email), each moderator gets one mail an hour at most listing what waits for them, as the contact form's digest goes: at most 50 comments, with a count of the rest.
- **Apps:** the editor MCP endpoint's `list_comments`, `moderate_comment` and `delete_comment` do the same with the same rights; moderating needs an app allowed to publish ([MCP](mcp.md)).
- **Visitors:** the form appears only with JavaScript; approved comments are part of the page and read without it. A writer's browser keeps a key that lets it edit or delete their comment; nobody signs in.

Spam is held back as on the contact form, except that a comment without a valid proof of work is refused: the form's script always sends one. The limits are `Visitor:CommentsPerHour` (5) and, for a writer's edits and deletions, `Visitor:CommentChangesPerHour` (20). Turned off, comments disappear from the posts and the form goes; they're kept, and come back when you turn comments on again.

The sample privacy policy has a section on comments; keep it while comments are on, and remove it if you leave them off.

## Privacy policy and terms

**Special pages.** The home page's page (Site settings → Home page → A page), the privacy policy, the terms and the contact form's text are *special pages*: pages without a slug (in the editor, **No address of its own**). Site settings choose them and show them at `/`, `/privacy`, `/terms` and `/contact`; they aren't in the menu, but search and the sitemap have them once placed. `privacy`, `terms` and `contact` are reserved slugs.

**Upgrading a site from before special pages:** Site settings named these pages by slug, which no longer finds them, so the footer links and the contact form's text disappear until you move them over by hand. For each page, open it under Pages, tick **No address of its own** and save; then choose it again in **Site settings** and save. Pages that still have the slug `privacy`, `terms` or `contact` keep loading, so you can find them.

The sample site comes with a privacy policy (`/privacy`) and terms of use (`/terms`), written for the EU's GDPR and for what Weesite actually records: page views without IP addresses or cookies (with the visitor's country, unless Site settings turn it off), a theme cookie only when a visitor picks a mode, an editor cookie only in the browsers editors sign in with, short-lived logs. Before going live, edit both under Pages in the admin: your name and contact address, the Azure region, your country and the date, which are in brackets. The terms license your content under CC BY 4.0 and code samples under MIT No Attribution; change that if you want other terms. Site settings choose which pages the footer links as Privacy and Terms; choose none to leave a link out. The policy has a section on reactions to posts ([ADR 0011](adr/0011-visitor-input.md)), which the sample site turns on: keep it while **Site settings → Reactions** is on, and remove it if you leave them off (they're off by default).

## App settings

The site's own choices (its name, menu, theme, footer, privacy and terms pages) are site settings, edited in the admin. App settings tune the software: rate limits, search, telemetry, sign-in. [`src/Weesite.Web/appsettings.example.json`](../src/Weesite.Web/appsettings.example.json) lists every one, with its default and what it does; set only what you change.

Where to set them:

- **Per environment, not secret:** the repository variables `APP_SETTINGS_TEST` and `APP_SETTINGS_PROD`, each a JSON object of environment variable names (`:` becomes `__`) and string values. `infra-apply` passes them to the Container App, and a change to them takes effect with the next apply (start `infra-apply` from the Actions tab). `gh variable set` replaces the whole object, so give every setting each time:

  ```sh
  gh variable set APP_SETTINGS_PROD --body '{"RateLimits__SearchPerMinute": "60", "Telemetry__SamplingRatio": "0.1"}'
  ```

  The settings `infra/app` sets itself (storage, Key Vault, telemetry and Data Protection connections, `Site__NoIndex`) can't be overridden this way; the plan says so.

  For a site whose posts aren't all in English, `"Search__EmbeddingModel": "multilingual-e5-small"` switches search by meaning to a model that knows about a hundred languages ([Languages](languages.md#search)). It gives the web replica 0.5 vCPU and 1 GiB instead of 0.25 and 0.5 GiB, about twice the compute cost, and the re-index job gets the same model; everything is re-embedded once.
- **Secrets:** the environment's Key Vault, as a secret named with `--` for `:` (`Authentication--Google--ClientSecret`). The app reads its Key Vault at startup, so restart it after a change: `just restart <env>`. `just site-admins`, `just google-signin` and `just smtp` store their settings there.
- **Locally:** `dotnet user-secrets` for the web project, or environment variables; Aspire sets the storage connections itself.

**The site's address (`Site:BaseUrl`):** set it everywhere but local runs. Without it, absolute links (canonical links, Open Graph, feeds, the sitemap) use the address each request came in on, and the comment and message digests leave out their link to the admin. Prod gets it from its [custom domain](#custom-domain); for test, or an environment without a domain, add its Container Apps address (`just url <env>`) to its settings: `"Site__BaseUrl": "https://<app>.<region>.azurecontainerapps.io"`.

### Proxies in front of the app

The rate limits and the hourly limits on what visitors send count per client address, which the app reads from `X-Forwarded-For`, and the scheme of absolute links comes from `X-Forwarded-Proto`. Each proxy in front of the app adds the address it received the request from to the end of `X-Forwarded-For`, so the app takes the entry the proxy furthest out added, and ignores what the client sent before it. Tell it how many there are:

- `ForwardedHeaders:Proxies` (1): the Container Apps ingress is one, and is the only way in, so the default fits a deployment as `infra/app` makes it. Put a CDN or another proxy in front of the ingress and it's 2; otherwise every visitor seems to come from that proxy's address and shares its limits.
- `0` when clients reach the app directly, with nothing in front (a container on a machine of its own): the headers are ignored, and the connection's own address and scheme count. With 1 or more and nothing in front, a client could name any address it likes and escape the limits.
- `ForwardedHeaders:KnownProxies` and `ForwardedHeaders:KnownNetworks` (empty): the proxies' addresses, or their networks such as `10.0.0.0/8`, comma-separated. Set, an entry counts only when it came over a connection from one of them, so a client that reaches the app past its proxies can't name an address. Leave them empty when the proxies are the only way in.

## Custom domain

Prod can serve your own domain from an Azure DNS zone; test always uses the hostname Container Apps generates.

1. Set `prod_domain` in `bootstrap.auto.tfvars` and run `just bootstrap-apply`. This sets the repository variable `PROD_DOMAIN`.
2. Run `infra-apply` (it runs on the next infrastructure change on `main`, or start it from the Actions tab). The prod apply creates the zone, then fails at adding the hostnames until the domain points at it. Read the zone's four name servers with `az network dns zone show --resource-group rg-weesite-prod --name <domain> --query nameServers --output tsv` (a later successful apply also lists them in the job summary).
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

### Search engines

Verify the domain with Google Search Console and Bing Webmaster Tools by DNS, and keep the records: both check them again from time to time. The records go in the repository variable `PROD_DNS_RECORDS`, a JSON object of TXT values and CNAME targets by record name (`@` is the domain itself), which the prod apply turns into records in the zone:

```sh
gh variable set PROD_DNS_RECORDS --body '{"txt": {"@": ["google-site-verification=<code>"]}, "cname": {"<code>": "verify.bing.com"}}'
```

Then run `infra-apply` (Actions tab), wait for the records (`dig TXT <domain> +short`), and choose Verify in each tool. Submit `https://<domain>/sitemap.xml` in both. `asuid`, `asuid.www` and `www` are the site's own (`infra/app/domain.tf`) and can't be set here.

To have Bing and the other engines that take part in IndexNow (Yandex, Naver, Seznam, Yep) hear about a post or page as soon as it's published, changed or taken down, turn on **Tell search engines about changes (IndexNow)** in the admin's Site settings on prod. The site generates its key (`indexnow-` and 32 hex digits), serves it at `https://<domain>/<key>.txt`, and sends each changed address to `api.indexnow.org`; the log says when the engines refuse one. Leave it off on test, so the test site's addresses aren't sent. Google doesn't take part in IndexNow: it reads the sitemap.

OpenTofu owns each record set it manages: a name and type, with all its values. Change those only through the variable. A value added in the portal to the TXT records at `@`, for example, is removed by the next apply. Record sets it doesn't manage, with a name or type of their own, are left alone, but they don't survive a rebuild of the zone, so keep every record in the variable.

### Email

To receive mail at the domain, for example with Microsoft 365 or another mail host, add the records the mail host asks for to `PROD_DNS_RECORDS`:

- MX, under `mx`, with a preference and a host name;
- SPF, as a TXT value at `@`, next to the search engines' values;
- DKIM, usually CNAMEs;
- DMARC, a TXT value at `_dmarc`.

For Microsoft 365:

```json
{
  "txt": {
    "@": ["google-site-verification=<code>", "v=spf1 include:spf.protection.outlook.com -all"],
    "_dmarc": ["v=DMARC1; p=reject; rua=mailto:dmarc@<domain>"]
  },
  "cname": {
    "<code>": "verify.bing.com",
    "selector1._domainkey": "<selector1 target from Microsoft 365>",
    "selector2._domainkey": "<selector2 target from Microsoft 365>",
    "autodiscover": "autodiscover.outlook.com"
  },
  "mx": {
    "@": [{"preference": 0, "exchange": "<domain with dashes>.mail.protection.outlook.com"}]
  }
}
```

Set it with `gh variable set PROD_DNS_RECORDS < <file>` (without `--body`, it reads the value from standard input), then run `infra-apply`. Microsoft 365's admin center shows the exact values under **Settings → Domains**, and checks them there.

A record created in the portal first has to be adopted, because the apply refuses to create one that already exists. Put its values in the variable, add an `import` block for it to `infra/app` in a pull request, and remove the block once the prod apply has run (an import of a record that doesn't exist fails). For the MX records at the domain itself:

```hcl
import {
  to = azurerm_dns_mx_record.extra["@"]
  id = "${azurerm_dns_zone.site[0].id}/MX/@"
}
```

TXT and CNAME records work the same way, with `azurerm_dns_txt_record.extra` and `/TXT/`, or `azurerm_dns_cname_record.extra` and `/CNAME/`, and the record's name.

## Backups and restore

What keeps your content safe, from the quickest way back to the most thorough:

- **Revisions:** every save of a post or page keeps the earlier text. The editor lists them under Versions; open one and save to restore it. Each post, page and settings file keeps its newest 50 earlier versions, however old, and older ones until 90 days after they were replaced: the app deletes the rest when the file is next saved. The app settings `Content:VersionsKept` and `Content:VersionDays` change the two numbers ([App settings](#app-settings)).
- **Blob versioning and soft delete:** the storage account keeps every earlier version of every file. For posts, pages and settings, that's the revisions above, and a deleted file's versions stay, its last text included, until a file is saved under the same name again, when the same limits apply. Earlier versions of media files (a replaced or deleted image) and of the app's own files go 90 days after they were written, which for a file that stayed unchanged longer than that is the day after it's replaced or deleted. A deleted version, and a deleted container, can still be undeleted for 30 days (soft delete).
- **Backup zip:** Backup in the admin downloads everything as one zip (content, settings, media), and imports one, reviewed item by item. Keep one before large changes; it's also how you move a site.
- **Reset:** Site settings' danger zone leads to **Reset the site** (`/admin/reset`, admins only), for clearing test content or a clean start before importing a backup. You tick what to delete and type the site's name. Ticked at first is what the sample content or a backup puts back: posts and pages, media, site settings and the search index. Comments, reactions, contact messages, the analytics, editors added in the admin, connected apps, the audit log, the spam protection counters and uploaded backups go only when ticked (Reactions takes the spam protection counters with it, as they record who has reacted today); kept comments and reactions reappear on a post that comes back under the same file. The admins in `Site:Admins`, the Data Protection keys and the country data always stay. When it's done the reset is the audit log's newest entry and the page says what went. You're signed out only if you deleted yourself, as an editor added in the admin. Its files can be restored like any deleted file, below.
- **Archive and delete:** archiving a post or page takes it off the site (its address answers 410 Gone) and keeps it in the editor under Archived; archiving a media file only hides it from the media picker. Deleting one permanently (after typing its slug or name) removes it from the site and the editor, its versions included; storage still keeps a post's or page's versions, and a media file's last version for at least 30 days, so it can be restored by hand, below.

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

   A version deleted in the last 30 days (a media file's, by the 90-day rule; or one past the revision limits) is soft-deleted, and not listed. `az storage blob undelete --account-name "$acct" --auth-mode login --container-name "$container" --name "$file"` brings back all of the file's soft-deleted versions; list them again.

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

## Keeping a fork up to date

Weesite's `main` moves between releases. A release is a tag, `v<x.y.z>`, with its notes on the repository's Releases page, taken from [CHANGELOG.md](../CHANGELOG.md). A push to your fork's `main` deploys to test straight away (`app`, and `infra-apply` when infrastructure files changed). Prod changes only when you start `promote`, or approve `infra-apply`'s prod step. How to update depends on what you do with the fork:

- **You run releases only.** Don't use GitHub's **Sync fork**: it brings the upstream `main` as it is, with everything merged since the last release. Take the release's tag instead:

  ```sh
  git remote add upstream https://github.com/<upstream owner>/weesite.git   # once
  git fetch upstream --tags
  git checkout main
  git merge --ff-only v1.1.0
  git push origin main
  ```

  `--ff-only` stops if your `main` has commits of its own: then the next case is yours. Before you push, read the release's notes, and those of every release you skipped, oldest first, for anything they ask you to do (lines starting "Upgrading"), such as starting a workflow or choosing a page again in Site settings. The push deploys to test; check it there, then promote to prod and approve `infra-apply`'s prod step if it asks.
- **You change Weesite in your fork.** Merge upstream when you choose: a release tag (`git merge v1.1.0`) for a finished version, or `upstream/main` for the newest. Do it on a branch and open a pull request, so CI checks the merged code before it reaches test, and read the release notes as above.
- **You work on Weesite itself.** Each merge to `main` goes to test; promote a build to prod when it's ready, and `just release` tags the version once prod runs it.

## Remove an install

Deleting the three resource groups removes almost everything. A few things live outside them: the sign-in app registrations in Entra, the bootstrap's custom roles on the subscription, and on GitHub the package. The commands name each one exactly; with a [second install](#second-install), put its instance in the names (`weesite-second-test`, `rg-weesite-second-test`, and so on), so nothing of another install in the same subscription or tenant matches.

1. The sign-in app registrations, one per environment, named `weesite-<env>` (prod's is named after the domain when there is one). Another install in the tenant may have one with the same name, so find them by their client ID, which each environment's Key Vault holds; check the list first, then delete them:

   ```sh
   for env in test prod; do
     vault=$(az keyvault list --resource-group "rg-weesite-$env" --query "[0].name" --output tsv)
     az keyvault secret show --vault-name "$vault" --name Authentication--Microsoft--ClientId --query value --output tsv
   done > signin-apps.txt
   while read -r app; do az ad app show --id "$app" --query "[displayName, appId]" --output tsv; done < signin-apps.txt
   while read -r app; do az ad app delete --id "$app"; done < signin-apps.txt
   ```

   Entra keeps deleted app registrations for 30 days, under **Deleted applications**, where they can be removed for good.
2. The resource groups, which takes 10 to 20 minutes:

   ```sh
   for g in rg-weesite-test rg-weesite-prod rg-weesite-tfstate; do
     az group delete --name "$g" --yes --no-wait
   done
   az group list --query "[?starts_with(name, 'rg-weesite-')].name" --output tsv   # until they're gone
   ```

   This deletes the DNS zone too, if there is one: point the domain's name servers elsewhere at the registrar.
3. Once they're gone, the custom roles (their assignments went with the groups), with your `name_suffix`:

   ```sh
   az role definition delete --name weesite-plan-container-app-secrets-<name_suffix>
   az role definition delete --name weesite-web-blob-versions-<name_suffix>
   ```

4. On GitHub, the `weesite` package (the owner's Packages tab, `weesite`, Package settings, Delete) and the fork, or the whole organisation.

What stays for a while, at no cost: the Key Vaults have purge protection, so their names stay taken for 90 days after the delete (give a new install another `name_suffix`), and deleted Log Analytics workspaces can be recovered for 14 days. The resource provider registrations stay; they cost nothing.
