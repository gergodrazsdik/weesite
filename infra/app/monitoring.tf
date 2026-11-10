# Logs of the Container Apps environment and the app's telemetry (OpenTelemetry to Application
# Insights, T-0054). The daily cap keeps a runaway log volume from becoming a bill.

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

# The connection string the app exports its telemetry with, kept in the app's Key Vault: the Container
# App references it (containerapp.tf) through the site's identity, so it isn't stored in the app.
# No prevent_destroy: the value comes from the Application Insights resource and the next apply
# writes it again.
# tflint-ignore: azurerm_resources_missing_prevent_destroy
resource "azurerm_key_vault_secret" "appinsights" {
  #checkov:skip=CKV_AZURE_41:No expiry: the connection string changes only with the Application Insights resource.
  name         = "appinsights-connection-string"
  value        = azurerm_application_insights.web.connection_string
  key_vault_id = azurerm_key_vault.app.id
  content_type = "text/plain"
  tags         = local.tags
}
