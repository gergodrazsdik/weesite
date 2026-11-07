# Remote state: a storage account with one container per state, and a Key
# Vault with one key per state for OpenTofu's client-side encryption.

resource "azurerm_resource_group" "state" {
  name     = local.state_resource_group_name
  location = var.location
  tags     = local.shared_tags
}

resource "azurerm_storage_account" "state" {
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
