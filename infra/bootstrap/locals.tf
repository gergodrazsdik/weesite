locals {
  workload = "weesite"

  # The start of every name a second install in the same subscription or tenant would clash on:
  # weesite, or weesite-<instance>.
  prefix       = var.instance == "" ? local.workload : "${local.workload}-${var.instance}"
  environments = toset(["test", "prod"])

  # The environments pull requests plan, with the plan identity. The others are planned only
  # from main, with the plan-main identity, so a branch's workflow can read neither their state
  # nor their secrets (T-0122).
  pr_plan_environments = toset(["test"])

  # The GitHub environment pull request plans run in: each waits for its reviewer's approval, and
  # only its jobs can sign in as the plan identity from a pull request (T-0177).
  plan_environment = "plan"

  # The GitHub environments of the helper workflows (seed, load test, geoip), "<env>-ops" => env:
  # they sign in as that environment's ops identity, not its apply identity (T-0177).
  ops_environments = { for env in local.environments : "${env}-ops" => env }

  # The environments' region; the state, Key Vault and identities stay in var.location.
  environment_location = var.environment_location != "" ? var.environment_location : var.location

  # Names follow the Cloud Adoption Framework's abbreviations. The state
  # names are also used by the backend and the encryption key provider
  # (backend.tf), so they may only depend on variables.
  state_resource_group_name  = "rg-${local.prefix}-tfstate"
  state_storage_account_name = "st${local.workload}state${var.name_suffix}"
  state_key_vault_name       = "kv-${local.workload}-state-${var.name_suffix}"
  state_key_vault_uri        = "https://${local.state_key_vault_name}.vault.azure.net"

  # One state container and one encryption key per state: the bootstrap's
  # own and one per environment.
  states = setunion(["bootstrap"], local.environments)

  tags = {
    workload   = local.workload
    managed-by = "opentofu"
  }
  shared_tags = merge(local.tags, { environment = "shared" })

  # GitHub's immutable OIDC subject: owner and repository with their IDs,
  # so a renamed or re-created repository can't reuse the trust.
  github_subject = format(
    "repo:%s@%s/%s@%s",
    var.github_owner, data.github_user.owner.id,
    var.github_repository, data.github_repository.this.repo_id,
  )
}

data "azurerm_client_config" "current" {}
