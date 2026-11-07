# The site's own managed identity: it reads and writes its storage and reads its secrets. The
# apply identity may grant only these data roles, and only to managed identities (bootstrap).

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
