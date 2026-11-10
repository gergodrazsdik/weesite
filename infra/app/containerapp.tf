# The web app on Azure Container Apps (consumption). prod keeps one replica warm; test scales
# to zero. Deploys (the app and promote workflows) change only the image, which OpenTofu
# ignores, so applies and deploys don't undo each other.

locals {
  # Until the first deploy: Microsoft's ASP.NET sample, also on port 8080.
  placeholder_image = "mcr.microsoft.com/dotnet/samples:aspnetapp-chiseled@sha256:a8856ff1ebfedb994b13bc22dcf999c98848b337e3b6c4a33130d33231356935"
}

resource "azurerm_container_app_environment" "main" {
  name                       = "cae-${local.workload}-${var.environment}"
  resource_group_name        = data.azurerm_resource_group.env.name
  location                   = local.location
  logs_destination           = "log-analytics"
  log_analytics_workspace_id = azurerm_log_analytics_workspace.logs.id
  tags                       = local.tags
}

resource "azurerm_container_app" "web" {
  name                         = "ca-${local.workload}-web"
  resource_group_name          = data.azurerm_resource_group.env.name
  container_app_environment_id = azurerm_container_app_environment.main.id
  revision_mode                = "Single"
  tags                         = local.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.web.id]
  }

  ingress {
    external_enabled           = true
    target_port                = 8080
    transport                  = "auto"
    allow_insecure_connections = false

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  # Key Vault references, resolved by Container Apps with the site's identity (no values stored here).
  secret {
    name                = "appinsights-connection-string"
    key_vault_secret_id = azurerm_key_vault_secret.appinsights.versionless_id
    identity            = azurerm_user_assigned_identity.web.id
  }

  template {
    min_replicas = local.prod ? 1 : 0
    max_replicas = 2

    container {
      name   = "web"
      image  = local.placeholder_image
      cpu    = 0.25
      memory = "0.5Gi"

      # The managed identity the app's Azure clients use.
      env {
        name  = "AZURE_CLIENT_ID"
        value = azurerm_user_assigned_identity.web.client_id
      }

      # Endpoints for the Aspire storage clients (the same names as in the AppHost).
      env {
        name  = "ConnectionStrings__blobs"
        value = azurerm_storage_account.data.primary_blob_endpoint
      }

      env {
        name  = "ConnectionStrings__tables"
        value = azurerm_storage_account.data.primary_table_endpoint
      }

      env {
        name  = "ConnectionStrings__queues"
        value = azurerm_storage_account.data.primary_queue_endpoint
      }

      # test loads the sample site from the image into its storage at startup (ADR 0002);
      # prod stays empty until the editor.
      dynamic "env" {
        for_each = local.prod ? [] : ["/app/seed"]

        content {
          name  = "Seed__Path"
          value = env.value
        }
      }

      # The app reads its secrets (sign-in clients) from its Key Vault at startup (ADR 0004).
      env {
        name  = "KeyVault__Uri"
        value = azurerm_key_vault.app.vault_uri
      }

      # Telemetry to Application Insights (T-0054).
      env {
        name        = "APPLICATIONINSIGHTS_CONNECTION_STRING"
        secret_name = "appinsights-connection-string"
      }

      # The Key Vault key that wraps the Data Protection keys (T-0078); versionless, so a new key
      # version is picked up without a change here.
      env {
        name  = "DataProtection__KeyId"
        value = azurerm_key_vault_key.dataprotection.versionless_id
      }

      # Not read by the app: a new Microsoft client secret changes it, which rolls the app to a
      # new revision that loads the new secret (signin.tf).
      env {
        name  = "KeyVault__SignInSecretVersion"
        value = azurerm_key_vault_secret.signin["Authentication--Microsoft--ClientSecret"].version
      }

      # test must stay out of search engines (Q-0004: open, but noindex).
      env {
        name  = "Site__NoIndex"
        value = local.prod ? "false" : "true"
      }

      # This environment's own settings (var.app_settings, from the repository variable
      # APP_SETTINGS_TEST or APP_SETTINGS_PROD).
      dynamic "env" {
        for_each = var.app_settings

        content {
          name  = env.key
          value = env.value
        }
      }

      # /alive answers once the process serves requests; /health only once the content and the
      # search index have loaded (T-0054). Startup and liveness use /alive, so a slow content load
      # never gets the replica restarted; readiness uses /health, so no traffic reaches a replica
      # before it can answer, and a new revision takes over only once it's ready.
      startup_probe {
        transport               = "HTTP"
        port                    = 8080
        path                    = "/alive"
        interval_seconds        = 3
        failure_count_threshold = 10
      }

      liveness_probe {
        transport               = "HTTP"
        port                    = 8080
        path                    = "/alive"
        interval_seconds        = 10
        failure_count_threshold = 3
      }

      readiness_probe {
        transport               = "HTTP"
        port                    = 8080
        path                    = "/health"
        interval_seconds        = 5
        failure_count_threshold = 3
        success_count_threshold = 1
      }
    }
  }

  lifecycle {
    ignore_changes = [template[0].container[0].image]
  }
}

output "web_app_name" {
  description = "Name of the web Container App, for the deploy workflows."
  value       = azurerm_container_app.web.name
}
