# Weesite task runner. `just` lists the recipes.
# On Windows, run recipes from Git Bash or WSL (they use sh).

import? 'local.just'

# Shell for the maintainer recipes, with $env set: the environment's resource group, rg-weesite-<env>
# or rg-weesite-<instance>-<env> for a second install in the same subscription (the bootstrap's
# `instance`). The instance is WEESITE_INSTANCE when it's set, even empty; otherwise the repository
# variable the bootstrap set on this clone's repository, so a fork's checkout doesn't reach another
# install's Key Vault. The recipe says which group it uses before it asks for anything.
_environment_group := 'if [ -n "${WEESITE_INSTANCE+set}" ]; then instance=$WEESITE_INSTANCE; elif vars=$(gh variable list --json name,value 2>/dev/null); then instance=$(jq -r ".[] | select(.name == \"WEESITE_INSTANCE\") | .value" <<<"$vars"); else instance=; echo "warning: gh could not read the repository variables; assuming no instance. Set WEESITE_INSTANCE to choose." >&2; fi; group="rg-weesite${instance:+-$instance}-$env"; echo "Environment $env: resource group $group"'

# List the recipes
default:
    @just --list --unsorted

# Check that the required tools are installed and print install hints
[group('meta')]
doctor:
    #!/bin/sh
    missing=0
    check() {
      if command -v "$1" >/dev/null 2>&1; then
        printf '  ok       %s\n' "$1"
      else
        printf '  MISSING  %-22s %s\n' "$1" "$2"
        missing=1
      fi
    }
    optional() {
      if command -v "$1" >/dev/null 2>&1; then
        printf '  ok       %s\n' "$1"
      else
        printf '  -        %-22s %s\n' "$1" "$2"
      fi
    }
    echo "Required:"
    check git                  "https://git-scm.com/downloads"
    check just                 "https://just.systems/man/en/packages.html"
    check lefthook             "https://lefthook.dev/installation/"
    check markdownlint-cli2    "npm install -g markdownlint-cli2"
    check yamllint             "pipx install yamllint (or uv tool install yamllint)"
    check actionlint           "https://github.com/rhysd/actionlint/blob/main/docs/install.md"
    check zizmor               "pipx install zizmor (or uv tool install zizmor), https://docs.zizmor.sh/installation/"
    check typos                "cargo install typos-cli, or https://github.com/crate-ci/typos#install"
    check editorconfig-checker "https://github.com/editorconfig-checker/editorconfig-checker#installation"
    check shellcheck           "https://github.com/koalaman/shellcheck#installing"
    check node                 "https://nodejs.org/en/download (checks the scripts' syntax; also runs markdownlint-cli2)"
    check esbuild              "npm install -g esbuild, or scripts/install-esbuild.sh ~/.local/bin (Linux x64, the image's version)"
    check gitleaks             "https://github.com/gitleaks/gitleaks#installing"
    check conftest             "https://www.conftest.dev/install/"
    check pinact               "https://github.com/suzuki-shunsuke/pinact#install"
    check hadolint             "https://github.com/hadolint/hadolint#install"
    echo "Recommended:"
    optional gh                "https://cli.github.com/"
    optional jq                "https://jqlang.org/download/ (needed by 'just github-settings')"
    optional claude            "https://code.claude.com/docs/en/setup"
    echo "Application:"
    optional dotnet            ".NET 11 SDK: https://dotnet.microsoft.com/download"
    optional aspire            "https://aspire.dev/get-started/install-cli/"
    optional podman            "Podman or Docker for Azurite: https://podman.io/docs/installation"
    optional grype             "https://github.com/anchore/grype#installation"
    optional syft              "https://github.com/anchore/syft#installation"
    echo "Infrastructure (also for just lint):"
    optional tofu              "https://opentofu.org/docs/intro/install/"
    optional tflint            "https://github.com/terraform-linters/tflint#installation"
    optional checkov           "pipx install checkov (or uv tool install checkov)"
    echo "Git settings:"
    if [ "$(git config --bool commit.gpgSign || echo false)" = true ]; then
      echo "  ok       commit.gpgSign"
    else
      echo "  MISSING  commit.gpgSign         git config commit.gpgSign true (see CONTRIBUTING.md)"
      missing=1
    fi
    if [ "$(git config --bool tag.gpgSign || echo false)" = true ]; then
      echo "  ok       tag.gpgSign"
    else
      echo "  -        tag.gpgSign            git config tag.gpgSign true (maintainers)"
    fi
    if [ -f "$(git rev-parse --git-path hooks)/commit-msg" ]; then
      echo "  ok       git hooks installed"
    else
      echo "  MISSING  git hooks              just hooks"
      missing=1
    fi
    exit "$missing"

# Install the git hooks (lefthook)
[group('dev')]
hooks:
    lefthook install

# Run all linters and checks (lint-core and infra)
[group('lint')]
lint: lint-core infra

# Linters for the whole repository (the lint workflow)
[group('lint')]
lint-core: lint-md lint-yaml lint-actions zizmor pinact-check lint-sh lint-js minify typos editorconfig gitleaks lint-containerfile

# Static infrastructure checks: formatting, validation, TFLint, Checkov and the policy (the infra-static workflow)
[group('infra')]
infra: lint-tofu validate tflint checkov lint-rego policy-test policy-check

# Lint Markdown (markdownlint-cli2)
[group('lint')]
lint-md:
    git ls-files -z '*.md' | xargs -0 scripts/with-tool.sh markdownlint-cli2

# Lint YAML (yamllint)
[group('lint')]
lint-yaml:
    scripts/with-tool.sh yamllint --strict .

# Lint GitHub Actions workflows (actionlint)
[group('lint')]
lint-actions:
    scripts/with-tool.sh actionlint

# Audit GitHub Actions workflows for security problems (zizmor, offline audits)
[group('lint')]
zizmor:
    scripts/with-tool.sh zizmor --offline --no-progress .github/workflows .github/actions

# Check that every action is pinned to a full commit SHA (pinact)
[group('lint')]
pinact-check:
    scripts/with-tool.sh pinact run --check

# Lint shell scripts (shellcheck)
[group('lint')]
lint-sh:
    git ls-files -z '*.sh' | xargs -0 scripts/with-tool.sh shellcheck

# Check the browser scripts' syntax as ES modules (node --check)
[group('lint')]
lint-js:
    git ls-files -z '*.js' '*.mjs' | xargs -0 scripts/check-js.sh

# Minify the browser scripts and stylesheets into a temporary directory; fails on errors (esbuild)
[group('lint')]
minify:
    scripts/minify.sh

# Check spelling (typos)
[group('lint')]
typos:
    scripts/with-tool.sh typos

# Check files against .editorconfig (editorconfig-checker)
[group('lint')]
editorconfig:
    git ls-files -z | xargs -0 scripts/with-tool.sh editorconfig-checker

# Scan the git history for secrets (gitleaks)
[group('lint')]
gitleaks:
    scripts/with-tool.sh gitleaks git --redact --no-banner

# Check OpenTofu formatting (only when there are .tf files)
[group('infra')]
lint-tofu:
    #!/bin/sh
    if git ls-files '*.tf' '*.tofu' | grep -q .; then
      exec scripts/with-tool.sh tofu fmt -check -recursive infra
    fi
    echo "lint-tofu: no OpenTofu files yet"

# Validate each OpenTofu root (init without a backend, then validate)
[group('infra')]
validate:
    #!/bin/sh
    set -eu
    found=0
    for root in infra/*/; do
      ls "$root"*.tf >/dev/null 2>&1 || continue
      found=1
      echo "validate: $root"
      scripts/with-tool.sh tofu -chdir="$root" init -backend=false -input=false -lockfile=readonly >/dev/null
      scripts/with-tool.sh tofu -chdir="$root" validate -no-color
    done
    [ "$found" = 1 ] || echo "validate: no OpenTofu roots yet"

# Lint the OpenTofu code (TFLint with the azurerm ruleset)
[group('infra')]
tflint:
    #!/bin/sh
    set -eu
    if ! git ls-files '*.tf' | grep -q .; then
      echo "tflint: no OpenTofu files yet"
      exit 0
    fi
    scripts/with-tool.sh tflint --init --config "$PWD/.tflint.hcl"
    scripts/with-tool.sh tflint --recursive --config "$PWD/.tflint.hcl"

# Scan the OpenTofu code for misconfigurations (Checkov; settings in .checkov.yaml)
[group('infra')]
checkov:
    #!/bin/sh
    set -eu
    if ! git ls-files '*.tf' | grep -q .; then
      echo "checkov: no OpenTofu files yet"
      exit 0
    fi
    exec scripts/with-tool.sh checkov --config-file .checkov.yaml

# Lint Containerfiles and Dockerfiles (only when there are any)
[group('lint')]
lint-containerfile:
    #!/bin/sh
    files=$(git ls-files '*Containerfile*' '*Dockerfile*')
    if [ -n "$files" ]; then
      # shellcheck disable=SC2086  # one path per word; tracked paths have no spaces
      exec scripts/with-tool.sh hadolint $files
    fi
    echo "lint-containerfile: no Containerfile yet"

# Check Rego formatting (conftest fmt)
[group('infra')]
lint-rego:
    scripts/with-tool.sh conftest fmt --check policy

# Run the policy tests (conftest verify)
[group('infra')]
policy-test:
    scripts/with-tool.sh conftest verify --policy policy --data policy/data --strict

# Check the OpenTofu code against the policy, one root at a time (conftest on the HCL, with the inline waivers)
[group('infra')]
policy-check:
    #!/bin/sh
    set -eu
    if ! git ls-files '*.tf' '*.tofu' | grep -q .; then
      echo "policy-check: no OpenTofu files yet"
      exit 0
    fi
    tmp=$(mktemp -d)
    trap 'rm -rf "$tmp"' EXIT
    status=0
    for root in infra/*/; do
      files=$(find "$root" -type d -name .terraform -prune -o -type f \( -name '*.tf' -o -name '*.tofu' \) -print)
      [ -n "$files" ] || continue
      scripts/policy-waivers.sh "$root" > "$tmp/waivers.json"
      # shellcheck disable=SC2086  # one path per word; tracked paths have no spaces
      scripts/with-tool.sh conftest test --parser hcl2 --strict --policy policy --data policy/data --data "$tmp/waivers.json" $files || status=1
    done
    exit "$status"

# Build the .NET solution (warnings are errors; -m:1 keeps it working in the Claude sandbox)
[group('dotnet')]
build:
    dotnet build Weesite.slnx -m:1

# Download the embedding model into models/ (pinned and hashed; needed by search and its tests)
[group('dotnet')]
model:
    scripts/fetch-model.sh

# Run the tests (TUnit on Microsoft.Testing.Platform): the browser tests first, alone, as CI does
# (containers started by the other projects disturb Chromium, F-0043)
[group('dotnet')]
test:
    dotnet test --project tests/Weesite.Web.EndToEnd
    dotnet test --solution Weesite.Tests.slnf

# Run the site locally with Azurite and the seed content (Aspire; needs Podman or Docker; outside the Claude sandbox)
[group('dotnet')]
aspire:
    aspire run --project src/Weesite.AppHost

# Write screenshots of the key pages (390 and 1280 px, light and dark) to artifacts/screens/
[group('dotnet')]
screens:
    dotnet test --project tests/Weesite.Web.EndToEnd -- --treenode-filter "/*/*/Screens/*"
    @echo "Screenshots: artifacts/screens/"

# Install the Chromium build the end-to-end tests' Playwright version needs (needs pwsh)
[group('dotnet')]
browsers:
    dotnet build tests/Weesite.Web.EndToEnd -m:1
    pwsh artifacts/bin/Weesite.Web.EndToEnd/debug/playwright.ps1 install chromium

# Format the C# code and fix code style (dotnet format, rules in .editorconfig)
[group('dotnet')]
format:
    dotnet format Weesite.slnx

# Check C# formatting and code style without changing anything (the app workflow runs it)
[group('dotnet')]
format-check:
    dotnet format Weesite.slnx --verify-no-changes

# Store an environment's Google sign-in client in its Key Vault (maintainers; needs `az login`; docs/deploy.md)
[group('bootstrap')]
google-signin env:
    #!/usr/bin/env bash
    set -euo pipefail
    case "{{ env }}" in test|prod) ;; *) echo "usage: just google-signin <test|prod>" >&2; exit 2 ;; esac
    env="{{ env }}"
    {{ _environment_group }}
    vault=$(az keyvault list --resource-group "$group" --query "[0].name" --output tsv)
    if [ -z "$vault" ]; then echo "no Key Vault in $group: run infra-apply first" >&2; exit 1; fi
    # The redirect URIs the Google client must list exactly (only printed here, never in CI logs).
    host=$(az containerapp show --resource-group "$group" --name ca-weesite-web --query properties.configuration.ingress.fqdn --output tsv)
    echo "Authorized redirect URIs for the {{ env }} Google client:"
    echo "  https://$host/signin-google"
    if [ "{{ env }}" = test ]; then
      echo "  http://localhost:5080/signin-google"
    elif domain=$(gh variable get PROD_DOMAIN 2>/dev/null) && [ -n "$domain" ]; then
      echo "  https://$domain/signin-google"
    fi
    echo
    read -r -p "Google client ID for {{ env }}: " client_id
    read -r -s -p "Google client secret for {{ env }}: " client_secret
    echo
    # Through files, so the secret never appears on a command line.
    tmp=$(mktemp -d)
    trap 'rm -rf "$tmp"' EXIT
    printf '%s' "$client_id" > "$tmp/id"
    printf '%s' "$client_secret" > "$tmp/secret"
    az keyvault secret set --vault-name "$vault" --name Authentication--Google--ClientId --file "$tmp/id" --encoding utf-8 --content-type text/plain --output none
    az keyvault secret set --vault-name "$vault" --name Authentication--Google--ClientSecret --file "$tmp/secret" --encoding utf-8 --content-type text/plain --output none
    echo "Stored in $vault. Restart the app to load them: just restart {{ env }}"

# Store an environment's SMTP settings for the editor invitation in its Key Vault (maintainers; needs `az login`; ADR 0010, docs/deploy.md)
[group('bootstrap')]
smtp env:
    #!/usr/bin/env bash
    set -euo pipefail
    case "{{ env }}" in test|prod) ;; *) echo "usage: just smtp <test|prod>" >&2; exit 2 ;; esac
    env="{{ env }}"
    {{ _environment_group }}
    vault=$(az keyvault list --resource-group "$group" --query "[0].name" --output tsv)
    if [ -z "$vault" ]; then echo "no Key Vault in $group: run infra-apply first" >&2; exit 1; fi
    read -r -p "SMTP server for {{ env }} (such as smtp.example.com): " host
    read -r -p "Port [587]: " port
    read -r -p "Security, StartTls or SslOnConnect [StartTls]: " security
    read -r -p "User name: " username
    read -r -s -p "Password: " password
    echo
    read -r -p "Sender address (such as noreply@example.com): " from
    if [ -z "$host" ] || [ -z "$from" ]; then echo "the server and the sender address are needed" >&2; exit 2; fi
    # Through files, so the password never appears on a command line.
    tmp=$(mktemp -d)
    trap 'rm -rf "$tmp"' EXIT
    store() {
      if [ -z "$2" ]; then echo "$1 left as it is (empty)"; return 0; fi
      printf '%s' "$2" > "$tmp/value"
      az keyvault secret set --vault-name "$vault" --name "$1" --file "$tmp/value" --encoding utf-8 --content-type text/plain --output none
    }
    store Email--Smtp--Host "$host"
    store Email--Smtp--Port "${port:-587}"
    store Email--Smtp--Security "${security:-StartTls}"
    store Email--Smtp--UserName "$username"
    store Email--Smtp--Password "$password"
    store Email--From "$from"
    echo "Stored in $vault. Restart the app to load them: just restart {{ env }}"

# Set an environment's admins, the email addresses that may always sign in, in its Key Vault (maintainers; needs `az login`; docs/deploy.md)
[group('bootstrap')]
site-admins env:
    #!/usr/bin/env bash
    set -euo pipefail
    case "{{ env }}" in test|prod) ;; *) echo "usage: just site-admins <test|prod>" >&2; exit 2 ;; esac
    env="{{ env }}"
    {{ _environment_group }}
    vault=$(az keyvault list --resource-group "$group" --query "[0].name" --output tsv)
    if [ -z "$vault" ]; then echo "no Key Vault in $group: run infra-apply first" >&2; exit 1; fi
    read -r -p "Admin email addresses for {{ env }} (comma-separated): " admins
    tmp=$(mktemp -d)
    trap 'rm -rf "$tmp"' EXIT
    printf '%s' "$admins" > "$tmp/admins"
    az keyvault secret set --vault-name "$vault" --name Site--Admins --file "$tmp/admins" --encoding utf-8 --content-type text/plain --output none
    echo "Stored in $vault. Restart the app to load them: just restart {{ env }}"

# Restart an environment's app, so it reads its Key Vault again (maintainers; needs `az login`)
[group('bootstrap')]
restart env:
    #!/usr/bin/env bash
    set -euo pipefail
    case "{{ env }}" in test|prod) ;; *) echo "usage: just restart <test|prod>" >&2; exit 2 ;; esac
    env="{{ env }}"
    {{ _environment_group }}
    revision=$(az containerapp show --resource-group "$group" --name ca-weesite-web --query properties.latestRevisionName --only-show-errors --output tsv)
    az containerapp revision restart --resource-group "$group" --name ca-weesite-web --revision "$revision" --only-show-errors --output none
    echo "Restarted $revision; it answers again within a minute."

# Print an environment's address: the Container Apps hostname (maintainers; needs `az login`)
[group('bootstrap')]
url env:
    #!/usr/bin/env bash
    set -euo pipefail
    case "{{ env }}" in test|prod) ;; *) echo "usage: just url <test|prod>" >&2; exit 2 ;; esac
    env="{{ env }}"
    {{ _environment_group }}
    echo "https://$(az containerapp show --resource-group "$group" --name ca-weesite-web --query properties.configuration.ingress.fqdn --only-show-errors --output tsv)"

# Shell for the bootstrap recipes, run in infra/bootstrap: does the state Key Vault exist (its name
# resolves)? Without getent, a name_suffix or working DNS it can't tell, and answers yes.
_state_vault_check := 'suffix=$(sed -n "s/^[[:space:]]*name_suffix[[:space:]]*=[[:space:]]*\"\([a-z0-9]*\)\".*/\1/p" bootstrap.auto.tfvars 2>/dev/null || true); state_exists() { [ -z "$suffix" ] || ! command -v getent >/dev/null || ! getent hosts login.microsoftonline.com >/dev/null || getent hosts "kv-weesite-state-$suffix.vault.azure.net" >/dev/null; }'

# First bootstrap run only: local, unencrypted state until `just bootstrap-migrate` (maintainers; docs/deploy.md)
[group('bootstrap')]
bootstrap-first-run:
    #!/bin/sh
    set -eu
    cd infra/bootstrap
    if [ -f first_run_override.tf ]; then
      echo "bootstrap: the first run is already set up"
      exit 0
    fi
    if grep -qs '"azurerm"' .terraform/terraform.tfstate; then
      {{ _state_vault_check }}
      if state_exists; then
        echo "error: this checkout already uses the remote state, so the first run is over" >&2
        exit 1
      fi
      # A plan tried the remote state before it existed: forget that backend (not state).
      rm .terraform/terraform.tfstate
    fi
    cat > first_run_override.tf <<'EOF'
    # Written by `just bootstrap-first-run`; removed by `just bootstrap-migrate`.
    # The first run can't use the storage account and Key Vault it creates.
    terraform {
      backend "local" {}
      encryption {
        method "unencrypted" "first_run" {}
        state {
          method = method.unencrypted.first_run
        }
        plan {
          method = method.unencrypted.first_run
        }
      }
    }
    EOF
    echo "bootstrap: first run, with local unencrypted state. Next: just bootstrap-apply, then just bootstrap-migrate."

# Plan the bootstrap (maintainers; needs `az login` and a GitHub token)
[group('bootstrap')]
bootstrap-plan: (_bootstrap "plan")

# Apply the bootstrap after showing the plan (maintainers only, never agents)
[group('bootstrap')]
bootstrap-apply: (_bootstrap "apply")

# Move the bootstrap's state from the first run into the storage account, encrypted (maintainers only, never agents)
[group('bootstrap')]
bootstrap-migrate:
    #!/bin/sh
    set -eu
    cd infra/bootstrap
    if [ ! -f first_run_override.tf ] || [ ! -s terraform.tfstate ]; then
      echo "error: nothing to migrate; run just bootstrap-first-run and just bootstrap-apply first" >&2
      exit 1
    fi
    if [ -z "${GITHUB_TOKEN:-}" ]; then GITHUB_TOKEN=$(gh auth token); export GITHUB_TOKEN; fi
    tmp=$(mktemp -d)
    restore() {
      rm -f migrate_override.tf
      if [ -f "$tmp/first_run_override.tf" ]; then mv "$tmp/first_run_override.tf" .; fi
      rm -rf "$tmp"
    }
    trap restore EXIT
    mv first_run_override.tf "$tmp/"
    # Read the unencrypted local state once; everything written is encrypted.
    cat > migrate_override.tf <<'EOF'
    terraform {
      encryption {
        method "unencrypted" "migrate" {}
        state {
          method = method.aes_gcm.state
          fallback {
            method = method.unencrypted.migrate
          }
        }
      }
    }
    EOF
    tofu init -lockfile=readonly -migrate-state
    rm -f migrate_override.tf "$tmp/first_run_override.tf"
    tofu plan -input=false
    echo "bootstrap: state migrated. If the plan above shows no changes, delete the local plaintext copies:"
    echo "  rm infra/bootstrap/terraform.tfstate infra/bootstrap/terraform.tfstate.backup"

[private]
_bootstrap command:
    #!/bin/sh
    set -eu
    cd infra/bootstrap
    if [ ! -f bootstrap.auto.tfvars ]; then
      echo "error: copy infra/bootstrap/bootstrap.auto.tfvars.example to bootstrap.auto.tfvars and fill it in" >&2
      exit 1
    fi
    if [ -f first_run_override.tf ]; then
      echo "bootstrap: first run (local, unencrypted state)"
    else
      {{ _state_vault_check }}
      if ! state_exists; then
        echo "error: the state Key Vault kv-weesite-state-$suffix isn't there. On a new install, run just bootstrap-first-run first (docs/install.md, step 4)." >&2
        exit 1
      fi
      echo "bootstrap: remote state (encrypted)"
    fi
    if [ -z "${GITHUB_TOKEN:-}" ]; then GITHUB_TOKEN=$(gh auth token); export GITHUB_TOKEN; fi
    tofu init -input=false -lockfile=readonly
    if [ "{{ command }}" != plan ]; then
      tofu {{ command }}
      exit 0
    fi
    # The plan, then the policy on it: HCL leaves expressions (scopes, role IDs) unjudged, and only
    # infra/app is planned in CI (T-0122). The plan's JSON holds secrets: it stays in a temporary
    # folder that's deleted on exit.
    tmp=$(mktemp -d)
    trap 'rm -rf "$tmp"' EXIT
    tofu plan -input=false -out "$tmp/plan"
    tofu show -json "$tmp/plan" > "$tmp/plan.json"
    ../../scripts/policy-waivers.sh . > "$tmp/waivers.json"
    conftest test --no-color --policy ../../policy --data ../../policy/data --data "$tmp/waivers.json" "$tmp/plan.json"

# Pin GitHub Actions to full commit SHAs (pinact)
[group('meta')]
pin:
    pinact run

# Which tool versions pinned outside Dependabot's reach are due an update (7 days old, a major 14), and this machine's beside them
[group('meta')]
tool-updates:
    scripts/tool-updates.py --local

# Set the release version in Directory.Build.props and date the changelog's Unreleased section as it (on a branch, then a PR)
[group('meta')]
version new:
    scripts/release.sh version {{ new }}

# Tag the version in Directory.Build.props, push the tag and create the GitHub release (maintainers; from main once prod runs it; --dry-run checks only)
[group('maintainer')]
release *flags:
    scripts/release.sh publish {{ flags }}

# Merge a PR by fast-forwarding main to its head (maintainers; keeps every commit's signature)
[group('maintainer')]
merge pr:
    scripts/ff-merge.sh {{ pr }}

# Merge a stack of PRs (from gh stack) bottom-up by fast-forwarding main, given its top PR (maintainers)
[group('maintainer')]
merge-stack pr *flags:
    scripts/ff-merge.sh --stack {{ flags }} {{ pr }}

# Apply the repository settings and rulesets to <owner/repo> (maintainers; see scripts/github-settings.sh)
[group('maintainer')]
github-settings repo *flags:
    scripts/github-settings.sh {{ flags }} {{ repo }}

# Read the repository settings of <owner/repo> back and report differences
[group('maintainer')]
github-settings-check repo:
    scripts/github-settings.sh --check {{ repo }}

# Start Claude Code in this repo (extra arguments are passed through)
[group('dev')]
claude *args:
    claude {{ args }}
