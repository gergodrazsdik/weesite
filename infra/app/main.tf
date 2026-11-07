# The site's resources in the environment's resource group (created by the bootstrap):
# identity.tf, storage.tf, keyvault.tf, monitoring.tf, containerapp.tf, dns.tf.

provider "azurerm" {
  storage_use_azuread = true

  # The bootstrap registers the resource providers; the apply identities
  # can't, as they only reach their resource group.
  resource_provider_registrations = "none"

  features {}
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
}

data "azurerm_resource_group" "env" {
  name = "rg-${local.workload}-${var.environment}"
}

output "resource_group_name" {
  description = "The environment's resource group."
  value       = data.azurerm_resource_group.env.name
}
