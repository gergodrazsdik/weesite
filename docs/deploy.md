# Deploy your own Weesite

> **Outline only.** This guide is filled in together with the bootstrap code (`infra/bootstrap`) and the workflows. Until then, nothing here is runnable.

Weesite deploys to your own Azure subscription from your own fork. Nothing in the repo names a deployment: your domain, hostnames and IDs live in your fork's GitHub environment variables and secrets.

## Prerequisites

An Azure subscription where you can assign roles, a GitHub account, a domain you control (optional), and the infrastructure tools from [dev-setup.md](dev-setup.md#infrastructure-from-phase-1).

## Fork the repository

Fork (or copy) the repo, then apply the repository settings and rulesets to it with `just github-settings <owner>/<repo>`.

## Bootstrap inputs: `bootstrap.auto.tfvars`

Copy `infra/bootstrap/bootstrap.auto.tfvars.example` to `bootstrap.auto.tfvars` (gitignored) and fill in your subscription, region, repository and budget.

## Run the bootstrap: `just bootstrap-plan`, `just bootstrap-apply`

Sign in with the Azure CLI and export a GitHub token, then plan and apply. The bootstrap creates the state storage, encryption keys, environment resource groups, identities, GitHub environments and a budget alert.

## Move the bootstrap state: `just bootstrap-migrate`

Moves the bootstrap's own local state into the storage account it created, encrypted with its own key.

## GitHub environment variables and secrets

What the bootstrap sets on the `test` and `prod` environments, and what you add yourself (domain and hostnames, sign-in client IDs).

## First deploy

Merge to `main`: the workflows plan and apply the app infrastructure, build and scan the image, and deploy it to test; promote to prod after your approval.

## Custom domain

DNS records, managed certificates and the redirect from `www` to the bare domain.
