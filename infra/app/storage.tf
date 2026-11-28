# The site's data, Entra ID only, versioned, soft-deleted:
# - Blob: posts and pages (content), media, settings, the search index, the Data Protection key
#   ring, the IP to country database (geoip.tf) and staged site imports (imports);
# - Table: page views, the audit log, editors, the editor MCP's OAuth clients and grants, and what
#   visitors send: comments, contact messages, reactions (reactions, reactionevents) and the
#   per-address limits (visitorlimits);
# - Queue: re-index requests.

locals {
  storage_account_name = "st${local.workload}${var.environment}${local.suffix}"
  storage_account_id   = "${data.azurerm_resource_group.env.id}/providers/Microsoft.Storage/storageAccounts/${local.storage_account_name}"

  # The app creates these at runtime too (CreateIfNotExists), and did before they were listed
  # here (1.1), so an install from before then has them already: they're imported where they
  # exist, below.
  app_created_containers = ["imports", "uploads"]
  app_created_tables     = ["comments", "messages", "reactions", "reactionevents", "visitorlimits"]
}

resource "azurerm_storage_account" "data" {
  # GitHub-hosted runners and the Container Apps environment (no VNet) reach it over the public
  # endpoint; every request needs an Entra ID role.
  #checkov:skip=CKV_AZURE_59:Public network access is needed without a VNet; Entra ID only.
  #checkov:skip=CKV2_AZURE_33:No VNet or private endpoints; see CKV_AZURE_59.
  #checkov:skip=CKV2_AZURE_1:Microsoft-managed keys with infrastructure encryption; customer-managed keys cost more for no gain here.
  #checkov:skip=CKV_AZURE_206:LRS keeps the cost down; versioning and soft delete cover recovery, backups come with T-0055.
  #checkov:skip=CKV_AZURE_33:Writes and deletes are logged (diagnostics.tf); reads aren't, as their logs would cost in proportion to traffic (T-0122).
  name                              = local.storage_account_name
  resource_group_name               = data.azurerm_resource_group.env.name
  location                          = local.location
  account_kind                      = "StorageV2"
  account_tier                      = "Standard"
  account_replication_type          = "LRS"
  min_tls_version                   = "TLS1_2"
  https_traffic_only_enabled        = true
  allow_nested_items_to_be_public   = false
  shared_access_key_enabled         = false
  default_to_oauth_authentication   = true
  local_user_enabled                = false
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

  tags = local.tags

  # The site's content lives here.
  lifecycle {
    prevent_destroy = true
  }
}

resource "azurerm_storage_container" "data" {
  for_each = toset(concat(["content", "media", "settings", "index", "dataprotection", "geoip"], local.app_created_containers))

  #checkov:skip=CKV2_AZURE_21:Blob writes and deletes are logged (diagnostics.tf); reads aren't, as every page view reads (T-0122).
  name                  = each.key
  storage_account_id    = azurerm_storage_account.data.id
  container_access_type = "private"

  lifecycle {
    prevent_destroy = true
  }
}

resource "azurerm_storage_table" "data" {
  for_each = toset(concat(["pageviews", "audit", "editors", "oauthclients", "oauthgrants"], local.app_created_tables))

  #checkov:skip=CKV2_AZURE_20:Table writes and deletes are logged (diagnostics.tf); reads aren't, as their logs would cost in proportion to traffic (T-0122).
  name               = each.key
  storage_account_id = azurerm_storage_account.data.id

  lifecycle {
    prevent_destroy = true
  }
}

resource "azurerm_storage_queue" "reindex" {
  name               = "reindex"
  storage_account_id = azurerm_storage_account.data.id

  lifecycle {
    prevent_destroy = true
  }
}

# The containers and tables the app created before they were listed here: imported where they
# exist, so an older install takes them over instead of failing on "already exists"; a new install
# (no storage account yet when it's planned) creates them above. The lists are read in the plan
# with the plan identity's Reader role.
data "azapi_resource_list" "storage_accounts" {
  type                   = "Microsoft.Storage/storageAccounts@2023-05-01"
  parent_id              = data.azurerm_resource_group.env.id
  response_export_values = { names = "value[].name" }
}

data "azapi_resource_list" "existing_tables" {
  count = contains(try(tolist(data.azapi_resource_list.storage_accounts.output.names), []), local.storage_account_name) ? 1 : 0

  type                   = "Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01"
  parent_id              = "${local.storage_account_id}/tableServices/default"
  response_export_values = { names = "value[].name" }
}

data "azapi_resource_list" "existing_containers" {
  count = length(data.azapi_resource_list.existing_tables)

  type                   = "Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01"
  parent_id              = "${local.storage_account_id}/blobServices/default"
  response_export_values = { names = "value[].name" }
}

import {
  for_each = toset([for name in local.app_created_tables : name if contains(try(tolist(data.azapi_resource_list.existing_tables[0].output.names), []), name)])

  to = azurerm_storage_table.data[each.key]
  id = "${local.storage_account_id}/tableServices/default/tables/${each.key}"
}

import {
  for_each = toset([for name in local.app_created_containers : name if contains(try(tolist(data.azapi_resource_list.existing_containers[0].output.names), []), name)])

  to = azurerm_storage_container.data[each.key]
  id = "${local.storage_account_id}/blobServices/default/containers/${each.key}"
}

# Blob versioning keeps every earlier version of every blob. Posts, pages and settings files (the
# content and settings containers) are the editor's revisions: the app deletes their earlier
# versions itself, by Content:VersionsKept and Content:VersionDays (50 and 90: the newest 50 stay,
# older ones until 90 days after they were replaced; F-0240, Q-0007), because a lifecycle rule
# counts a version's age from when it was written, not from when it was replaced.
#
# This rule covers the other containers, whose earlier versions nothing in the app shows (a
# replaced or deleted media file, the index, the key ring, the IP database, staged imports): they
# go 90 days after they were written, which can be as soon as the next day for a file that was
# current for longer; blob soft delete keeps them 30 days more. The current version of every blob
# stays.
resource "azurerm_storage_management_policy" "data" {
  storage_account_id = azurerm_storage_account.data.id

  rule {
    name    = "delete-old-versions"
    enabled = true

    filters {
      blob_types   = ["blockBlob"]
      prefix_match = [for name in sort(keys(azurerm_storage_container.data)) : "${name}/" if !contains(["content", "settings"], name)]
    }

    actions {
      version {
        delete_after_days_since_creation = 90
      }
    }
  }
}
