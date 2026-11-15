# Who reads the OpenTofu states and their keys (T-0122): the states hold the sign-in client secret,
# so every read of a state blob and every use of a state key is logged, into a workspace of the
# state resource group's own, kept 90 days. Only the workflows and the maintainer touch them, so
# the volume is small; the cap only guards against a runaway.

resource "azurerm_log_analytics_workspace" "state" {
  name                = "log-${local.prefix}-state"
  resource_group_name = azurerm_resource_group.state.name
  location            = azurerm_resource_group.state.location
  sku                 = "PerGB2018"
  retention_in_days   = 90
  daily_quota_gb      = 0.5
  tags                = local.shared_tags
}

resource "azurerm_monitor_diagnostic_setting" "state_key_vault" {
  name                       = "audit-to-log-analytics"
  target_resource_id         = azurerm_key_vault.state.id
  log_analytics_workspace_id = azurerm_log_analytics_workspace.state.id

  enabled_log {
    category = "AuditEvent"
  }
}

resource "azurerm_monitor_diagnostic_setting" "state_blobs" {
  name                       = "access-to-log-analytics"
  target_resource_id         = "${azurerm_storage_account.state.id}/blobServices/default"
  log_analytics_workspace_id = azurerm_log_analytics_workspace.state.id

  enabled_log {
    category = "StorageRead"
  }

  enabled_log {
    category = "StorageWrite"
  }

  enabled_log {
    category = "StorageDelete"
  }
}
