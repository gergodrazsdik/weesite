# Audit trails (T-0122), into a Log Analytics workspace of their own, kept 90 days:
# - the app Key Vault: every access to a secret or key (AuditEvent), which are few;
# - Blob, Table and Queue storage: writes and deletes, so a changed post, setting, key ring or
#   media file can be traced. Reads aren't logged: every page view and media request reads, so
#   their logs would cost in proportion to traffic.
# Their own workspace and cap, so a busy day of app logs and telemetry (monitoring.tf) can't stop
# them. Table write logs name each entity, editors' email addresses among them (accepted, T-0122).
#
# What anonymous requests cause isn't kept (F-0239, T-0177): every page view, reaction, comment,
# contact message, per-address limit and OAuth client registration (anyone may register a client,
# RFC 7591) is a table write, so their logs would follow traffic, and a busy day could fill the cap
# and cut off the rest of the day's trail. A workspace transformation drops the table logs of those
# tables, batches on them included, before they're stored. What stays: Key Vault access, every
# blob write and delete (posts, pages, settings, media, the key ring), the editor, OAuth grant and
# audit tables (a deleted audit log too), and re-index requests. Should the cap still be reached,
# an alert emails the subscription's Owners (audit_cap below).

resource "azurerm_log_analytics_workspace" "audit" {
  name                = "log-${local.workload}-${var.environment}-audit"
  resource_group_name = data.azurerm_resource_group.env.name
  location            = local.location
  sku                 = "PerGB2018"
  retention_in_days   = 90
  daily_quota_gb      = local.prod ? 1 : 0.2
  tags                = local.tags

  # Set by azapi_update_resource.audit_transforms below, as the rule refers to this workspace.
  lifecycle {
    ignore_changes = [data_collection_rule_id]
  }
}

resource "azurerm_monitor_data_collection_rule" "audit" {
  name                = "dcr-${local.workload}-${var.environment}-audit"
  resource_group_name = data.azurerm_resource_group.env.name
  location            = local.location
  kind                = "WorkspaceTransforms"
  description         = "Drops the table write logs of what anonymous requests cause (page views, reactions, comments, messages, limits, OAuth client registrations) from the audit workspace."
  tags                = local.tags

  destinations {
    log_analytics {
      name                  = "audit"
      workspace_resource_id = azurerm_log_analytics_workspace.audit.id
    }
  }

  data_flow {
    streams      = ["Microsoft-Table-StorageTableLogs"]
    destinations = ["audit"]
    # The table is the first segment of the request's path (<account>.table.core.windows.net/<table>(...))
    # and the second of the object key (/<account>/<table>/...). A batch's path is $batch, and its
    # key is its first entity's, so the key names the table there.
    transform_kql = <<-EOT
      source
      | extend UriTable = tolower(extract(@"^[a-z]+://[^/]+/([^/(?]+)", 1, Uri)), KeyTable = tolower(extract(@"^/[^/]+/([^/(]+)", 1, ObjectKey))
      | where not(UriTable in ("pageviews", "reactions", "reactionevents", "comments", "messages", "visitorlimits", "oauthclients"))
      | where not(KeyTable in ("pageviews", "reactions", "reactionevents", "comments", "messages", "visitorlimits", "oauthclients"))
      | project-away UriTable, KeyTable
    EOT
  }
}

# The workspace's transformation rule. azurerm's data_collection_rule_id on the workspace would
# make a cycle with the rule's destination, so azapi reads the workspace and merges this in.
resource "azapi_update_resource" "audit_transforms" {
  type        = "Microsoft.OperationalInsights/workspaces@2023-09-01"
  resource_id = azurerm_log_analytics_workspace.audit.id

  body = {
    properties = {
      defaultDataCollectionRuleResourceId = azurerm_monitor_data_collection_rule.audit.id
    }
  }
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

# The audit workspace stops ingesting for the rest of the UTC day once it reaches its daily cap,
# Key Vault access and blob writes included: that mustn't pass unnoticed (T-0177). Azure records
# the stop in the workspace's operation log, which the cap doesn't stop; this rule looks for it
# every 15 minutes and emails the subscription's Owners (availability.tf's action group).
resource "azurerm_monitor_scheduled_query_rules_alert_v2" "audit_cap" {
  name                 = "alert-${local.workload}-${var.environment}-audit-cap"
  resource_group_name  = data.azurerm_resource_group.env.name
  location             = local.location
  scopes               = [azurerm_log_analytics_workspace.audit.id]
  description          = "The audit workspace reached its daily cap: it stores nothing more until the next UTC day."
  severity             = 1
  evaluation_frequency = "PT15M"
  window_duration      = "PT15M"
  tags                 = local.tags

  criteria {
    query                   = <<-EOT
      _LogOperation
      | where Category =~ "Ingestion"
      | where Detail contains "OverQuota"
    EOT
    time_aggregation_method = "Count"
    operator                = "GreaterThan"
    threshold               = 0

    failing_periods {
      minimum_failing_periods_to_trigger_alert = 1
      number_of_evaluation_periods             = 1
    }
  }

  action {
    action_groups = [azurerm_monitor_action_group.owners.id]
  }
}
