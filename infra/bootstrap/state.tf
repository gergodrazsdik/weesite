# Remote state: a storage account with one container per state, and a Key
# Vault with one key per state for OpenTofu's client-side encryption.

resource "azurerm_resource_group" "state" {
  name     = local.state_resource_group_name
  location = var.location
  tags     = local.shared_tags
}

resource "azurerm_storage_account" "state" {
  # GitHub-hosted runners have no fixed addresses and there is no VNet;
  # access is Entra ID only (shared keys off) and the states are encrypted
  # client-side.
  #checkov:skip=CKV_AZURE_59:Public network access is needed by GitHub-hosted runners; Entra ID only.
  #checkov:skip=CKV2_AZURE_33:No VNet or private endpoints; see CKV_AZURE_59.
  #checkov:skip=CKV2_AZURE_1:States are encrypted client-side with Key Vault keys; infrastructure encryption is on.
  #checkov:skip=CKV_AZURE_33:No queues in this account (and azurerm 5.x dropped queue_properties).
  name                              = local.state_storage_account_name
  resource_group_name               = azurerm_resource_group.state.name
  location                          = azurerm_resource_group.state.location
  account_kind                      = "StorageV2"
  account_tier                      = "Standard"
  account_replication_type          = "GRS"
  min_tls_version                   = "TLS1_2"
  https_traffic_only_enabled        = true
  allow_nested_items_to_be_public   = false
  shared_access_key_enabled         = false
  default_to_oauth_authentication   = true
  infrastructure_encryption_enabled = true

  blob_properties {
    versioning_enabled = true

    delete_retention_policy {
      days = 30
    }

    container_delete_retention_policy {
      days = 30
    }
  }

  tags = local.shared_tags

  # Losing it loses the states or the means to decrypt them.
  lifecycle {
    prevent_destroy = true
  }
}

resource "azurerm_storage_container" "state" {
  #checkov:skip=CKV2_AZURE_21:No Log Analytics workspace in the bootstrap; versioning and soft delete cover recovery.
  for_each = local.states

  name                  = each.key
  storage_account_id    = azurerm_storage_account.state.id
  container_access_type = "private"

  # Losing it loses the states or the means to decrypt them.
  lifecycle {
    prevent_destroy = true
  }
}

resource "azurerm_key_vault" "state" {
  # Same reasoning as the storage account: GitHub-hosted runners need to
  # reach it, and every operation needs an Entra ID role on the key.
  #checkov:skip=CKV_AZURE_189:Public network access is needed by GitHub-hosted runners; RBAC only.
  #checkov:skip=CKV_AZURE_109:No firewall rules; see CKV_AZURE_189.
  #checkov:skip=CKV2_AZURE_32:No VNet or private endpoints; see CKV_AZURE_189.
  name                       = local.state_key_vault_name
  resource_group_name        = azurerm_resource_group.state.name
  location                   = azurerm_resource_group.state.location
  tenant_id                  = data.azurerm_client_config.current.tenant_id
  sku_name                   = "standard"
  rbac_authorization_enabled = true
  purge_protection_enabled   = true
  soft_delete_retention_days = 90
  tags                       = local.shared_tags

  # Losing it loses the states or the means to decrypt them.
  lifecycle {
    prevent_destroy = true
  }
}

# Whoever runs the bootstrap creates the keys and keeps the bootstrap's own
# state, which needs data-plane roles that subscription Owner doesn't have.
resource "azurerm_role_assignment" "runner_crypto_officer" {
  scope                = azurerm_key_vault.state.id
  role_definition_name = "Key Vault Crypto Officer"
  principal_id         = data.azurerm_client_config.current.object_id
  description          = "Bootstrap runner: create the state keys and encrypt the bootstrap state."
}

resource "azurerm_role_assignment" "runner_bootstrap_state" {
  scope                = azurerm_storage_container.state["bootstrap"].id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = data.azurerm_client_config.current.object_id
  description          = "Bootstrap runner: read and write the bootstrap state."
}

# New role assignments take a while to reach the Key Vault data plane.
resource "time_sleep" "rbac_propagation" {
  create_duration = "120s"

  depends_on = [azurerm_role_assignment.runner_crypto_officer]
}

# OpenTofu's azure_vault key provider encrypts a fresh data key per state
# file with this key (RSA-OAEP-256).
resource "azurerm_key_vault_key" "state" {
  #checkov:skip=CKV_AZURE_40:No expiry: a state encrypted under an expired key version could become unreadable (F-0012).
  #checkov:skip=CKV_AZURE_112:Software-protected keys (Standard SKU); HSM keys need Premium for no real gain here.
  for_each = local.states

  name         = each.key
  key_vault_id = azurerm_key_vault.state.id
  key_type     = "RSA"
  key_size     = 3072
  key_opts     = ["encrypt", "decrypt"]
  tags         = merge(local.tags, { environment = each.key == "bootstrap" ? "shared" : each.key })

  depends_on = [time_sleep.rbac_propagation]

  # Losing it loses the states or the means to decrypt them.
  lifecycle {
    prevent_destroy = true
  }
}
