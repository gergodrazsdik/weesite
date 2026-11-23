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

resource "azurerm_role_assignment" "web_blob_versions" {
  for_each = toset(["content", "settings"])

  scope              = azurerm_storage_container.data[each.key].id
  role_definition_id = "/subscriptions/${local.subscription_id}/providers/Microsoft.Authorization/roleDefinitions/${local.web_blob_versions_role_id}"
  principal_id       = azurerm_user_assigned_identity.web.principal_id
  principal_type     = "ServicePrincipal"
  description        = "Web app: delete earlier versions in ${each.key}."
}
