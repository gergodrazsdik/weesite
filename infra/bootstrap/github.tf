# GitHub environments, secrets and variables. Created here, never by hand.

data "github_repository" "this" {
  full_name = "${var.github_owner}/${var.github_repository}"
}

data "github_user" "owner" {
  username = var.github_owner
}

data "github_user" "prod_reviewer" {
  for_each = toset(var.prod_reviewers)

  username = each.key
}

# test and prod deploy only from main. prod waits for a reviewer;
# self-review is allowed while there is one maintainer.
resource "github_repository_environment" "env" {
  for_each = local.environments

  repository          = data.github_repository.this.name
  environment         = each.key
  can_admins_bypass   = each.key != "prod"
  prevent_self_review = false

  dynamic "reviewers" {
    for_each = each.key == "prod" ? [1] : []

    content {
      users = [for u in data.github_user.prod_reviewer : tonumber(u.id)]
    }
  }

  deployment_branch_policy {
    protected_branches     = false
    custom_branch_policies = true
  }
}

resource "github_repository_environment_deployment_policy" "main" {
  for_each = local.environments

  repository     = data.github_repository.this.name
  environment    = github_repository_environment.env[each.key].environment
  branch_pattern = "main"
}

# Azure IDs are secrets, so public workflow logs mask them.
locals {
  environment_secrets = merge([
    for env in local.environments : {
      for name, value in {
        AZURE_CLIENT_ID       = azurerm_user_assigned_identity.apply[env].client_id
        AZURE_TENANT_ID       = data.azurerm_client_config.current.tenant_id
        AZURE_SUBSCRIPTION_ID = var.subscription_id
      } : "${env}/${name}" => { environment = env, name = name, value = value }
    }
  ]...)

  # Plan jobs run without an environment, so the plan identity's IDs are
  # repository secrets. Fork PRs get no secrets, and so no plan.
  repository_secrets = {
    AZURE_PLAN_CLIENT_ID  = azurerm_user_assigned_identity.plan.client_id
    AZURE_TENANT_ID       = data.azurerm_client_config.current.tenant_id
    AZURE_SUBSCRIPTION_ID = var.subscription_id
  }

  # Where the infra/app states live: a container and a key per environment,
  # both named after it.
  repository_variables = merge(
    {
      TF_STATE_RESOURCE_GROUP  = azurerm_resource_group.state.name
      TF_STATE_STORAGE_ACCOUNT = azurerm_storage_account.state.name
      TF_STATE_KEY_VAULT_URI   = local.state_key_vault_uri
    },
    # A repository variable, not an environment one: plan jobs run without
    # an environment. The domain is public anyway.
    var.prod_domain == "" ? {} : { PROD_DOMAIN = var.prod_domain },
  )

  environment_variables = merge([
    for env, vars in var.environment_variables : {
      for name, value in vars : "${env}/${name}" => { environment = env, name = name, value = value }
    }
  ]...)
}

resource "github_actions_environment_secret" "env" {
  for_each = local.environment_secrets

  repository  = data.github_repository.this.name
  environment = github_repository_environment.env[each.value.environment].environment
  secret_name = each.value.name
  value       = each.value.value
}

resource "github_actions_secret" "repository" {
  for_each = local.repository_secrets

  repository  = data.github_repository.this.name
  secret_name = each.key
  value       = each.value
}

resource "github_actions_variable" "repository" {
  for_each = local.repository_variables

  repository    = data.github_repository.this.name
  variable_name = each.key
  value         = each.value
}

resource "github_actions_environment_variable" "env" {
  for_each = local.environment_variables

  repository    = data.github_repository.this.name
  environment   = github_repository_environment.env[each.value.environment].environment
  variable_name = each.value.name
  value         = each.value.value
}
