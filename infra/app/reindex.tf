# The re-index job (T-0069; brief, Bulk re-indexing): the web app's image started with
# `reindex`, which embeds every post and page again and stores the vectors in Blob. A message on
# the reindex queue ("Re-embed all" in the admin, or a replica that found many chunks without
# vectors) starts one run; the run takes every waiting message. Billed only while it runs.
#
# It uses the web app's identity, which already reads and writes the storage it needs (Blob and
# Queue); the queue trigger polls through the same identity, so no storage key is stored.
# Deploys change only the image (scripts/deploy-app.sh), which OpenTofu ignores.

resource "azurerm_container_app_job" "reindex" {
  name                         = "caj-${local.workload}-reindex"
  location                     = local.location
  resource_group_name          = data.azurerm_resource_group.env.name
  container_app_environment_id = azurerm_container_app_environment.main.id
  replica_timeout_in_seconds   = 1800
  replica_retry_limit          = 1
  tags                         = local.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.web.id]
  }

  event_trigger_config {
    parallelism              = 1
    replica_completion_count = 1

    scale {
      min_executions              = 0
      max_executions              = 1
      polling_interval_in_seconds = 60

      rules {
        name             = "reindex-queue"
        custom_rule_type = "azure-queue"
        identity_id      = azurerm_user_assigned_identity.web.id
        metadata = {
          accountName = azurerm_storage_account.data.name
          queueName   = azurerm_storage_queue.reindex.name
          queueLength = "1"
        }
      }
    }
  }

  template {
    container {
      name   = "reindex"
      image  = local.placeholder_image
      args   = ["reindex"]
      cpu    = 1
      memory = "2Gi"

      env {
        name  = "AZURE_CLIENT_ID"
        value = azurerm_user_assigned_identity.web.client_id
      }

      # Only the managed identity: DefaultAzureCredential doesn't try the credentials before it
      # (F-0107).
      env {
        name  = "AZURE_TOKEN_CREDENTIALS"
        value = "ManagedIdentityCredential"
      }

      env {
        name  = "ConnectionStrings__blobs"
        value = azurerm_storage_account.data.primary_blob_endpoint
      }

      env {
        name  = "ConnectionStrings__queues"
        value = azurerm_storage_account.data.primary_queue_endpoint
      }

      # The web app's search settings (var.app_settings), so the job embeds with the same model
      # (Search__EmbeddingModel, ADR 0008) and stores vectors the web app can use.
      dynamic "env" {
        for_each = { for name, value in var.app_settings : name => value if startswith(name, "Search__") }

        content {
          name  = env.key
          value = env.value
        }
      }
    }
  }

  lifecycle {
    ignore_changes = [template[0].container[0].image]
  }
}

output "reindex_job_name" {
  description = "Name of the re-index Container Apps job, for the deploy workflows."
  value       = azurerm_container_app_job.reindex.name
}
