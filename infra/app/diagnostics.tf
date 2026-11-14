# Audit trails (T-0122), into a Log Analytics workspace of their own, kept 90 days:
# - the app Key Vault: every access to a secret or key (AuditEvent), which are few;
# - Blob, Table and Queue storage: writes and deletes, so a changed post, setting, key ring or
#   media file can be traced. Reads aren't logged: every page view and media request reads, so
#   their logs would cost in proportion to traffic.
# Their own workspace and cap, so a busy day of app logs and telemetry (monitoring.tf) can't stop
# them. Table write logs name each entity, editors' email addresses among them (accepted, T-0122).

resource "azurerm_log_analytics_workspace" "audit" {
  name                = "log-${local.workload}-${var.environment}-audit"
  resource_group_name = data.azurerm_resource_group.env.name
  location            = local.location
  sku                 = "PerGB2018"
  retention_in_days   = 90
  daily_quota_gb      = local.prod ? 1 : 0.2
  tags                = local.tags
}

resource "azurerm_monitor_diagnostic_setting" "key_vault" {
  name                       = "audit-to-log-analytics"
  target_resource_id         = azurerm_key_vault.app.id
  log_analytics_workspace_id = azurerm_log_analytics_workspace.audit.id

  enabled_log {
    category = "AuditEvent"
  }
}

resource "azurerm_monitor_diagnostic_setting" "storage" {
  for_each = toset(["blobServices", "tableServices", "queueServices"])

  name                       = "writes-to-log-analytics"
  target_resource_id         = "${azurerm_storage_account.data.id}/${each.key}/default"
  log_analytics_workspace_id = azurerm_log_analytics_workspace.audit.id

  enabled_log {
    category = "StorageWrite"
  }

  enabled_log {
    category = "StorageDelete"
  }
}
