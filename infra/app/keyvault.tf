# App secrets (sign-in client secrets and the like), read by the web app as Key Vault references.

resource "azurerm_key_vault" "app" {
  #checkov:skip=CKV_AZURE_189:Public network access is needed without a VNet; RBAC only.
  #checkov:skip=CKV_AZURE_109:No firewall rules; see CKV_AZURE_189.
  #checkov:skip=CKV2_AZURE_32:No VNet or private endpoints; see CKV_AZURE_189.
  name                       = "kv-${local.workload}-${var.environment}-${local.suffix}"
  resource_group_name        = data.azurerm_resource_group.env.name
  location                   = local.location
  tenant_id                  = data.azurerm_client_config.current.tenant_id
  sku_name                   = "standard"
  rbac_authorization_enabled = true
  purge_protection_enabled   = true
  soft_delete_retention_days = 90
  tags                       = local.tags

  lifecycle {
    prevent_destroy = true
  }
}

data "azurerm_client_config" "current" {}
