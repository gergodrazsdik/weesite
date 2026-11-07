# Logs of the Container Apps environment and, from T-0054, the app's telemetry. The daily cap
# keeps a runaway log volume from becoming a bill.

resource "azurerm_log_analytics_workspace" "logs" {
  name                = "log-${local.workload}-${var.environment}"
  resource_group_name = data.azurerm_resource_group.env.name
  location            = local.location
  sku                 = "PerGB2018"
  retention_in_days   = 30
  daily_quota_gb      = local.prod ? 0.5 : 0.1
  tags                = local.tags
}

resource "azurerm_application_insights" "web" {
  name                = "appi-${local.workload}-${var.environment}"
  resource_group_name = data.azurerm_resource_group.env.name
  location            = local.location
  workspace_id        = azurerm_log_analytics_workspace.logs.id
  application_type    = "web"
  tags                = local.tags
}
