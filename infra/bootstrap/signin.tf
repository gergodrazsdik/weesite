# Sign-in (ADR 0004): what infra/app needs to manage each environment's Entra app registration
# and the sign-in secrets in its Key Vault.
#
# - Apply identities: Microsoft Graph Application.ReadWrite.OwnedBy (create and manage only the
#   apps they own) and Key Vault Secrets Officer (write the Microsoft client secret).
# - Plan identities: Application.Read.All, to refresh the app registration. They read only the
#   secrets infra/app manages (the Microsoft client and the Application Insights connection
#   string), each at its own scope: infra/app grants that (keyvault.tf), as the secrets exist only
#   once it has run. Not the secrets stored by hand (the Google client, SMTP, the admin list:
#   T-0177). They can already decrypt the state, which holds the same values.
# - The bootstrap runner: Key Vault Secrets Officer, to store the Google client created by hand
#   (`just google-signin <env>`).
#
# Granting Graph application permissions is admin consent: the runner needs Global
# Administrator or Privileged Role Administrator in the tenant.

data "azuread_application_published_app_ids" "well_known" {}

data "azuread_service_principal" "graph" {
  client_id = data.azuread_application_published_app_ids.well_known.result["MicrosoftGraph"]
}

resource "azuread_app_role_assignment" "apply_graph" {
  for_each = local.environments

  app_role_id         = data.azuread_service_principal.graph.app_role_ids["Application.ReadWrite.OwnedBy"]
  principal_object_id = azurerm_user_assigned_identity.apply[each.key].principal_id
  resource_object_id  = data.azuread_service_principal.graph.object_id
}

resource "azuread_app_role_assignment" "plan_graph" {
  app_role_id         = data.azuread_service_principal.graph.app_role_ids["Application.Read.All"]
  principal_object_id = azurerm_user_assigned_identity.plan.principal_id
  resource_object_id  = data.azuread_service_principal.graph.object_id
}

resource "azuread_app_role_assignment" "plan_main_graph" {
  app_role_id         = data.azuread_service_principal.graph.app_role_ids["Application.Read.All"]
  principal_object_id = azurerm_user_assigned_identity.plan_main.principal_id
  resource_object_id  = data.azuread_service_principal.graph.object_id
}

resource "azurerm_role_assignment" "apply_secrets" {
  for_each = local.environments

  scope                = azurerm_resource_group.env[each.key].id
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = azurerm_user_assigned_identity.apply[each.key].principal_id
  principal_type       = "ServicePrincipal"
  description          = "Apply: write the sign-in secrets in the ${each.key} Key Vault."
}

# Only while an install from before 1.1 moves to the secret-scope roles above: set
# plan_secrets_group_scope for the first bootstrap-apply, run infra-apply, then apply again
# without it (docs/deploy.md).
# policy:waive keyvault-secrets-user-narrow Transitional, while infra/app hasn't yet granted the secret-scope roles (plan_secrets_group_scope).
resource "azurerm_role_assignment" "plan_secrets" {
  for_each = var.plan_secrets_group_scope ? local.environments : toset([])

  scope                = azurerm_resource_group.env[each.key].id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = local.plan_principal[each.key]
  principal_type       = "ServicePrincipal"
  description          = "Plan: refresh the sign-in secrets in the ${each.key} Key Vault (until infra/app grants them one by one)."
}

resource "azurerm_role_assignment" "runner_secrets" {
  for_each = local.environments

  scope                = azurerm_resource_group.env[each.key].id
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = data.azurerm_client_config.current.object_id
  description          = "Bootstrap runner: store the Google sign-in client in the ${each.key} Key Vault."
}
