locals {
  workload     = "weesite"
  environments = toset(["test", "prod"])

  # Names follow the Cloud Adoption Framework's abbreviations. The state
  # names are also used by the backend and the encryption key provider
  # (backend.tf), so they may only depend on variables.
  state_resource_group_name  = "rg-${local.workload}-tfstate"
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
