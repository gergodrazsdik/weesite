# The site's resources in the environment's resource group (created by the bootstrap):
# identity.tf, storage.tf, keyvault.tf, monitoring.tf, containerapp.tf, dns.tf, domain.tf, and
# the sign-in registration in Entra ID (signin.tf).

provider "azurerm" {
  storage_use_azuread = true

  # The bootstrap registers the resource providers; the apply identities
  # can't, as they only reach their resource group.
  resource_provider_registrations = "none"

  features {}
}

# Entra ID: the same credentials as azurerm (ARM_* in CI, the Azure CLI locally).
provider "azuread" {}

# Azure Resource Manager directly, for what azurerm can't do (domain.tf); the same credentials.
provider "azapi" {
  # As for azurerm: the bootstrap registers the resource providers.
  skip_provider_registration = true
}

locals {
  workload = "weesite"
  prod     = var.environment == "prod"

  tags = {
    workload    = local.workload
    environment = var.environment
    managed-by  = "opentofu"
  }

  # Globally unique names (storage account, Key Vault) get a short, stable hash of the
  # resource group's ID and region, so a deployment needs no extra input for them. The region
  # is part of it because moving an environment re-creates its Key Vault, and a deleted vault's
  # name stays reserved while purge protection holds it (90 days).
  suffix   = substr(sha256("${data.azurerm_resource_group.env.id}|${data.azurerm_resource_group.env.location}"), 0, 6)
  location = data.azurerm_resource_group.env.location

  # The web replica's size follows the embedding model (ADR 0008): multilingual-e5-small is about
  # four times bge-small-en-v1.5 and doesn't fit 0.5 GiB beside the content, so it gets the next
  # size Container Apps offers.
  multilingual = lower(lookup(var.app_settings, "Search__EmbeddingModel", "")) == "multilingual-e5-small"
  web_size     = local.multilingual ? { cpu = 0.5, memory = "1Gi" } : { cpu = 0.25, memory = "0.5Gi" }
}

data "azurerm_resource_group" "env" {
  name = "rg-${local.workload}-${var.environment}"
}

output "resource_group_name" {
  description = "The environment's resource group."
  value       = data.azurerm_resource_group.env.name
}
