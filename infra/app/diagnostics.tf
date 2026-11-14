# Audit trails (T-0122), into the environment's Log Analytics workspace (monitoring.tf), kept
# 30 days like the rest of its logs:
# - the app Key Vault: every access to a secret or key (AuditEvent), which are few;
# - Blob, Table and Queue storage: writes and deletes, so a changed post, setting, key ring or
#   media file can be traced. Reads aren't logged: every page view and media request reads, so
#   their logs would cost in proportion to traffic.

resource "azurerm_monitor_diagnostic_setting" "key_vault" {
  name                       = "audit-to-log-analytics"
  target_resource_id         = azurerm_key_vault.app.id
  log_analytics_workspace_id = azurerm_log_analytics_workspace.logs.id

  enabled_log {
    category = "AuditEvent"
  }
}

resource "azurerm_monitor_diagnostic_setting" "storage" {
  for_each = toset(["blobServices", "tableServices", "queueServices"])

  name                       = "writes-to-log-analytics"
  target_resource_id         = "${azurerm_storage_account.data.id}/${each.key}/default"
  log_analytics_workspace_id = azurerm_log_analytics_workspace.logs.id

  enabled_log {
    category = "StorageWrite"
  }

  enabled_log {
    category = "StorageDelete"
  }
}
