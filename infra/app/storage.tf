# The site's data: posts, pages, media, settings and the search index in Blob; page views and
# the audit log in Table; re-index requests in Queue. Entra ID only, versioned, soft-deleted.

resource "azurerm_storage_account" "data" {
  # GitHub-hosted runners and the Container Apps environment (no VNet) reach it over the public
  # endpoint; every request needs an Entra ID role.
  #checkov:skip=CKV_AZURE_59:Public network access is needed without a VNet; Entra ID only.
  #checkov:skip=CKV2_AZURE_33:No VNet or private endpoints; see CKV_AZURE_59.
  #checkov:skip=CKV2_AZURE_1:Microsoft-managed keys with infrastructure encryption; customer-managed keys cost more for no gain here.
  #checkov:skip=CKV_AZURE_206:LRS keeps the cost down; versioning and soft delete cover recovery, backups come with T-0055.
  #checkov:skip=CKV_AZURE_33:Queue logging needs diagnostic settings; monitoring comes with T-0054.
  name                              = "st${local.workload}${var.environment}${local.suffix}"
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
  for_each = toset(["content", "media", "settings", "index", "dataprotection"])

  #checkov:skip=CKV2_AZURE_21:Blob read logging needs diagnostic settings; monitoring comes with T-0054.
  name                  = each.key
  storage_account_id    = azurerm_storage_account.data.id
  container_access_type = "private"

  lifecycle {
    prevent_destroy = true
  }
}

resource "azurerm_storage_table" "data" {
  for_each = toset(["pageviews", "audit", "editors", "oauthclients", "oauthgrants"])

  #checkov:skip=CKV2_AZURE_20:Table read logging needs diagnostic settings; monitoring comes with T-0054.
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

# Blob versioning keeps every earlier version of a post, page or settings file: the editor's
# revisions. Earlier versions go 90 days after they were made (Q-0007); the current version of
# every blob stays.
resource "azurerm_storage_management_policy" "data" {
  storage_account_id = azurerm_storage_account.data.id

  rule {
    name    = "delete-old-versions"
    enabled = true

    filters {
      blob_types = ["blockBlob"]
    }

    actions {
      version {
        delete_after_days_since_creation = 90
      }
    }
  }
}
