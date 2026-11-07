# GitHub Actions identities: user-assigned managed identities trusted
# through federated credentials, so no client secret exists anywhere.
# They live in the state resource group, out of reach of the apply
# identities' own Contributor scope.
#
# - plan: read-only, for PR plans and the weekly drift check (jobs without
#   an environment, on pull requests and on main).
# - apply-<env>: deploys one environment (jobs with `environment: <env>`).

locals {
  oidc_issuer   = "https://token.actions.githubusercontent.com"
  oidc_audience = ["api://AzureADTokenExchange"]

  plan_subjects = {
    pull-request = "${local.github_subject}:pull_request"
    main         = "${local.github_subject}:ref:refs/heads/main"
  }

  # Data roles an apply identity may grant (to the site's own managed
  # identity) in its resource group; anything else needs the bootstrap.
  delegable_roles = toset([
    "Storage Blob Data Contributor",
    "Storage Blob Data Reader",
    "Storage Queue Data Contributor",
    "Storage Queue Data Message Processor",
    "Storage Queue Data Message Sender",
    "Storage Table Data Contributor",
    "Storage Table Data Reader",
  ])
  delegable_role_ids = join(", ", sort([for r in data.azurerm_role_definition.delegable : r.role_definition_id]))

  # Role Based Access Control Administrator, limited by an ABAC condition:
  # assign and remove only the delegable roles, and assign them only to
  # service principals (managed identities), never to users or groups.
  rbac_admin_condition = <<-EOT
    (
      (
        !(ActionMatches{'Microsoft.Authorization/roleAssignments/write'})
      )
      OR
      (
        @Request[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {${local.delegable_role_ids}}
        AND
        @Request[Microsoft.Authorization/roleAssignments:PrincipalType] ForAnyOfAnyValues:StringEqualsIgnoreCase {'ServicePrincipal'}
      )
    )
    AND
    (
      (
        !(ActionMatches{'Microsoft.Authorization/roleAssignments/delete'})
      )
      OR
      (
        @Resource[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {${local.delegable_role_ids}}
      )
    )
  EOT
}

data "azurerm_role_definition" "delegable" {
  for_each = local.delegable_roles

  name  = each.key
  scope = "/subscriptions/${var.subscription_id}"
}

# Plan identity

resource "azurerm_user_assigned_identity" "plan" {
  name                = "id-${local.workload}-plan"
  resource_group_name = azurerm_resource_group.state.name
  location            = azurerm_resource_group.state.location
  tags                = local.shared_tags
}

resource "azurerm_federated_identity_credential" "plan" {
  for_each = local.plan_subjects

  name                      = "github-${each.key}"
  user_assigned_identity_id = azurerm_user_assigned_identity.plan.id
  issuer                    = local.oidc_issuer
  audience                  = local.oidc_audience
  subject                   = each.value
}

resource "azurerm_role_assignment" "plan_reader" {
  for_each = local.environments

  scope                = azurerm_resource_group.env[each.key].id
  role_definition_name = "Reader"
  principal_id         = azurerm_user_assigned_identity.plan.principal_id
  principal_type       = "ServicePrincipal"
  description          = "Plan: read the ${each.key} resources."
}

# Plans run with -lock=false: reading the state needs no lease.
resource "azurerm_role_assignment" "plan_state" {
  for_each = local.environments

  scope                = azurerm_storage_container.state[each.key].id
  role_definition_name = "Storage Blob Data Reader"
  principal_id         = azurerm_user_assigned_identity.plan.principal_id
  principal_type       = "ServicePrincipal"
  description          = "Plan: read the ${each.key} state."
}

resource "azurerm_role_assignment" "plan_state_key" {
  for_each = local.environments

  scope                = "${azurerm_key_vault.state.id}/keys/${azurerm_key_vault_key.state[each.key].name}"
  role_definition_name = "Key Vault Crypto User"
  principal_id         = azurerm_user_assigned_identity.plan.principal_id
  principal_type       = "ServicePrincipal"
  description          = "Plan: decrypt the ${each.key} state, encrypt the plan file."
}

# Apply identities, one per environment

resource "azurerm_user_assigned_identity" "apply" {
  for_each = local.environments

  name                = "id-${local.workload}-apply-${each.key}"
  resource_group_name = azurerm_resource_group.state.name
  location            = azurerm_resource_group.state.location
  tags                = merge(local.tags, { environment = each.key })
}

resource "azurerm_federated_identity_credential" "apply" {
  for_each = local.environments

  name                      = "github-environment-${each.key}"
  user_assigned_identity_id = azurerm_user_assigned_identity.apply[each.key].id
  issuer                    = local.oidc_issuer
  audience                  = local.oidc_audience
  subject                   = "${local.github_subject}:environment:${each.key}"
}

resource "azurerm_role_assignment" "apply_contributor" {
  for_each = local.environments

  scope                = azurerm_resource_group.env[each.key].id
  role_definition_name = "Contributor"
  principal_id         = azurerm_user_assigned_identity.apply[each.key].principal_id
  principal_type       = "ServicePrincipal"
  description          = "Apply: manage the ${each.key} resources."
}

resource "azurerm_role_assignment" "apply_rbac_admin" {
  for_each = local.environments

  scope                = azurerm_resource_group.env[each.key].id
  role_definition_name = "Role Based Access Control Administrator"
  principal_id         = azurerm_user_assigned_identity.apply[each.key].principal_id
  principal_type       = "ServicePrincipal"
  condition            = local.rbac_admin_condition
  condition_version    = "2.0"
  description          = "Apply: grant storage data roles to the site's managed identity in ${each.key}."
}

resource "azurerm_role_assignment" "apply_state" {
  for_each = local.environments

  scope                = azurerm_storage_container.state[each.key].id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_user_assigned_identity.apply[each.key].principal_id
  principal_type       = "ServicePrincipal"
  description          = "Apply: read, lock and write the ${each.key} state."
}

resource "azurerm_role_assignment" "apply_state_key" {
  for_each = local.environments

  scope                = "${azurerm_key_vault.state.id}/keys/${azurerm_key_vault_key.state[each.key].name}"
  role_definition_name = "Key Vault Crypto User"
  principal_id         = azurerm_user_assigned_identity.apply[each.key].principal_id
  principal_type       = "ServicePrincipal"
  description          = "Apply: encrypt and decrypt the ${each.key} state and plan files."
}
