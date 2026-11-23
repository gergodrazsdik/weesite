# GitHub Actions identities: user-assigned managed identities trusted
# through federated credentials, so no client secret exists anywhere.
# They live in the state resource group, out of reach of the apply
# identities' own Contributor scope.
#
# - plan: read-only, for the environments pull requests plan (test), from
#   pull requests and from main (jobs without an environment).
# - plan-main: read-only, for the other environments (prod), from main only:
#   the plan before an apply and the weekly drift check (T-0122).
# - apply-<env>: deploys one environment (jobs with `environment: <env>`).

locals {
  # Which plan identity reads each environment (T-0122).
  plan_principal = {
    for env in local.environments : env => contains(local.pr_plan_environments, env)
    ? azurerm_user_assigned_identity.plan.principal_id
    : azurerm_user_assigned_identity.plan_main.principal_id
  }

  oidc_issuer   = "https://token.actions.githubusercontent.com"
  oidc_audience = ["api://AzureADTokenExchange"]

  plan_subjects = {
    pull-request = "${local.github_subject}:pull_request"
    main         = "${local.github_subject}:ref:refs/heads/main"
  }

  # Data roles an apply identity may grant (to the site's own managed
  # identity) in its resource group; anything else needs the bootstrap.
  # Key Vault Secrets User: the app reads its secrets as Key Vault
  # references (Q-0005). Key Vault Crypto User: the app wraps its Data
  # Protection keys with a key in its vault (T-0078). Besides these, the
  # custom role web_blob_versions below.
  delegable_roles = toset([
    "Key Vault Crypto User",
    "Key Vault Secrets User",
    "Storage Blob Data Contributor",
    "Storage Blob Data Reader",
    "Storage Queue Data Contributor",
    "Storage Queue Data Message Processor",
    "Storage Queue Data Message Sender",
    "Storage Table Data Contributor",
    "Storage Table Data Reader",
  ])
  delegable_role_ids = join(", ", sort(concat(
    [for r in data.azurerm_role_definition.delegable : r.role_definition_id],
    [azurerm_role_definition.web_blob_versions.role_definition_id],
  )))

  # The custom role's ID, derived from the subscription and the install's prefix, so that
  # infra/app derives the same one without a lookup (its identity.tf).
  web_blob_versions_role_id = uuidv5("url", "https://weesite.invalid/${lower(var.subscription_id)}/${local.prefix}/web-blob-versions")

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

# The web app deletes earlier versions of its posts, pages and settings files itself, by its own
# retention rules (F-0240): a lifecycle rule can only count a version's age from when it was
# written, not from when it was replaced. Deleting a version needs deleteBlobVersion, which
# Storage Blob Data Contributor lacks and only Storage Blob Data Owner has; this role has that
# action alone. infra/app assigns it on the content and settings containers.
resource "azurerm_role_definition" "web_blob_versions" {
  role_definition_id = local.web_blob_versions_role_id
  name               = "${local.prefix}-web-blob-versions-${var.name_suffix}"
  scope              = "/subscriptions/${var.subscription_id}"
  description        = "Weesite web app: delete earlier versions of its posts, pages and settings files."

  permissions {
    data_actions = [
      "Microsoft.Storage/storageAccounts/blobServices/containers/blobs/deleteBlobVersion/action",
    ]
  }

  assignable_scopes = [for rg in azurerm_resource_group.env : rg.id]
}

# Plan identity

resource "azurerm_user_assigned_identity" "plan" {
  name                = "id-${local.prefix}-plan"
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

resource "azurerm_user_assigned_identity" "plan_main" {
  name                = "id-${local.prefix}-plan-main"
  resource_group_name = azurerm_resource_group.state.name
  location            = azurerm_resource_group.state.location
  tags                = local.shared_tags
}

resource "azurerm_federated_identity_credential" "plan_main" {
  name                      = "github-main"
  user_assigned_identity_id = azurerm_user_assigned_identity.plan_main.id
  issuer                    = local.oidc_issuer
  audience                  = local.oidc_audience
  subject                   = local.plan_subjects.main
}

resource "azurerm_role_assignment" "plan_reader" {
  for_each = local.environments

  scope                = azurerm_resource_group.env[each.key].id
  role_definition_name = "Reader"
  principal_id         = local.plan_principal[each.key]
  principal_type       = "ServicePrincipal"
  description          = "Plan: read the ${each.key} resources."
}

# Plans run with -lock=false: reading the state needs no lease.
resource "azurerm_role_assignment" "plan_state" {
  for_each = local.environments

  scope                = azurerm_storage_container.state[each.key].id
  role_definition_name = "Storage Blob Data Reader"
  principal_id         = local.plan_principal[each.key]
  principal_type       = "ServicePrincipal"
  description          = "Plan: read the ${each.key} state."
}

resource "azurerm_role_assignment" "plan_state_key" {
  for_each = local.environments

  scope                = "${azurerm_key_vault.state.id}/keys/${azurerm_key_vault_key.state[each.key].name}"
  role_definition_name = "Key Vault Crypto User"
  principal_id         = local.plan_principal[each.key]
  principal_type       = "ServicePrincipal"
  description          = "Plan: decrypt the ${each.key} state, encrypt the plan file."
}

# Apply identities, one per environment

resource "azurerm_user_assigned_identity" "apply" {
  for_each = local.environments

  name                = "id-${local.prefix}-apply-${each.key}"
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
  description          = "Apply: grant storage and Key Vault secret and key roles to the site's managed identity in ${each.key}."
}

# infra/app creates the key that wraps the site's Data Protection keys in the
# environment's app vault (T-0078): creating and managing keys is a data-plane
# role that Contributor doesn't include. The plan identity reads it to refresh.
resource "azurerm_role_assignment" "apply_key_officer" {
  for_each = local.environments

  scope                = azurerm_resource_group.env[each.key].id
  role_definition_name = "Key Vault Crypto Officer"
  principal_id         = azurerm_user_assigned_identity.apply[each.key].principal_id
  principal_type       = "ServicePrincipal"
  description          = "Apply: create and manage the keys in the ${each.key} app Key Vault."
}

resource "azurerm_role_assignment" "plan_key_reader" {
  for_each = local.environments

  scope                = azurerm_resource_group.env[each.key].id
  role_definition_name = "Key Vault Reader"
  principal_id         = local.plan_principal[each.key]
  principal_type       = "ServicePrincipal"
  description          = "Plan: read the ${each.key} app Key Vault's key properties, never their use."
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

# Refreshing infra/app needs two actions that Reader doesn't include:
# - azurerm reads a Container App's (and job's) secrets with listSecrets. infra/app keeps only
#   Key Vault references there (policy: no-stored-secrets), so this reveals no secret values.
# - azurerm reads each storage table's stored access policies (Get Table ACL). With Entra ID
#   that needs tables/getAcl/action, which no built-in role short of Contributor has; the
#   apply identities have it through Contributor.
resource "azurerm_role_definition" "plan_container_app_secrets" {
  name        = "${local.prefix}-plan-container-app-secrets-${var.name_suffix}"
  scope       = "/subscriptions/${var.subscription_id}"
  description = "Weesite plan identity: the reads Reader lacks to refresh infra/app (Container Apps secrets, which are Key Vault references; storage table ACLs)."

  permissions {
    actions = [
      "Microsoft.App/containerApps/listSecrets/action",
      "Microsoft.App/jobs/listSecrets/action",
      "Microsoft.Storage/storageAccounts/tableServices/tables/getAcl/action",
    ]
  }

  assignable_scopes = [for rg in azurerm_resource_group.env : rg.id]
}

resource "azurerm_role_assignment" "plan_container_app_secrets" {
  for_each = local.environments

  scope              = azurerm_resource_group.env[each.key].id
  role_definition_id = azurerm_role_definition.plan_container_app_secrets.role_definition_resource_id
  principal_id       = local.plan_principal[each.key]
  principal_type     = "ServicePrincipal"
  description        = "Plan: refresh the ${each.key} Container Apps and storage tables."
}
