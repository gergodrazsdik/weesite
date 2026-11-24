# The site's own managed identity: it reads and writes its storage, reads its secrets and wraps its
# Data Protection keys with its key. The apply identity may grant only the data roles the
# bootstrap lists as delegable (delegable_roles, and its custom web_blob_versions role), and only
# to managed identities.

resource "azurerm_user_assigned_identity" "web" {
  name                = "id-${local.workload}-web"
  resource_group_name = data.azurerm_resource_group.env.name
  location            = local.location
  tags                = local.tags
}

locals {
  web_roles = {
    blob  = { scope = azurerm_storage_account.data.id, role = "Storage Blob Data Contributor" }
    table = { scope = azurerm_storage_account.data.id, role = "Storage Table Data Contributor" }
    queue = { scope = azurerm_storage_account.data.id, role = "Storage Queue Data Contributor" }
    vault = { scope = azurerm_key_vault.app.id, role = "Key Vault Secrets User" }
    key   = { scope = azurerm_key_vault_key.dataprotection.resource_versionless_id, role = "Key Vault Crypto User" }
    # Telemetry to Application Insights, which takes it only through Entra ID (monitoring.tf).
    telemetry = { scope = azurerm_application_insights.web.id, role = "Monitoring Metrics Publisher" }
  }
}

resource "azurerm_role_assignment" "web" {
  for_each = local.web_roles

  scope                = each.value.scope
  role_definition_name = each.value.role
  principal_id         = azurerm_user_assigned_identity.web.principal_id
  principal_type       = "ServicePrincipal"
  description          = "Web app: ${each.value.role}."
}

# The app deletes earlier versions of posts, pages and settings files itself (F-0240), which
# Storage Blob Data Contributor can't: the bootstrap's custom role (deleteBlobVersion only), on
# those two containers only. Its ID is derived as the bootstrap derives it.
locals {
  subscription_id           = split("/", data.azurerm_resource_group.env.id)[2]
  web_blob_versions_role_id = uuidv5("url", "https://weesite.invalid/${lower(local.subscription_id)}/${local.prefix}/web-blob-versions")
}

# An install whose bootstrap predates the role (before 1.1) gets the assignment once the
# bootstrap has been applied again; until then the plan warns, and the app logs that it can't
# delete versions, which stay.
data "azapi_resource_list" "custom_roles" {
  type                   = "Microsoft.Authorization/roleDefinitions@2022-04-01"
  parent_id              = data.azurerm_resource_group.env.id
  query_parameters       = { "$filter" = ["type eq 'CustomRole'"] }
  response_export_values = { ids = "value[].name" }
}

locals {
  web_blob_versions_role_ready = contains([for id in try(tolist(data.azapi_resource_list.custom_roles.output.ids), []) : lower(id)], local.web_blob_versions_role_id)
}

check "web_blob_versions_role" {
  assert {
    condition     = local.web_blob_versions_role_ready
    error_message = "The bootstrap's web-blob-versions role isn't there yet: run just bootstrap-apply, then infra-apply again, so the app can delete the old versions of posts, pages and settings (F-0240)."
  }
}

resource "azurerm_role_assignment" "web_blob_versions" {
  for_each = local.web_blob_versions_role_ready ? toset(["content", "settings"]) : toset([])

  scope              = azurerm_storage_container.data[each.key].id
  role_definition_id = "/subscriptions/${local.subscription_id}/providers/Microsoft.Authorization/roleDefinitions/${local.web_blob_versions_role_id}"
  principal_id       = azurerm_user_assigned_identity.web.principal_id
  principal_type     = "ServicePrincipal"
  description        = "Web app: delete earlier versions in ${each.key}."
}
