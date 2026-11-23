# Install Weesite

Step by step from a fork to a site running on test and prod, signed in as its admin. Each step says what to run and what you should see. The [deploy reference](deploy.md) explains each part and covers what comes after: your domain, Google sign-in, email, backups.

Allow an hour or two, most of it waiting for workflows. In the commands, `<owner>/<repo>` is your fork, such as `example/weesite`.

## 1. Check what you need

- An Azure subscription where you are Owner, and Global Administrator or Privileged Role Administrator in its Microsoft Entra tenant. The owner of a personal subscription has both. [Why](deploy.md#requirements).
- The tools: OpenTofu, the Azure CLI, `just`, Conftest, `jq` and `gh`. After cloning (step 2), `just doctor` lists what's missing.
- A region with room for two Container Apps environments, listed in `policy/data/policy.yaml` (edit the list if yours isn't there).

Installing a second Weesite in a subscription or tenant that already has one? Read [Second install](deploy.md#second-install) first.

## 2. Fork and clone

1. Fork the repository into your GitHub account or organisation. It must not host another Weesite, because the image goes to `ghcr.io/<owner>/weesite`.
2. On the fork's **Actions** tab, enable the workflows. A fork's workflows stay off until you do.
3. Clone the fork and apply its repository settings:

   ```sh
   git clone git@github.com:<owner>/<repo>.git
   cd <repo>
   gh auth login                     # if gh isn't signed in yet
   just github-settings --no-required-checks <owner>/<repo>
   ```

**You should see:** `All settings match.` (CodeQL may take a minute; run it again if it's the only difference.)

## 3. Fill in the bootstrap's inputs

```sh
az login
az account set --subscription <subscription ID>
cp infra/bootstrap/bootstrap.auto.tfvars.example infra/bootstrap/bootstrap.auto.tfvars
```

Fill in `infra/bootstrap/bootstrap.auto.tfvars` (it stays on your machine): the subscription, the regions, a `name_suffix`, your fork's owner and name, who approves prod deployments, and the budget. The file explains each input; [Bootstrap inputs](deploy.md#bootstrap-inputs) has more. Leave `prod_domain` out for now.

## 4. Run the bootstrap

```sh
just bootstrap-first-run    # the first run keeps its state locally
just bootstrap-plan         # about 77 resources to add, then the policy check
just bootstrap-apply        # asks for confirmation; takes a few minutes
just bootstrap-migrate      # moves the state into the storage account it created
rm infra/bootstrap/terraform.tfstate infra/bootstrap/terraform.tfstate.backup
```

**You should see:** the policy check passing after the plan, and after the migration a plan with `No changes`.

## 5. Create the environments

On the fork's **Actions** tab, open **infra-apply** and choose **Run workflow** on `main`. It applies test, then waits for you to approve **prod** (the run's page has **Review deployments**).

**You should see:** both jobs green.

## 6. Build and deploy the site

1. On the **Actions** tab, open **app** and choose **Run workflow** on `main`. It builds, tests and publishes the image, then deploys it to test.
2. The first time, **deploy (test)** stops: the new package is private, and Azure pulls it without credentials. Make it public: your profile's or organisation's **Packages** tab, then `weesite`, **Package settings**, **Change visibility**, Public.
3. On the run's page, choose **Re-run failed jobs**.

**You should see:** **deploy (test)** green, and the site answering at the address `just url test` prints.

To try the site with sample posts, start **seed** from the Actions tab (test only).

For visitors' countries in the analytics, start **geoip** from the Actions tab and approve **prod** when it asks; it then runs by itself each month ([Visitor countries](deploy.md#visitor-countries-geoip)).

## 7. Make yourself the admin

Sign-in only lets in the addresses you list.

1. **With a work or school Microsoft account** (not outlook.com or hotmail.com, not Google), trust its tenant first. The app settings take effect with an apply, so run **infra-apply** again afterwards (step 5):

   ```sh
   tenant=$(az account show --query tenantId --output tsv)    # the subscription's tenant
   for env in TEST PROD; do
     gh variable set "APP_SETTINGS_$env" --repo <owner>/<repo> \
       --body "{\"Authentication__Microsoft__TrustedTenants\": \"$tenant\"}"
   done
   ```

   If your account belongs to another tenant than the subscription's, put that tenant's ID in `tenant` instead. This replaces any app settings set before; on a new install there are none. [Why and where](deploy.md#admins-and-editors).
2. Store the admins' addresses and restart each environment, so it reads them:

   ```sh
   just site-admins test     # asks for the addresses, comma-separated
   just restart test
   just site-admins prod
   just restart prod
   ```

   The recipes name the resource group they use before they ask for anything.

**You should see:** at `/admin` on test, **Sign in with Microsoft** lets you in. If it says "Not allowed in", the log says why ([Admins and editors](deploy.md#admins-and-editors)).

## 8. Deploy to prod

On the **Actions** tab, open **promote** and choose **Run workflow** on `main`. Approve **prod** when it asks. It deploys the image test runs.

**You should see:** the site at the address `just url prod` prints, and `/admin` letting you in there too.

## 9. Turn on the required checks

Once your first pull request has run the checks (`lint`, `dco`, `infra-static`, `infra-plan`, `build` and `dependency-review`), make them required:

```sh
just github-settings <owner>/<repo>
```

## Next

Before you tell anyone about the site:

- Edit the privacy policy and terms, under Pages in the admin ([Privacy policy and terms](deploy.md#privacy-policy-and-terms)).
- Your own domain on prod ([Custom domain](deploy.md#custom-domain)).
- Optional: [Google sign-in](deploy.md#google-by-hand-once-per-environment), [invitations by email](deploy.md#invitations-by-email), [app settings](deploy.md#app-settings) such as a search model for other languages.
- Try a restore once ([Backups and restore](deploy.md#backups-and-restore)).

Later, to take a new release: [Keeping a fork up to date](deploy.md#keeping-a-fork-up-to-date).

To remove the install again: [Remove an install](deploy.md#remove-an-install).
